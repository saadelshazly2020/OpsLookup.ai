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
}