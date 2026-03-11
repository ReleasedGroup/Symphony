using Microsoft.EntityFrameworkCore;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Infrastructure.Persistence.Sqlite;

public sealed class SymphonyDbContext(DbContextOptions<SymphonyDbContext> options) : DbContext(options)
{
    public DbSet<InstanceLeaseEntity> InstanceLeases => Set<InstanceLeaseEntity>();
    public DbSet<DispatchClaimEntity> DispatchClaims => Set<DispatchClaimEntity>();
    public DbSet<WorkflowSnapshotEntity> WorkflowSnapshots => Set<WorkflowSnapshotEntity>();
    public DbSet<IssueCacheEntity> IssueCache => Set<IssueCacheEntity>();
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<RunAttemptEntity> RunAttempts => Set<RunAttemptEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<RetryQueueEntity> RetryQueue => Set<RetryQueueEntity>();
    public DbSet<WorkspaceRecordEntity> WorkspaceRecords => Set<WorkspaceRecordEntity>();
    public DbSet<EventLogEntity> EventLog => Set<EventLogEntity>();

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
            entity.Property(x => x.ReleasedAtUtc);
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.HasIndex(x => x.IssueId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.IssueId, x.Status });
            entity.HasIndex(x => x.IssueId)
                .IsUnique()
                .HasFilter("Status = 'active'");
        });

        modelBuilder.Entity<WorkflowSnapshotEntity>(entity =>
        {
            entity.ToTable("workflow_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourcePath).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ConfigHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RuntimeJson).IsRequired();
            entity.Property(x => x.LoadedAtUtc).IsRequired();
            entity.HasIndex(x => x.LoadedAtUtc);
            entity.HasIndex(x => x.ConfigHash);
        });

        modelBuilder.Entity<IssueCacheEntity>(entity =>
        {
            entity.ToTable("issues_cache");
            entity.HasKey(x => x.IssueId);
            entity.Property(x => x.IssueId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Identifier).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(500).IsRequired();
            entity.Property(x => x.State).HasMaxLength(100).IsRequired();
            entity.Property(x => x.BranchName).HasMaxLength(250);
            entity.Property(x => x.Url).HasMaxLength(2048);
            entity.Property(x => x.Milestone).HasMaxLength(200);
            entity.Property(x => x.LabelsJson).IsRequired();
            entity.Property(x => x.PullRequestsJson).IsRequired();
            entity.Property(x => x.BlockedByJson).IsRequired();
            entity.Property(x => x.CachedAtUtc).IsRequired();
            entity.HasIndex(x => x.Identifier);
            entity.HasIndex(x => x.State);
            entity.HasIndex(x => x.UpdatedAtUtc);
        });

        modelBuilder.Entity<RunEntity>(entity =>
        {
            entity.ToTable("runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IssueId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.IssueIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OwnerInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(50).IsRequired();
            entity.Property(x => x.State).HasMaxLength(100).IsRequired();
            entity.Property(x => x.WorkspacePath).HasMaxLength(2048);
            entity.Property(x => x.SessionId).HasMaxLength(256);
            entity.Property(x => x.RequestedStopReason).HasMaxLength(100);
            entity.Property(x => x.LastEvent).HasMaxLength(100);
            entity.Property(x => x.StartedAtUtc).IsRequired();
            entity.HasIndex(x => x.IssueId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.State);
            entity.HasIndex(x => new { x.IssueId, x.Status })
                .IsUnique()
                .HasFilter("Status IN ('running', 'retrying')");
        });

        modelBuilder.Entity<RunAttemptEntity>(entity =>
        {
            entity.ToTable("run_attempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IssueId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(50).IsRequired();
            entity.Property(x => x.WorkspacePath).HasMaxLength(2048);
            entity.Property(x => x.StartedAtUtc).IsRequired();
            entity.HasIndex(x => x.RunId);
            entity.HasIndex(x => x.IssueId);
            entity.HasIndex(x => x.Status);
            entity.HasOne(x => x.Run)
                .WithMany(x => x.Attempts)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RunAttemptId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ThreadId).HasMaxLength(128);
            entity.Property(x => x.TurnId).HasMaxLength(128);
            entity.Property(x => x.CodexAppServerPid).HasMaxLength(32);
            entity.Property(x => x.LastCodexEvent).HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.HasIndex(x => x.RunId);
            entity.HasIndex(x => x.RunAttemptId);
            entity.HasIndex(x => x.LastCodexTimestamp);
        });

        modelBuilder.Entity<RetryQueueEntity>(entity =>
        {
            entity.ToTable("retry_queue");
            entity.HasKey(x => x.IssueId);
            entity.Property(x => x.IssueId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.IssueIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OwnerInstanceId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.DelayType).HasMaxLength(50).IsRequired();
            entity.Property(x => x.DueAtUtc).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.HasIndex(x => x.DueAtUtc);
            entity.HasIndex(x => x.Attempt);
        });

        modelBuilder.Entity<WorkspaceRecordEntity>(entity =>
        {
            entity.ToTable("workspace_records");
            entity.HasKey(x => x.IssueId);
            entity.Property(x => x.IssueId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.IssueIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(x => x.WorkspacePath).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.BranchName).HasMaxLength(250);
            entity.Property(x => x.LastPreparedAtUtc).IsRequired();
            entity.Property(x => x.LastCleanupReason).HasMaxLength(100);
            entity.HasIndex(x => x.IssueIdentifier);
        });

        modelBuilder.Entity<EventLogEntity>(entity =>
        {
            entity.ToTable("event_log");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IssueId).HasMaxLength(200);
            entity.Property(x => x.IssueIdentifier).HasMaxLength(200);
            entity.Property(x => x.RunId).HasMaxLength(64);
            entity.Property(x => x.RunAttemptId).HasMaxLength(64);
            entity.Property(x => x.SessionId).HasMaxLength(256);
            entity.Property(x => x.EventName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Level).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.OccurredAtUtc).IsRequired();
            entity.HasIndex(x => x.IssueId);
            entity.HasIndex(x => x.RunId);
            entity.HasIndex(x => x.OccurredAtUtc);
        });
    }
}
