using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Endpoints;

/// <summary>
/// Issues a bearer token for the agent host, which is a machine rather than a
/// person and so has no interactive login.
/// </summary>
/// <remarks>
/// <para>
/// This replaces minting a long-lived token once and storing it in the agent's
/// <c>.env</c>. That arrangement died every sixty minutes and left a valid
/// credential sitting in a file. Here the host requests a token per turn, so the
/// token's lifetime stops being an operational concern and the secret never
/// reaches Python.
/// </para>
/// <para>
/// The caller proves who it is with a shared secret in <c>X-Service-Secret</c>.
/// That is appropriate on a private network. Anything internet-facing should
/// move to a real identity provider — a shared secret that cannot be scoped,
/// rotated or revoked per client is not a substitute for OIDC.
/// </para>
/// </remarks>
public static class ServiceTokenEndpoints
{
    public const string SecretHeader = "X-Service-Secret";

    /// <summary>Upper bound on the configurable lifetime, in minutes.</summary>
    private const int MaxMinutes = 1440;

    public static IEndpointRouteBuilder MapServiceTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/service-token", IssueServiceToken)
            .AllowAnonymous()
            .WithName("ServiceToken")
            .WithSummary("Exchange the service secret for a bearer token.")
            .WithDescription(
                "For the agent host only. Send the shared secret in the "
                + $"`{SecretHeader}` header to receive a short-lived token. Returns 401 "
                + "when the secret is missing or wrong.")
            .Produces<DevTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static IResult IssueServiceToken(
        IOptions<JwtOptions> jwtOptions,
        [FromHeader(Name = SecretHeader)] string? secret)
    {
        var options = jwtOptions.Value;

        // Fail closed. An unconfigured secret is a deployment mistake, and the
        // safe reading of it is "nobody is allowed in".
        if (string.IsNullOrWhiteSpace(options.ServiceSecret))
        {
            return Results.Problem(new ProblemDetails
            {
                Title = "Service tokens are not configured",
                Detail = $"Set Jwt:ServiceSecret (via the {SecretHeader} provider) "
                         + "before requesting a service token.",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        if (!SecretsMatch(options.ServiceSecret, secret))
        {
            return Results.Problem(new ProblemDetails
            {
                Title = "Invalid service secret",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        // Clamped rather than trusted: a caller should not be able to ask for a
        // token that outlives the policy set by whoever deployed this.
        var minutes = Math.Clamp(options.ServiceTokenMinutes, 1, MaxMinutes);

        var token = JwtTokenFactory.Create(options, options.ServiceSubject, TimeSpan.FromMinutes(minutes));

        return Results.Ok(new DevTokenResponse(
            AccessToken: token,
            TokenType: "Bearer",
            ExpiresInSeconds: minutes * 60));
    }

    /// <summary>
    /// Compares in fixed time so a caller cannot discover the secret by timing
    /// the response.
    /// </summary>
    private static bool SecretsMatch(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(supplied);

        // Length is not secret, but comparing different lengths with the fixed
        // time helper would throw, so treat that as a mismatch first.
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}