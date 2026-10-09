using OpsLookup.Api.Contracts;

namespace OpsLookup.Api.Data;

public interface IAttorneyRepository
{
    /// <summary>
    /// Finds attorneys through the parties they act for. Supply a social ID, a
    /// party name, or both. Parties with no attorney on file are excluded, because
    /// there is no attorney to report.
    /// </summary>
    Task<IReadOnlyList<AttorneyResult>> SearchAsync(
        long? socialId,
        string? partyName,
        CancellationToken cancellationToken = default);
}