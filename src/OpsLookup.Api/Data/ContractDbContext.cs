using Microsoft.EntityFrameworkCore;
using OpsLookup.Api.Entities;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Data;

public class ContractDbContext(DbContextOptions<ContractDbContext> options) : DbContext(options)
{
    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<Party> Parties => Set<Party>();

    public DbSet<Attorney> Attorneys => Set<Attorney>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contract>(entity =>
        {
            entity.ToTable("Contract");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.HasIndex(x => x.Code)
                .IsUnique();

            entity.HasMany(x => x.Parties)
                .WithOne(x => x.Contract)
                .HasForeignKey(x => x.ContractId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Party>(entity =>
        {
            entity.ToTable("Party");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            // Every lookup filters on SocialId. Without this index each ticket
            // becomes a full table scan on Party.
            entity.HasIndex(x => x.SocialId);

            // Attorney lookups filter by Name, so this is the index that keeps
            // a name search off a table scan.
            entity.HasIndex(x => x.Name);

            entity.HasOne(x => x.Attorney)
                .WithMany(x => x.Parties)
                .HasForeignKey(x => x.AttorneyId)
                // Removing an attorney must not remove the parties they acted for.
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Attorney>(entity =>
        {
            entity.ToTable("Attorney");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.Status)
                .HasMaxLength(20)
                .IsRequired();

            entity.HasIndex(x => x.Name);

            // Belt and braces. The value is also validated in C#, but nothing
            // stops a script or SSMS from writing an arbitrary string.
            entity.ToTable(t =>
                t.HasCheckConstraint(
                    "CK_Attorney_Status",
                    $"[Status] IN ({string.Join(", ", AttorneyStatus.All.Select(s => $"'{s}'"))})"));
        });
    }
}