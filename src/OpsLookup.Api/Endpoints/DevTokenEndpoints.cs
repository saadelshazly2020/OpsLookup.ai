using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Endpoints;

/// <summary>
/// Issues a signed token so the API can be exercised without a real identity provider.
/// Registered only when the environment is Development.
/// </summary>
/// <remarks>
/// Anonymous, and the caller chooses both the subject and the lifetime, so it is
/// mapped inside an <c>IsDevelopment()</c> check in <c>Program.cs</c>. Do not move
/// that call out: an open dev-token endpoint is a production data breach.
/// </remarks>
public static class DevTokenEndpoints
{
    /// <summary>
    /// Upper bound on the requested lifetime. The `minutes` parameter is
    /// caller-supplied and was previously unbounded, which meant a token valid
    /// for years could be minted from a query string.
    /// </summary>
    private const int MaxMinutes = 480;

    public static IEndpointRouteBuilder MapDevTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/dev-token", IssueToken)
            .AllowAnonymous()
            .WithName("DevToken")
            .WithSummary("Development only. Returns a bearer token for local testing.")
            .WithDescription(
                "Issues a signed JWT so the API can be exercised without a real identity "
                + "provider. Copy `accessToken` from the response, then use the "
                + "**Authorize** button in Swagger UI to paste it.")
            .Produces<DevTokenResponse>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    private static IResult IssueToken(
        IOptions<JwtOptions> jwtOptions,
        [FromQuery] string? operatorId,
        [FromQuery] int minutes = 60)
    {
        var options = jwtOptions.Value;

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            return Results.Problem(new ProblemDetails
            {
                Title = "Signing key not configured",
                Detail = "Set Jwt:SigningKey before requesting a dev token.",
                Status = StatusCodes.Status500InternalServerError,
            });
        }

        // Clamped, not validated: a caller asking for 100000 minutes gets the
        // maximum rather than an error, which keeps local use frictionless.
        var lifetime = Math.Clamp(minutes, 1, MaxMinutes);

        var accessToken = JwtTokenFactory.Create(options, operatorId ?? "dev-operator", TimeSpan.FromMinutes(lifetime));

        return Results.Ok(new DevTokenResponse(
            AccessToken: accessToken,
            TokenType: "Bearer",
            ExpiresInSeconds: lifetime * 60));
    }
}