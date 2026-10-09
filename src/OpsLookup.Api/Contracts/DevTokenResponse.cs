namespace OpsLookup.Api.Contracts;

/// <summary>
/// Response shape for POST /auth/dev-token
/// </summary>
public sealed record DevTokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds);