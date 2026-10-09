using OpsLookup.Api.Contracts;

namespace OpsLookup.Api.Data;

public interface IContractRepository
{
    /// <summary>
    /// Returns contracts for a party social ID. When <paramref name="partyName" /> is
    /// supplied the results are narrowed to parties whose name contains it.
    /// </summary>
    Task<IReadOnlyList<ContractResult>> GetBySocialIdAsync(
        long socialId,
        string? partyName,
        CancellationToken cancellationToken = default);
}