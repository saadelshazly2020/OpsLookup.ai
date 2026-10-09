using Microsoft.EntityFrameworkCore;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Entities;

namespace OpsLookup.Api.Data;

public sealed class AttorneyRepository(ContractDbContext db) : IAttorneyRepository
{
    public async Task<IReadOnlyList<AttorneyResult>> SearchAsync(
        long? socialId,
        string? partyName,
        CancellationToken cancellationToken = default)
    {
        // Anchored on Party, not Attorney: both filters describe the party, and
        // starting from Attorney would need a reverse navigation EF cannot express
        // cleanly here. Parties with no attorney drop out via the join.
        IQueryable<Party> parties = db.Parties
            .AsNoTracking()
            .Where(p => p.AttorneyId != null);

        if (socialId is not null)
        {
            parties = parties.Where(p => p.SocialId == socialId.Value);
        }

        if (!string.IsNullOrWhiteSpace(partyName))
        {
            // Parameterized LIKE '%x%'. Never concatenated into SQL.
            parties = parties.Where(p => p.Name.Contains(partyName));
        }

        return await parties
            .Where(p => p.Attorney != null)
            .OrderBy(p => p.Attorney!.Name)
            .ThenBy(p => p.Id)
            .Select(p => new AttorneyResult(
                p.Attorney!.Id,
                p.Attorney.Name,
                p.Attorney.Status,
                p.Id,
                p.Name,
                p.SocialId))
            .ToListAsync(cancellationToken);
    }
}