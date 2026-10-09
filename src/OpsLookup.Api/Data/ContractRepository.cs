using Microsoft.EntityFrameworkCore;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Entities;

namespace OpsLookup.Api.Data;

public sealed class ContractRepository(ContractDbContext db) : IContractRepository
{
    public async Task<IReadOnlyList<ContractResult>> GetBySocialIdAsync(
        long socialId,
        string? partyName,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Party> parties = db.Parties
            .AsNoTracking()
            .Where(p => p.SocialId == socialId);

        if (!string.IsNullOrWhiteSpace(partyName))
        {
            // Translates to a parameterized LIKE '%x%'. Never concatenated into SQL.
            parties = parties.Where(p => p.Name.Contains(partyName));
        }

        // Projecting through the Contract navigation lets EF generate the JOIN.
        return await parties
            .OrderBy(p => p.Contract.Code)
            .Select(p => new ContractResult(
                p.Contract.Code,
                p.SocialId,
                p.Name,
                p.Contract.Name))
            .ToListAsync(cancellationToken);
    }
}