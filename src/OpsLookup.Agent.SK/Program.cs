using System.Net;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpsLookup.Agent.Agents;
using OpsLookup.Agent.Data;
using OpsLookup.Agent.Services;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------ services
var modelId = builder.Configuration["Model:Id"] ?? "longcat-2.5-preview-free";
var apiKey = builder.Configuration["Model:ApiKey"];
var baseUrl = builder.Configuration["Model:BaseUrl"]
    ?? throw new InvalidOperationException("Model:BaseUrl is not configured.");
var lookupBase = builder.Configuration["Lookup:BaseUrl"]
    ?? throw new InvalidOperationException("Lookup:BaseUrl is not configured.");

// A missing API key is not fatal at startup. It is an operator mistake they can
// fix without a rebuild, and a turn without it fails with a message that names
// it -- a crash loop would say less and block the health endpoint that would
// have explained it.
builder.Services.AddSingleton<IChatCompletionService>(_ =>
{
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return new UnconfiguredChatService();
    }

    // The Zen gateway rejects a request that does not identify itself, with
    // MissingSessionID. A generic OpenAI client sends neither header, so both
    // are attached explicitly here. The session id is stable for the process
    // lifetime: the gateway uses it for routing and prompt caching, and a fresh
    // id per request would defeat the cache entirely.
    var isZenGateway = baseUrl.Contains("opencode.ai/zen", StringComparison.OrdinalIgnoreCase);
    var sessionId = builder.Configuration["Model:SessionId"]
        ?? $"opslookup-{Guid.NewGuid():N}"[..25];

    var handler = new HttpClientHandler();
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        builder.Configuration["Model:UserAgent"] ?? "OpsLookup.Agent/1.0");

    if (isZenGateway)
    {
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-opencode-session", sessionId);
    }

    return new OpenAIChatCompletionService(
        modelId: modelId,
        endpoint: new Uri(baseUrl.TrimEnd('/') + "/"),
        apiKey: apiKey,
        httpClient: http);
});

builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<TicketAgent>();
builder.Services.AddSingleton<ServiceTokenClient>();

builder.Services.AddHttpClient<LookupClient>(client =>
{
    client.BaseAddress = new Uri(lookupBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<ServiceTokenClient>(client =>
{
    client.BaseAddress = new Uri(lookupBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
});

// Reachability probe for /api/health. Separate from LookupClient because /health
// is anonymous: probing it must not need a minted token, or reporting
// "unreachable" would conflate a bad secret with a down API.
builder.Services.AddHttpClient("healthProbe", client =>
{
    client.BaseAddress = new Uri(lookupBase.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ------------------------------------------------------------------ endpoints
app.MapGet("/api/health", async (IHttpClientFactory factory, CancellationToken ct) =>
{
    // `ready` replaces the Python agent's `pythonReady`, because there is no
    // interpreter to warm up -- the agent is usable the moment the process is.
    // The distinction the old flag carried (up but useless) is now covered by
    // apiReachable, which is a real probe rather than a remembered flag.
    var reachable = false;
    try
    {
        using var probe = factory.CreateClient("healthProbe");
        using var response = await probe.GetAsync("health", ct);
        reachable = response.IsSuccessStatusCode;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        reachable = false;
    }

    return Results.Ok(new
    {
        status = "ok",
        ready = true,
        agent = "semantic-kernel",
        model = modelId,
        apiBase = lookupBase,
        apiReachable = reachable,
        modelConfigured = !string.IsNullOrWhiteSpace(apiKey),
    });
});

app.MapPost("/api/chat", async (
    ChatRequest request,
    TicketAgent agent,
    ServiceTokenClient tokens,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.TicketId) || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "ticketId and message are both required." });
    }

    try
    {
        // Minted here, per turn, and handed in. The agent cannot obtain one, and
        // nothing here caches it -- a cached token reintroduces the expiry cliff
        // this arrangement was built to remove.
        var token = await tokens.MintAsync(ct);

        var result = await agent.RunAsync(
            new TurnRequest(request.TicketId, request.Message, request.Locale),
            token.AccessToken,
            ct);

        return Results.Ok(new
        {
            threadId = result.ThreadId,
            reply = result.Reply,
            toolCalls = result.ToolCalls,
            lookupFailed = result.LookupFailed,
            error = result.Error,
        });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch (Exception ex)
    {
        // Logged in full, returned in summary. The model never sees this -- it
        // would try to explain a configuration fault as though it were data.
        app.Logger.LogError(ex, "Chat turn failed for thread {Thread}", request.TicketId);
        return Results.Json(
            new { error = "The assistant is temporarily unavailable. Please try again." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

// Backs the UI's "new conversation" button. The Python agent keyed history off a
// fresh ticket id instead, so this is additive rather than a port.
app.MapPost("/api/chat/{ticketId}/clear", async (
    string ticketId, ConversationStore store, CancellationToken ct) =>
{
    await store.ClearAsync(ticketId, ct);
    return Results.Ok(new { cleared = ticketId });
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed record ChatRequest(string? TicketId, string? Message, string? Locale);

/// <summary>
/// Stands in when no model key is configured, so the process still starts and
/// /api/health still answers.
/// </summary>
/// <remarks>
/// Not an exception at startup: a crash loop gives an operator less to work with
/// than a running service that reports exactly what is missing.
/// </remarks>
internal sealed class UnconfiguredChatService : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?>();

    public Task<IReadOnlyList<Microsoft.SemanticKernel.ChatMessageContent>> GetChatMessageContentsAsync(
        Microsoft.SemanticKernel.ChatCompletion.ChatHistory chat,
        Microsoft.SemanticKernel.PromptExecutionSettings? executionSettings = null,
        Microsoft.SemanticKernel.Kernel? kernel = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No model API key is configured. Set Model__ApiKey as an environment variable.");

    public IAsyncEnumerable<Microsoft.SemanticKernel.StreamingChatMessageContent>
        GetStreamingChatMessageContentsAsync(
            Microsoft.SemanticKernel.ChatCompletion.ChatHistory chat,
            Microsoft.SemanticKernel.PromptExecutionSettings? executionSettings = null,
            Microsoft.SemanticKernel.Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No model API key is configured. Set Model__ApiKey as an environment variable.");
}

/// <summary>Exposed so integration tests can reference the entry-point assembly.</summary>
public partial class Program;