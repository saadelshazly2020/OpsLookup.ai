using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Endpoints;

/// <summary>
/// Issues a signed token so the API can be exercised without a real identity provider.
/// Registered only when the environment is Development.
/// </summary>
public static class DevTokenEndpoints
{
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

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, operatorId ?? "dev-operator"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        return Results.Ok(new DevTokenResponse(
            AccessToken: accessToken,
            TokenType: "Bearer",
            ExpiresInSeconds: minutes * 60));
    }
}