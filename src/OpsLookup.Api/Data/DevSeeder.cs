using Microsoft.EntityFrameworkCore;
using OpsLookup.Api.Entities;

namespace OpsLookup.Api.Data;

/// <summary>
/// Development only. Seeding here rather than via <c>HasData</c> keeps demo rows out of
/// the migration history, which is where real data must never end up.
/// </summary>
public static class DevSeeder
{
    public static async Task SeedAsync(ContractDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Contracts.AnyAsync(cancellationToken))
        {
            return;
        }

        var created = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Primary keys are IDENTITY, so they are left to the database. Contracts are
        // referenced by navigation so EF populates ContractId from the generated keys.
        var maintenance = new Contract { Name = "Annual Maintenance Agreement", Code = 100234, CreatedDate = created };
        var network = new Contract { Name = "Network Support Retainer", Code = 100235, CreatedDate = created };
        var audit = new Contract { Name = "Security Audit Services", Code = 100236, CreatedDate = created };

        db.Contracts.AddRange(maintenance, network, audit);

        db.Parties.AddRange(
            new Party { Name = "A Nasser", Contract = maintenance, SocialId = 1234567890, CreatedDate = created },
            new Party { Name = "A Nasser", Contract = network, SocialId = 1234567890, CreatedDate = created },
            new Party { Name = "B Karimi", Contract = maintenance, SocialId = 1987654321, CreatedDate = created },
            new Party { Name = "C Delacroix", Contract = audit, SocialId = 2111111111, CreatedDate = created });

        await db.SaveChangesAsync(cancellationToken);
    }
}