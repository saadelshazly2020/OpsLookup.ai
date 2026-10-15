using System.Net.Http.Headers;
using System.Text.Json;

namespace OpsLookup.Agent.Services;

public sealed record ServiceToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Exchanges the shared secret for a bearer token, once per turn.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately uncached. A cached token reintroduces the exact expiry cliff
/// this replaced: the previous arrangement refreshed on a timer, so a token that
/// expired between refreshes produced a turn that failed for no visible reason.
/// One mint per turn costs one extra HTTP call to the data API, which is
/// negligible against a language model round trip.
/// </para>
/// <para>
/// <see cref="TicketAgent"/> then holds the token for the duration of a single
/// turn and drops it. Nothing in this process can obtain a token on its own
/// without the secret, and the secret is not handed to the model.
/// </para>
/// </remarks>
public sealed class ServiceTokenClient(HttpClient http, IConfiguration configuration)
{
    private readonly string _secret = configuration["Jwt:ServiceSecret"] ?? string.Empty;

    public async Task<ServiceToken> MintAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_secret))
        {
            throw new InvalidOperationException(
                "Jwt:ServiceSecret is not configured. Without it POST /auth/service-token "
                + "answers 401 and no turn can reach the data API. Fail closed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/service-token");
        // The secret goes in a header rather than the body so it does not end up
        // in request-body logs.
        request.Headers.TryAddWithoutValidation("X-Service-Secret", _secret);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The data API refused to mint a service token (HTTP {(int)response.StatusCode}). "
                + "Check Jwt:ServiceSecret matches the API's.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var token = payload.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("The data API returned no accessToken.");

        var expiresIn = payload.TryGetProperty("expiresInSeconds", out var seconds)
            ? TimeSpan.FromSeconds(seconds.GetInt64())
            : TimeSpan.FromHours(12);

        return new ServiceToken(token, DateTimeOffset.UtcNow.Add(expiresIn));
    }
}