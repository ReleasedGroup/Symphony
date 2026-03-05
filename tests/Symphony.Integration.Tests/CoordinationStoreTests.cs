using Microsoft.EntityFrameworkCore;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Integration.Tests;

public sealed class CoordinationStoreTests
{
    [Fact]
    public async Task LeaseAndClaim_ShouldEnforceSingleOwnerAtATime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-coord.db");
        var connectionString = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate";
        var options = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var setupContext = new SymphonyDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        await using var contextA = new SymphonyDbContext(options);
        await using var contextB = new SymphonyDbContext(options);
        var storeA = new OrchestrationCoordinationStore(contextA, TimeProvider.System);
        var storeB = new OrchestrationCoordinationStore(contextB, TimeProvider.System);

        var leaseA = await storeA.AcquireOrRenewLeaseAsync("dispatch", "instance-a", TimeSpan.FromMinutes(5));
        var leaseB = await storeB.AcquireOrRenewLeaseAsync("dispatch", "instance-b", TimeSpan.FromMinutes(5));
        Assert.True(leaseA);
        Assert.False(leaseB);

        var claimA = await storeA.TryClaimIssueAsync("issue-1", "#1", "instance-a");
        var claimB = await storeB.TryClaimIssueAsync("issue-1", "#1", "instance-b");
        Assert.True(claimA);
        Assert.False(claimB);

        await storeA.ReleaseIssueClaimAsync("issue-1", "instance-a", "released");
        var claimBAfterRelease = await storeB.TryClaimIssueAsync("issue-1", "#1", "instance-b");
        Assert.True(claimBAfterRelease);
    }
}
