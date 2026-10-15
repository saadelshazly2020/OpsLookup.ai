namespace OpsLookup.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Symmetric signing key. Must be at least 32 characters for HMAC-SHA256.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret proving a caller is the agent host rather than an anonymous
    /// client. Deliberately separate from <see cref="SigningKey" />: reusing the
    /// signing key would mean anything able to request a token can also forge one.
    /// </summary>
    /// <remarks>
    /// Blank by default, which makes <c>POST /auth/service-token</c> return 401
    /// rather than handing out tokens. Supply it through an environment variable
    /// (<c>Jwt__ServiceSecret</c>) so it never lands in a file under the web root.
    /// </remarks>
    public string ServiceSecret { get; set; } = string.Empty;

    /// <summary>
    /// How long a service token stays valid, in minutes. Clamped to a sane range
    /// by the endpoint.
    /// </summary>
    public int ServiceTokenMinutes { get; set; } = 720;

    /// <summary>
    /// Subject claim recorded on service tokens. Taken from configuration, never
    /// from the request — a machine identity should not choose who it is.
    /// </summary>
    public string ServiceSubject { get; set; } = "ticketing-agent";
}