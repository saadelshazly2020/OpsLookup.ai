using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OpsLookup.Api.Security;

/// <summary>
/// Builds signed tokens for both token endpoints.
/// </summary>
/// <remarks>
/// Two endpoints issue tokens and their signing rules must not drift apart, so
/// the construction lives here once. The caller decides the subject and the
/// lifetime; nothing here reads request state.
/// </remarks>
public static class JwtTokenFactory
{
    /// <summary>
    /// Signs a token for <paramref name="subject" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The signing key is missing or too short for HMAC-SHA256. Surfaces as a 500
    /// because it is a server misconfiguration, not a bad request.
    /// </exception>
    public static string Create(JwtOptions options, string subject, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            throw new InvalidOperationException("Jwt:SigningKey is not configured.");
        }

        // HMAC-SHA256 needs >= 32 bytes of key material. A short key throws deep
        // inside the token handler with an opaque message; catching it here keeps
        // the failure legible.
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 characters for HMAC-SHA256.");
        }

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            // A unique id per token, so one can be identified in logs later.
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
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}