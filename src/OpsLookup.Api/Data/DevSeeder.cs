using Microsoft.EntityFrameworkCore;
using OpsLookup.Api.Entities;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Data;

/// <summary>
/// Development only. Seeding here rather than via <c>HasData</c> keeps demo rows out of
/// the migration history, which is where real data must never end up.
/// </summary>
/// <remarks>
/// Seeds only a completely empty database, so adding a new seeded entity means a
/// reset is needed before it appears: <c>dotnet ef database drop</c>. Seeded rows
/// are not real data and are safe to discard.
/// </remarks>
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
        var maintenance = new Contract { Name = "عقد العمل الموحد", Code = 100234, CreatedDate = created };
        var network = new Contract { Name = "عقد المدارس", Code = 100235, CreatedDate = created };
        var audit = new Contract { Name = "عقد المحامين", Code = 100236, CreatedDate = created };

        db.Contracts.AddRange(maintenance, network, audit);

        // Attorneys are referenced by navigation from Party, so EF assigns the
        // identity keys and fills the foreign keys.
        var approvedAttorney = new Attorney { Name = "وكالة فردية", Status = AttorneyStatus.Approved, CreatedDate = created };
        var fakedAttorney = new Attorney { Name = "وكالة إلكترونية", Status = AttorneyStatus.Faked, CreatedDate = created };

        db.Attorneys.AddRange(approvedAttorney, fakedAttorney);

        db.Parties.AddRange(
            new Party { Name = "أحمد ناصر", Contract = maintenance, SocialId = 1234567890, CreatedDate = created },
            new Party { Name = "أحمد ناصر", Contract = network, SocialId = 1234567890, CreatedDate = created },
            new Party
            {
                Name = "محمد إبراهيم", Contract = maintenance, SocialId = 1987654321,
                Attorney = approvedAttorney, CreatedDate = created,
            },
            new Party
            {
                Name = "ياسين محمد", Contract = audit, SocialId = 2111111111,
                Attorney = fakedAttorney, CreatedDate = created,
            });

        // A Nasser is deliberately left with no attorney, so the "no attorney on
        // file" path is exercised by the seed data rather than only in theory.
        await db.SaveChangesAsync(cancellationToken);
    }
}