namespace OpsLookup.Api.Contracts;

/// <summary>
/// Response shape for GET /api/attorneys
/// </summary>
/// <remarks>
/// Includes the matched party, not just the attorney. A member asking "is my
/// attorney approved?" wants confirmation that the attorney they hold on file is
/// the one that answered, so the join has to be visible in the response.
/// </remarks>
public sealed record AttorneyResult(
    int AttorneyId,
    string AttorneyName,
    string Status,
    long PartyId,
    string PartyName,
    long SocialId);