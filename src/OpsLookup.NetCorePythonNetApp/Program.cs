using OpsLookup.NetCorePythonNetApp.Services;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------- services
builder.Services.AddSingleton<PythonBridge>();

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
    CancellationToken cancellationToken) =>
{
    if (!bridge.IsReady)
    {
        return Results.Json(
            new { detail = bridge.StartupError ?? "The agent is still starting." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var outcome = await bridge.ChatAsync(
            request.TicketId,
            request.Message,
            request.OperatorId);

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
    string? OperatorId);

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