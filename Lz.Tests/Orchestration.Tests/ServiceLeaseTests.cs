using Lz.Aws.Ops;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// The per-service lease (DecoupledCd.md V-22, §15.7 V-J), against an in-memory store with the same conditional
/// semantics as the DynamoDB one: a put succeeds only on a free or expired key, a delete only on the holder's token.
/// </summary>
public class ServiceLeaseTests
{
    private sealed class FakeStore : IServiceLeaseStore
    {
        public readonly Dictionary<string, ServiceLeaseRecord> Items = new();
        public Action? BeforeGet;

        public Task<bool> PutIfFreeAsync(string key, ServiceLeaseRecord record, DateTimeOffset now)
        {
            if (Items.TryGetValue(key, out var held) && held.ExpiresAt > now) return Task.FromResult(false);
            Items[key] = record;
            return Task.FromResult(true);
        }

        public Task<ServiceLeaseRecord?> GetAsync(string key)
        {
            BeforeGet?.Invoke();
            return Task.FromResult(Items.TryGetValue(key, out var r) ? r : null);
        }

        public Task<bool> DeleteIfTokenAsync(string key, string token)
        {
            if (!Items.TryGetValue(key, out var r) || r.Token != token) return Task.FromResult(false);
            Items.Remove(key);
            return Task.FromResult(true);
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
    private const string Cluster = "scu-dev-cluster", Service = "scu-mp-aiphost";

    private static Task<ServiceLeaseClaim> Claim(FakeStore s, string holder, DateTimeOffset at)
        => ServiceLease.ClaimAsync(s, Cluster, Service, holder, "an image roll", at);

    [Fact]
    public async Task AFreeServiceIsClaimed()
    {
        var store = new FakeStore();
        var claim = Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "updatecontainer:tmay", T0));
        Assert.Equal("updatecontainer:tmay", claim.Lease.Holder);
        Assert.Equal(T0 + ServiceLease.Duration, claim.Lease.ExpiresAt);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task AHeldServiceIsRefusedNamingItsHolder()
    {
        var store = new FakeStore();
        await Claim(store, "deployer:req-1", T0);
        var held = Assert.IsType<ServiceLeaseClaim.Held>(await Claim(store, "deploytenant:tmay", T0.AddMinutes(5)));
        Assert.Equal("deployer:req-1", held.By.Holder);
        Assert.Contains("deployer:req-1 holds it for an image roll", ServiceLease.Describe(held.By));
    }

    [Fact]
    public async Task AnExpiredLeaseIsTakenOver()
    {
        var store = new FakeStore();
        await Claim(store, "deployer:crashed", T0);
        Assert.IsType<ServiceLeaseClaim.Held>(await Claim(store, "updatecontainer:tmay", T0 + ServiceLease.Duration - TimeSpan.FromSeconds(1)));
        Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "updatecontainer:tmay", T0 + ServiceLease.Duration));
    }

    [Fact]
    public async Task OnlyTheHolderReleases()
    {
        var store = new FakeStore();
        var mine = Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "deployer:req-1", T0)).Lease;
        Assert.False(await ServiceLease.ReleaseAsync(store, Cluster, Service, mine with { Token = "someone-else" }));
        Assert.Single(store.Items);
        Assert.True(await ServiceLease.ReleaseAsync(store, Cluster, Service, mine));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task AWriterThatOutlivedItsLeaseLearnsSoAtItsRelease()
    {
        var store = new FakeStore();
        var slow = Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "deployer:slow", T0)).Lease;
        Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "updatecontainer:tmay", T0 + ServiceLease.Duration));
        Assert.False(await ServiceLease.ReleaseAsync(store, Cluster, Service, slow));
        Assert.Equal("updatecontainer:tmay", store.Items.Single().Value.Holder);
    }

    [Fact]
    public async Task ALeaseReleasedBetweenTheRefusalAndTheReadIsTakenOnTheSecondTry()
    {
        var store = new FakeStore();
        var other = Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "deployer:req-1", T0)).Lease;
        store.BeforeGet = () => { store.Items.Clear(); store.BeforeGet = null; };   // released just after our put was refused
        var claim = Assert.IsType<ServiceLeaseClaim.Claimed>(await Claim(store, "deploytenant:tmay", T0.AddMinutes(1)));
        Assert.NotEqual(other.Token, claim.Lease.Token);
    }

    [Fact]
    public async Task EachServiceHasItsOwnLease()
    {
        var store = new FakeStore();
        await ServiceLease.ClaimAsync(store, Cluster, "scu-mp-aiphost", "deployer:req-1", "roll", T0);
        Assert.IsType<ServiceLeaseClaim.Claimed>(await ServiceLease.ClaimAsync(store, Cluster, "scu-mp-other", "deployer:req-2", "roll", T0));
        Assert.Equal("lease#ecs-service#scu-dev-cluster#scu-mp-aiphost", ServiceLease.KeyFor(Cluster, "scu-mp-aiphost"));
    }

    [Theory]
    [InlineData("", "svc")]
    [InlineData("cluster", " ")]
    public void AKeyNamesItsClusterAndService(string cluster, string service)
        => Assert.Throws<ArgumentException>(() => ServiceLease.KeyFor(cluster, service));

    [Fact]
    public async Task AClaimNamesItsHolder()
        => await Assert.ThrowsAsync<ArgumentException>(() => Claim(new FakeStore(), " ", T0));

    [Fact]
    public void TheLeaseOutlastsTheLongestRolloutWait()
        => Assert.True(ServiceLease.Duration > TimeSpan.FromMinutes(10));

    // ---- deploytenant's claim on every service of a tenant (ServiceLease.ClaimAllAsync) ----

    [Fact]
    public async Task ATenantDeployClaimsEveryService()
    {
        var store = new FakeStore();
        var (claimed, held) = await ServiceLease.ClaimAllAsync(store, Cluster, new[] { "scu-mp-a", "scu-mp-b" }, "deploytenant:tmay", "deploy", T0);
        Assert.Null(held);
        Assert.Equal(new[] { "scu-mp-a", "scu-mp-b" }, claimed.Select(c => c.Service));
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public async Task OneHeldServiceRefusesTheTenantAndReleasesTheOthers()
    {
        var store = new FakeStore();
        await ServiceLease.ClaimAsync(store, Cluster, "scu-mp-b", "deployer:req-9", "roll", T0);
        var (claimed, held) = await ServiceLease.ClaimAllAsync(store, Cluster, new[] { "scu-mp-a", "scu-mp-b", "scu-mp-c" }, "deploytenant:tmay", "deploy", T0);
        Assert.Empty(claimed);
        Assert.Equal("scu-mp-b", held!.Value.Service);
        Assert.Equal("deployer:req-9", held.Value.By.Holder);
        Assert.Equal("deployer:req-9", store.Items.Single().Value.Holder);   // a released, c never claimed
    }

    // ---- updatecontainer's roll under the lease (AwsContainerUpdater.RunUnderLeaseAsync) ----

    private static Task<ContainerUpdateResult> Roll(FakeStore s, bool wait, Func<Task<ContainerUpdateResult>> roll)
        => AwsContainerUpdater.RunUnderLeaseAsync(new ServiceLeaseRequest(s, "updatecontainer:tmay@ws", "lz updatecontainer"),
            Cluster, Service, wait, T0, roll);

    [Fact]
    public async Task ARollRunsHoldingTheLeaseAndReleasesItAfterTheWait()
    {
        var store = new FakeStore();
        string? holderDuringRoll = null;
        var result = await Roll(store, wait: true, () =>
        {
            holderDuringRoll = store.Items.Single().Value.Holder;
            return Task.FromResult(new ContainerUpdateResult(Service, UpdateOutcome.Verified, "landed"));
        });
        Assert.Equal("updatecontainer:tmay@ws", holderDuringRoll);
        Assert.Equal(UpdateOutcome.Verified, result.Outcome);
        Assert.Equal("landed", result.Detail);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task AHeldServiceIsNotRolledAndTheRefusalNamesTheHolder()
    {
        var store = new FakeStore();
        await Claim(store, "deployer:req-7", T0.AddMinutes(-1));
        var rolled = false;
        var result = await Roll(store, wait: true, () => { rolled = true; return Task.FromResult(new ContainerUpdateResult(Service, UpdateOutcome.Verified, "")); });
        Assert.False(rolled);
        Assert.Equal(UpdateOutcome.LeaseHeld, result.Outcome);
        Assert.Contains("deployer:req-7", result.Detail);
        Assert.Equal("deployer:req-7", store.Items.Single().Value.Holder);
    }

    [Fact]
    public async Task ARollNotWaitedForKeepsTheLeaseUntilItExpires()
    {
        var store = new FakeStore();
        var result = await Roll(store, wait: false, () => Task.FromResult(new ContainerUpdateResult(Service, UpdateOutcome.Deployed, "requested")));
        Assert.Equal("updatecontainer:tmay@ws", store.Items.Single().Value.Holder);
        Assert.Contains("stays held until", result.Detail);
    }

    [Fact]
    public async Task AFailedRollReleases()
    {
        var store = new FakeStore();
        await Roll(store, wait: true, () => Task.FromResult(new ContainerUpdateResult(Service, UpdateOutcome.Failed, "rolled back")));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task ARollThatThrowsReleasesEvenWhenNotWaitedFor()
    {
        var store = new FakeStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Roll(store, wait: false, () => throw new InvalidOperationException("UpdateService refused")));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task ARollThatOutlivedItsLeaseSaysSo()
    {
        var store = new FakeStore();
        var result = await Roll(store, wait: true, async () =>
        {
            await Claim(store, "deployer:req-8", T0 + ServiceLease.Duration);   // ours expired mid-roll and was taken
            return new ContainerUpdateResult(Service, UpdateOutcome.Verified, "landed");
        });
        Assert.Contains("outlived its lease", result.Detail);
        Assert.Equal("deployer:req-8", store.Items.Single().Value.Holder);
    }
}
