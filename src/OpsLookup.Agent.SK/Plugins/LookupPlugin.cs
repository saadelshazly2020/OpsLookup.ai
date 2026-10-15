using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using OpsLookup.Agent.Data;

namespace OpsLookup.Agent.Plugins;

/// <summary>
/// The two data tools.
/// </summary>
/// <remarks>
/// <para>
/// Neither accepts SQL, a table name, or a free-text filter. The data API
/// resolves the query; this side only sends arguments. That boundary is the
/// reason the agent cannot be talked into reading arbitrary rows.
/// </para>
/// <para>
/// Failures come back as <c>{"error": ...}</c> text rather than exceptions, so
/// the model explains what went wrong to the member instead of the turn
/// collapsing. It also means a failure has to be read back out of the result to
/// set <c>lookupFailed</c>: a tool-call count cannot stand in for it, because
/// that counts invocations, not successes.
/// </para>
/// <para>
/// The bearer token is a constructor field and the instance is built per turn.
/// That is deliberate. A shared instance would need ambient state to carry a
/// per-turn token, and ambient state is exactly how two concurrent turns on
/// different tickets end up using each other's credentials.
/// </para>
/// </remarks>
public sealed partial class LookupPlugin(LookupClient client, string bearerToken)
{
    // Mirrors the data API's own rule. Duplicated rather than shared: this
    // project does not reference the API, and validating here saves a round trip
    // that would only come back as a 400 the model has to interpret.
    [GeneratedRegex(@"^[12]\d{9}$")]
    private static partial Regex SocialIdPattern();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [KernelFunction, Description(
        "Look up contract records for a party by their national unified ID. "
        + "Use this whenever a member asks what contracts a party holds. "
        + "Returns contract code, national unified ID, party name and contract name.")]
    public async Task<string> GetContractAsync(
        [Description("The party's national unified ID. Must be exactly 10 digits starting with 1 or 2. "
            + "Do not guess or invent this value; ask the operations member for it if it was not provided.")]
        string socialId,
        [Description("Optional party name filter to narrow the results, for example 'Nasser'. "
            + "Omit to return all contracts for the national unified ID.")]
        string? name = null,
        CancellationToken ct = default)
    {
        if (!TryParseSocialId(socialId, out var id, out var problem))
        {
            return Error(problem);
        }

        try
        {
            var rows = await client.GetContractsAsync(id, name, bearerToken, ct);
            return JsonSerializer.Serialize(new { socialId, results = rows }, Json);
        }
        catch (LookupApiException ex)
        {
            return Error(ex.Message);
        }
    }

    [KernelFunction, Description(
        "Look up the agency acting for a party, and whether its status is approved. "
        + "Search by the party's national unified ID, party name, or both. At least one is required. "
        + "Use this whenever a member asks who represents a party, or whether an agency is approved or flagged.")]
    public async Task<string> GetAttorneyAsync(
        [Description("The party's national unified ID. Must be exactly 10 digits starting with 1 or 2.")]
        string? socialId = null,
        [Description("Optional party name to search by, for example 'Karimi'. At least one of "
            + "socialId or partyName is required.")]
        string? partyName = null,
        CancellationToken ct = default)
    {
        long? id = null;
        if (!string.IsNullOrWhiteSpace(socialId))
        {
            if (!TryParseSocialId(socialId, out var parsed, out var problem))
            {
                return Error(problem);
            }
            id = parsed;
        }

        if (id is null && string.IsNullOrWhiteSpace(partyName))
        {
            return Error("Provide a national unified ID, a party name, or both.");
        }

        try
        {
            var rows = await client.GetAttorneysAsync(id, partyName, bearerToken, ct);
            return JsonSerializer.Serialize(new { results = rows }, Json);
        }
        catch (LookupApiException ex)
        {
            return Error(ex.Message);
        }
    }

    private static bool TryParseSocialId(string? raw, out long value, out string problem)
    {
        value = 0;
        var trimmed = (raw ?? string.Empty).Trim();

        // Tolerant of what an operator actually types. A member pasting a number
        // with spaces or a trailing ".0" should get a lookup, not an error, as
        // long as the digits themselves are well-formed.
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());

        if (digits.Length == 0)
        {
            problem = "Provide the party's national unified ID as 10 digits. Do not guess it.";
            return false;
        }

        if (!SocialIdPattern().IsMatch(digits))
        {
            problem = $"'{trimmed}' is not a national unified ID. It must be 10 digits starting with 1 or 2.";
            return false;
        }

        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            problem = $"'{trimmed}' is not a national unified ID.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, Json);

    /// <summary>
    /// True when a tool result is a failure payload.
    /// </summary>
    /// <remarks>
    /// The caller decides, from results it already holds, rather than the plugin
    /// flipping a flag. Mutable state on a shared plugin would be corrupted by
    /// concurrent turns, and "which call failed" needs to line up with "which
    /// call is this result for" anyway.
    /// </remarks>
    public static bool IsErrorResult(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith('{')) return false;

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            // Not JSON. "Cannot tell" rather than asserting a failure we cannot
            // evidence -- the same reasoning the Python version used.
            return false;
        }
    }
}