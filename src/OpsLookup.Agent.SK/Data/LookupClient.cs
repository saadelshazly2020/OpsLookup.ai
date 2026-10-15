using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpsLookup.Agent.Data;

/// <summary>Raised when the lookup API cannot be reached or refuses the call.</summary>
public sealed class LookupApiException : Exception
{
    public LookupApiException(string message) : base(message) { }
    public LookupApiException(string message, Exception inner) : base(message, inner) { }
}

/// <remarks>
/// The numeric types are deliberate. The data API serialises these as JSON
/// numbers, and declaring them as strings makes deserialisation fail outright --
/// which surfaced as "the lookup API returned malformed JSON" for every lookup.
/// The agent then told the member the service had an error, when in fact the
/// shape here was simply wrong.
/// </remarks>
public sealed record ContractRow(
    [property: JsonPropertyName("contractCode")] long ContractCode,
    [property: JsonPropertyName("socialId")] long SocialId,
    [property: JsonPropertyName("partyName")] string PartyName,
    [property: JsonPropertyName("contractName")] string ContractName);

public sealed record AttorneyRow(
    [property: JsonPropertyName("attorneyId")] int AttorneyId,
    [property: JsonPropertyName("attorneyName")] string AttorneyName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("partyId")] int PartyId,
    [property: JsonPropertyName("partyName")] string PartyName,
    [property: JsonPropertyName("socialId")] long SocialId);

/// <summary>
/// Typed client for the data API.
/// </summary>
/// <remarks>
/// <para>
/// Holds no credential. A bearer token is minted per turn by
/// <see cref="Services.ServiceTokenClient"/> and passed in, because an agent
/// that can obtain its own tokens is an agent that can hold them indefinitely.
/// The Python version had the same arrangement and it is worth preserving in the
/// port: the property is a per-turn parameter, never a field.
/// </para>
/// <para>
/// Replaces the Python httpx client. Being in-process also removes the
/// <c>localhost</c> versus <c>127.0.0.1</c> trap -- that one existed because
/// httpx would not fall back to IPv4 when the name resolved to ::1, and
/// HttpClient on the loopback interface does not have the problem.
/// </para>
/// </remarks>
public sealed class LookupClient(HttpClient http, ILogger<LookupClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True when the API answers its health endpoint.</summary>
    public async Task<bool> IsReachableAsync(string bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            ApplyAuth(request, bearerToken);
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Lookup API unreachable: {Message}", ex.Message);
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ContractRow>> GetContractsAsync(
        long socialId, string? name, string bearerToken, CancellationToken ct = default)
    {
        var query = $"?socialId={socialId.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(name))
        {
            query += "&name=" + Uri.EscapeDataString(name);
        }

        return await GetAsync<List<ContractRow>>($"/api/contracts{query}", bearerToken, ct);
    }

    public async Task<IReadOnlyList<AttorneyRow>> GetAttorneysAsync(
        long? socialId, string? partyName, string bearerToken, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (socialId is not null)
        {
            parts.Add("socialId=" + socialId.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(partyName))
        {
            parts.Add("partyName=" + Uri.EscapeDataString(partyName));
        }

        var query = parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
        return await GetAsync<List<AttorneyRow>>($"/api/attorneys{query}", bearerToken, ct);
    }

    private async Task<T> GetAsync<T>(string path, string bearerToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            ApplyAuth(request, bearerToken);

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Worth naming distinctly: this is a configuration fault, not a
                // data fault, and the two produce very different things to go fix.
                throw new LookupApiException(
                    "The lookup API rejected the service token. Check Jwt:ServiceSecret matches the API's.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LookupApiException(
                    $"The lookup API returned {(int)response.StatusCode} for {path}.");
            }

            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return value ?? throw new LookupApiException("The lookup API returned an empty body.");
        }
        catch (JsonException ex)
        {
            throw new LookupApiException("The lookup API returned malformed JSON.", ex);
        }
        catch (HttpRequestException ex)
        {
            // The message matters. The earlier build reported "all connection
            // attempts failed" for what was really one cause, and the single most
            // common instance of that is a base URL pointing at the wrong host.
            throw new LookupApiException(
                $"Could not reach the lookup API at {http.BaseAddress}: {ex.Message}", ex);
        }
    }

    private void ApplyAuth(HttpRequestMessage request, string bearerToken)
    {
        // Same reason the Python client stopped sending an empty bearer: a header
        // with no value is malformed and the transport rejects it, which then
        // presents as "the API is unreachable".
        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }
}