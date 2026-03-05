using Microsoft.EntityFrameworkCore;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Infrastructure.Persistence.Sqlite;

public sealed class SymphonyDbContext(DbContextOptions<SymphonyDbContext> options) : DbContext(options)
{
    public DbSet<InstanceLeaseEntity> InstanceLeases => Set<InstanceLeaseEntity>();
    public DbSet<DispatchClaimEntity> DispatchClaims => Set<DispatchClaimEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InstanceLeaseEntity>(entity =>
        {
            entity.ToTable("instance_leases");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LeaseName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OwnerInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.AcquiredAtUtc).IsRequired();
            entity.Property(x => x.ExpiresAtUtc).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.HasIndex(x => x.LeaseName).IsUnique();
        });

        modelBuilder.Entity<DispatchClaimEntity>(entity =>
        {
            entity.ToTable("dispatch_claims");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IssueId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.IssueIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ClaimedByInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(50).IsRequired();
            entity.Property(x => x.ClaimedAtUtc).IsRequired();
            entity.HasIndex(x => x.IssueId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.IssueId, x.Status });
        });
    }
}
