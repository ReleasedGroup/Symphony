using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Host.Services;
using Symphony.Host.Workers;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Integration.Tests;

public sealed class ManagedLeaseTests
{
    [Fact]
    [Trait("Spec", "17.4")]
    public async Task Renewal_ShouldFenceStaleEpochAndRejectInvalidIdentityOrSignature()
    {
        await using var harness = await LeaseHarness.CreateAsync("instance-1", "generation-1");
        var now = harness.TimeProvider.GetUtcNow();
        var first = harness.Sign(2, now, now.AddSeconds(30));

        Assert.Equal("lease_not_renewed",
            (await harness.Service.GetDispatchDecisionAsync(CancellationToken.None)).DeniedReason);
        Assert.Equal("invalid_signature", (await harness.Service.RenewAsync(
            first with { Signature = "not-a-signature" }, CancellationToken.None)).ErrorCode);
        Assert.Equal("generation_mismatch", (await harness.Service.RenewAsync(
            first with { GenerationId = "old-generation" }, CancellationToken.None)).ErrorCode);
        Assert.True((await harness.Service.RenewAsync(first, CancellationToken.None)).Accepted);
        Assert.True((await harness.Service.RenewAsync(first, CancellationToken.None)).Accepted);
        Assert.Equal("stale_epoch", (await harness.Service.RenewAsync(
            harness.Sign(1, now, now.AddSeconds(30)), CancellationToken.None)).ErrorCode);

        var oldToken = harness.Runtime.GetRunCancellationToken();
        Assert.True((await harness.Service.RenewAsync(
            harness.Sign(3, now, now.AddSeconds(30)), CancellationToken.None)).Accepted);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(3, (await harness.Service.GetDispatchDecisionAsync(CancellationToken.None)).Epoch);

        var dbOptions = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite(harness.DbContext.Database.GetConnectionString()).Options;
        await using (var concurrentContext = new SymphonyDbContext(dbOptions))
        {
            await concurrentContext.ManagedLease.ExecuteUpdateAsync(setters =>
                setters.SetProperty(item => item.CurrentEpoch, 5));
        }

        Assert.Equal("stale_epoch", (await harness.Service.RenewAsync(
            harness.Sign(4, now, now.AddSeconds(30)), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task Lease_ShouldDenyClockSkewAndCancelOnNetworkPartitionWithoutPolling()
    {
        await using var harness = await LeaseHarness.CreateAsync(
            "instance-1", "generation-1", maxClockSkewSeconds: 0, maxLeaseSeconds: 10);
        var now = harness.TimeProvider.GetUtcNow();
        Assert.Equal("clock_skew_exceeded", (await harness.Service.RenewAsync(
            harness.Sign(1, now.AddSeconds(10), now.AddSeconds(12)), CancellationToken.None)).ErrorCode);

        var lease = harness.Sign(1, now, now.AddSeconds(1));
        Assert.True((await harness.Service.RenewAsync(lease, CancellationToken.None)).Accepted);
        var runToken = harness.Runtime.GetRunCancellationToken();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(Timeout.InfiniteTimeSpan, runToken).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("lease_expired", harness.Runtime.GetLocalDecision().DeniedReason);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task Restart_ShouldRequireFreshRenewalOfPersistedLease()
    {
        await using var harness = await LeaseHarness.CreateAsync("instance-1", "generation-1");
        var now = harness.TimeProvider.GetUtcNow();
        var lease = harness.Sign(7, now, now.AddSeconds(30));
        Assert.True((await harness.Service.RenewAsync(lease, CancellationToken.None)).Accepted);

        var restartedRuntime = new ManagedLeaseRuntime(harness.Options, harness.TimeProvider);
        var restartedService = new ManagedLeaseService(
            harness.DbContext, harness.Options, restartedRuntime, harness.TimeProvider);
        Assert.Equal("lease_not_renewed",
            (await restartedService.GetDispatchDecisionAsync(CancellationToken.None)).DeniedReason);
        Assert.True((await restartedService.RenewAsync(lease, CancellationToken.None)).Accepted);
        Assert.True((await restartedService.GetDispatchDecisionAsync(CancellationToken.None)).Allowed);
        restartedRuntime.Dispose();
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task ExternalEpochChange_ShouldCancelActiveRunTokenWithinMonitorInterval()
    {
        await using var harness = await LeaseHarness.CreateAsync("instance-1", "generation-1");
        var now = harness.TimeProvider.GetUtcNow();
        Assert.True((await harness.Service.RenewAsync(
            harness.Sign(1, now, now.AddSeconds(30)), CancellationToken.None)).Accepted);
        var token = harness.Runtime.GetRunCancellationToken();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(harness.Options);
        builder.Services.AddSingleton(harness.Runtime);
        builder.Services.AddDbContext<SymphonyDbContext>(options =>
            options.UseSqlite(harness.DbContext.Database.GetConnectionString()));
        builder.Services.AddHostedService<ManagedLeaseMonitor>();
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await harness.DbContext.ManagedLease.ExecuteUpdateAsync(setters =>
                setters.SetProperty(item => item.CurrentEpoch, 2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("lease_epoch_mismatch", harness.Runtime.GetLocalDecision().DeniedReason);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task IndependentDatabases_ShouldOnlyDispatchTheGenerationWithAnUnexpiredIssuedLease()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var oldHost = await LeaseHarness.CreateAsync("logical-1", "generation-old", clock);
        await using var newHost = await LeaseHarness.CreateAsync("logical-1", "generation-new", clock);
        var issued = clock.GetUtcNow();
        Assert.True((await oldHost.Service.RenewAsync(
            oldHost.Sign(1, issued, issued.AddSeconds(10)), CancellationToken.None)).Accepted);
        Assert.True((await oldHost.Service.GetDispatchDecisionAsync(CancellationToken.None)).Allowed);
        Assert.False((await newHost.Service.GetDispatchDecisionAsync(CancellationToken.None)).Allowed);
        Assert.Equal("generation_mismatch", (await newHost.Service.RenewAsync(
            oldHost.Sign(1, issued, issued.AddSeconds(10)), CancellationToken.None)).ErrorCode);

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False((await oldHost.Service.GetDispatchDecisionAsync(CancellationToken.None)).Allowed);
        var newIssueTime = clock.GetUtcNow();
        Assert.True((await newHost.Service.RenewAsync(
            newHost.Sign(2, newIssueTime, newIssueTime.AddSeconds(10)), CancellationToken.None)).Accepted);
        Assert.True((await newHost.Service.GetDispatchDecisionAsync(CancellationToken.None)).Allowed);
        Assert.False((await oldHost.Service.GetDispatchDecisionAsync(CancellationToken.None)).Allowed);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }

    private sealed class LeaseHarness : IAsyncDisposable
    {
        private readonly string dbPath;
        private readonly string keyName;
        private readonly string keyValue;

        private LeaseHarness(
            string dbPath,
            string keyName,
            string keyValue,
            SymphonyDbContext dbContext,
            IOptions<ManagedLeaseOptions> options,
            ManagedLeaseRuntime runtime,
            ManagedLeaseService service,
            TimeProvider timeProvider)
        {
            this.dbPath = dbPath;
            this.keyName = keyName;
            this.keyValue = keyValue;
            DbContext = dbContext;
            Options = options;
            Runtime = runtime;
            Service = service;
            TimeProvider = timeProvider;
        }

        public SymphonyDbContext DbContext { get; }
        public IOptions<ManagedLeaseOptions> Options { get; }
        public ManagedLeaseRuntime Runtime { get; }
        public ManagedLeaseService Service { get; }
        public TimeProvider TimeProvider { get; }

        public static async Task<LeaseHarness> CreateAsync(
            string instanceId,
            string generationId,
            TimeProvider? timeProvider = null,
            int maxClockSkewSeconds = 5,
            int maxLeaseSeconds = 60)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-lease-{Guid.NewGuid():N}.db");
            var keyName = $"SYMPHONY_LEASE_TEST_{Guid.NewGuid():N}";
            var keyValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Environment.SetEnvironmentVariable(keyName, keyValue);
            var dbOptions = new DbContextOptionsBuilder<SymphonyDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False").Options;
            var dbContext = new SymphonyDbContext(dbOptions);
            await dbContext.Database.EnsureCreatedAsync();
            var options = Microsoft.Extensions.Options.Options.Create(new ManagedLeaseOptions
            {
                Enabled = true,
                InstanceId = instanceId,
                GenerationId = generationId,
                SigningKeyReference = $"${keyName}",
                MaxClockSkewSeconds = maxClockSkewSeconds,
                MaxLeaseSeconds = maxLeaseSeconds
            });
            var clock = timeProvider ?? TimeProvider.System;
            var runtime = new ManagedLeaseRuntime(options, clock);
            var service = new ManagedLeaseService(dbContext, options, runtime, clock);
            return new LeaseHarness(dbPath, keyName, keyValue, dbContext, options, runtime, service, clock);
        }

        public ManagedLeaseRequest Sign(long epoch, DateTimeOffset issued, DateTimeOffset expiry)
        {
            var options = Options.Value;
            var payload = $"{options.InstanceId}\n{options.GenerationId}\n{epoch}\n" +
                $"{issued.ToUnixTimeMilliseconds()}\n{expiry.ToUnixTimeMilliseconds()}";
            var signature = Convert.ToBase64String(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(keyValue), Encoding.UTF8.GetBytes(payload)));
            return new ManagedLeaseRequest(
                options.InstanceId, options.GenerationId, epoch, issued, expiry, signature);
        }

        public async ValueTask DisposeAsync()
        {
            Runtime.Dispose();
            await DbContext.DisposeAsync();
            Environment.SetEnvironmentVariable(keyName, null);
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
