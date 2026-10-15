using OpsLookup.NetCorePythonNetApp.Services;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------- services
builder.Services.AddSingleton<PythonBridge>();

// Talks to the data API to exchange the shared secret for a bearer token. A
// fresh HttpClient per process is fine here: one call per turn, and the handler
// pool is sized for that.
builder.Services.AddHttpClient<ServiceTokenClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

var lookupApiBaseUrl = (builder.Configuration["Lookup:BaseUrl"] ?? string.Empty).TrimEnd('/');
if (string.IsNullOrWhiteSpace(lookupApiBaseUrl))
{
    // Loud, and at startup rather than on the first ticket.
    Console.Error.WriteLine(
        "Lookup:BaseUrl is not configured. The agent host needs to know where the "
        + "data API is. Set Lookup__BaseUrl (e.g. http://localhost:5250).");
}

// Starts the embedded interpreter and builds the graph before the first request.
builder.Services.AddHostedService<PythonWarmup>();

var app = builder.Build();

// ------------------------------------------------------------------- pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/api/error");
    app.UseHsts();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", async (PythonBridge bridge) => Results.Ok(new
{
    status = bridge.IsReady ? "ok" : "starting",
    pythonReady = bridge.IsReady,
    apiReachable = await bridge.ApiReachableAsync(),
    startupError = bridge.StartupError,
}));

app.MapPost("/api/chat", async (
    ChatRequest request,
    PythonBridge bridge,
    ServiceTokenClient tokens,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    if (!bridge.IsReady)
    {
        return Results.Json(
            new { detail = bridge.StartupError ?? "The agent is still starting." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // A non-nullable string on the record does not make ASP.NET enforce it.
    // Without this check a body that omits `ticketId` or `message` deserialises
    // to null, reaches the bridge, and surfaces as a 500 "unexpected error" --
    // telling the caller the server broke when in fact the request was malformed.
    if (string.IsNullOrWhiteSpace(request.TicketId) || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.Json(
            new { detail = "Both 'ticketId' and 'message' are required." },
            statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        // The token is minted here, per turn, and handed to Python. The agent
        // never sees the shared secret and cannot obtain credentials itself.
        var apiToken = await tokens.GetTokenAsync(cancellationToken);

        var outcome = await bridge.ChatAsync(
            request.TicketId,
            request.Message,
            request.OperatorId,
            request.Locale,
            lookupApiBaseUrl,
            apiToken);

        return outcome.Error
            ? Results.Json(outcome, statusCode: StatusCodes.Status502BadGateway)
            : Results.Ok(outcome);
    }
    catch (OperationCanceledException)
    {
        return Results.Json(
            new { detail = "The request was cancelled." },
            statusCode: StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "chat failed for {TicketId}", request.TicketId);
        return Results.Json(
            new { detail = "The agent hit an unexpected error." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapFallbackToFile("index.html");

app.Run();

public sealed record ChatRequest(
    string TicketId,
    string Message,
    string? OperatorId,
    /// <summary>
    /// Page language: "ar" or "en". Selects the agent's system prompt so the
    /// answer comes back in the language the member is reading. Null or
    /// unrecognised values fall back to Arabic rather than failing the turn.
    /// </summary>
    string? Locale);

/// <summary>Initialises Python and blocks until the graph is ready.</summary>
internal sealed class PythonWarmup(PythonBridge bridge) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await bridge.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Do not crash the host: /api/health reports the failure and the page
            // still loads, so the problem is visible rather than a dead port.
            bridge.RecordStartupError(ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}