using Microsoft.EntityFrameworkCore;
using OpsLookup.Api.Entities;

namespace OpsLookup.Api.Data;

public class ContractDbContext(DbContextOptions<ContractDbContext> options) : DbContext(options)
{
    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<Party> Parties => Set<Party>();

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
        });
    }
}