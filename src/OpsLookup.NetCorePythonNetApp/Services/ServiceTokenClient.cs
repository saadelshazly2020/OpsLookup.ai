using System.Net;
using System.Text.Json;

namespace OpsLookup.NetCorePythonNetApp.Services;

/// <summary>
/// Obtains a bearer token for the data API on behalf of the agent.
/// </summary>
/// <remarks>
/// <para>
/// The token is minted per turn rather than cached, and that is deliberate.
/// Previously the agent carried one long-lived token in its <c>.env</c>, which
/// expired after sixty minutes and left a usable credential sitting in a file
/// under the web root. One extra localhost HTTP call against a fifteen-second
/// model turn costs nothing, and it removes every staleness question a cache
/// would raise.
/// </para>
/// <para>
/// The shared secret stays here, in .NET configuration, and is never passed to
/// Python. The agent only ever sees the short-lived token string, so a bug or a
/// prompt injection inside the model cannot mint a fresh supply of API access.
/// </para>
/// </remarks>
public sealed class ServiceTokenClient(
    HttpClient http,
    IConfiguration configuration,
    ILogger<ServiceTokenClient> logger)
{
    public const string SecretHeader = "X-Service-Secret";

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = (configuration["Lookup:BaseUrl"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Lookup:BaseUrl is not configured. Set it to the data API root, "
                + "e.g. http://localhost:5250.");
        }

        var secret = configuration["Jwt:ServiceSecret"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Jwt:ServiceSecret is not configured. Set it as the Jwt__ServiceSecret "
                + "environment variable. The data API rejects service tokens without "
                + "it, and refuses to issue any while it is blank.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/auth/service-token");
        request.Headers.TryAddWithoutValidation(SecretHeader, secret);

        using var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                "The data API rejected the service secret. Check that Jwt:ServiceSecret "
                + "here matches the one configured on the API, and that the API has "
                + "finished starting.");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("accessToken", out var token)
            || string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new InvalidOperationException(
                "The data API returned no accessToken from /auth/service-token.");
        }

        var value = token.GetString()!;
        logger.LogDebug("service token acquired from {BaseUrl}", baseUrl);
        return value;
    }
}