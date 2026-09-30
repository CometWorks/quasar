using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterContentMonitorTests
{
    [Fact]
    public async Task BaselinesAndPendingChangesSurviveRestartAndFailedChecks()
    {
        using var fixture = new Fixture();
        using (var monitor = fixture.Create())
        {
            await monitor.CheckCoreAsync(default);
            Assert.All(monitor.GetSnapshot(fixture.Cluster).Items, row => Assert.False(row.HasUpdate));
        }
        fixture.Timestamp++;
        fixture.Commit = new('b', 40);
        using var restarted = fixture.Create();
        await restarted.CheckCoreAsync(default);
        var changed = restarted.GetSnapshot(fixture.Cluster);
        Assert.Equal(2, changed.Items.Count(row => row.HasUpdate));
        var mod = Assert.Single(changed.Items, row => row.Kind == "mod");
        Assert.Null(mod.PinnedVersion);
        Assert.Equal("1700000000", mod.BaselineVersion);
        Assert.Equal("1700000001", mod.LatestVersion);
        Assert.Equal(1, fixture.PinReads);
        fixture.Offline = true;
        await restarted.CheckCoreAsync(default);
        var offline = restarted.GetSnapshot(fixture.Cluster);
        Assert.All(offline.Items, row => { Assert.True(row.HasUpdate); Assert.NotNull(row.Error); });
        Assert.Equal(changed.Items.Select(row => row.ObservedAt), offline.Items.Select(row => row.ObservedAt));
        using var again = fixture.Create();
        Assert.Equal(2, again.GetSnapshot(fixture.Cluster).Items.Count(row => row.HasUpdate));
    }

    [Fact]
    public async Task CandidatePinsNeverReplaceActiveInventoryAndActivationInvalidatesIt()
    {
        using var fixture = new Fixture();
        using var monitor = fixture.Create();
        await monitor.CheckCoreAsync(default);
        fixture.PinCommit = new('c', 40); // Newly selected candidate, not deployed.
        fixture.Cluster.DependencyManifestSha256 = new('d', 64);
        await monitor.CheckCoreAsync(default);
        Assert.Equal(new string('a', 40), Assert.Single(monitor.GetSnapshot(fixture.Cluster).Items, i => i.Kind == "plugin").PinnedVersion);
        fixture.Cluster.ActiveDeployment = fixture.Cluster.ActiveDeployment! with { Revision = "new-revision" };
        Assert.Empty(monitor.GetSnapshot(fixture.Cluster).Items);
        await monitor.CheckCoreAsync(default);
        Assert.Equal(new string('c', 40), Assert.Single(monitor.GetSnapshot(fixture.Cluster).Items, i => i.Kind == "plugin").PinnedVersion);
        Assert.Equal(2, fixture.PinReads);
    }

    [Fact]
    public async Task MissingInventoryOrProfileDoesNotPretendContentIsCurrent()
    {
        using var fixture = new Fixture();
        using var monitor = fixture.Create();
        await monitor.CheckCoreAsync(default);
        fixture.MissingProfile = true;
        await monitor.CheckCoreAsync(default);
        Assert.NotNull(monitor.GetSnapshot(fixture.Cluster).CheckError);
        Assert.NotNull(Assert.Single(monitor.GetSnapshot(fixture.Cluster).Items, i => i.Kind == "mod").Error);
        fixture.MissingProfile = false;
        fixture.Timestamp++;
        fixture.Cluster.ActiveDeployment = fixture.Cluster.ActiveDeployment! with { Revision = "imported" };
        fixture.MissingPins = true;
        await monitor.CheckCoreAsync(default);
        var snapshot = monitor.GetSnapshot(fixture.Cluster);
        Assert.NotNull(snapshot.InventoryError);
        Assert.True(Assert.Single(snapshot.Items).HasUpdate);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneSweepAndWorkshopIdsAreBatchedAcrossClusters()
    {
        using var fixture = new Fixture();
        fixture.Clusters.Add(fixture.Cluster.Clone());
        fixture.Clusters[1].UniqueName = "beta";
        using var monitor = fixture.Create();
        await Task.WhenAll(monitor.CheckNowAsync(), monitor.CheckNowAsync(), monitor.CheckNowAsync());
        Assert.Equal(1, fixture.WorkshopReads);
        Assert.Equal(1, fixture.HubReads);
        Assert.Equal(new long[] { 123456 }, fixture.RequestedIds);
    }

    [Fact]
    public async Task DamagedStateDoesNotOverwriteBaselinesOrPreventUiFromLoading()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "broken json");
        using var monitor = fixture.Create();
        Assert.NotNull(monitor.GetSnapshot(fixture.Cluster).CheckError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.CheckNowAsync());
        Assert.Equal("broken json", File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void PluginVersionIsDeclaredCommitAndRepositoryMustMatch()
    {
        var pin = new ClusterPluginPin("test", "Test", "CometWorks/test", new('a', 40));
        var hub = new QuasarPluginCatalogEntry { PluginId = pin.Id, SourceRepo = pin.Repository,
            SourceCommit = pin.Commit, Description = "Metadata changed", ManifestFile = "Plugins/Renamed.xml" };
        Assert.False(ClusterContentMonitor.ObservePlugin(pin, hub, null, null, DateTimeOffset.UtcNow).HasUpdate);
        hub.SourceCommit = new('b', 40);
        var changed = ClusterContentMonitor.ObservePlugin(pin, hub, null, null, DateTimeOffset.UtcNow);
        Assert.True(changed.HasUpdate);
        Assert.EndsWith(pin.Commit + "..." + hub.SourceCommit, changed.DetailsUrl);
        hub.SourceRepo = "another/repository";
        var mismatch = ClusterContentMonitor.ObservePlugin(pin, hub, null, null, DateTimeOffset.UtcNow);
        Assert.NotNull(mismatch.Error);
        Assert.Null(mismatch.LatestVersion);
    }

    [Fact]
    public async Task NoticesIncludeContentAlongsideWebAndRespectClusterScope()
    {
        using var fixture = new Fixture();
        using var monitor = fixture.Create();
        await monitor.CheckCoreAsync(default);
        fixture.Timestamp++;
        await monitor.CheckCoreAsync(default);
        var web = new QuasarUpdateSnapshot { WebReleases = [new() { Version = "2.0", IsNewer = true }] };
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new Claim(QuasarClaimTypes.Cluster, "alpha")], "service"));
        var notices = UpdateNotices.All(web, new(null, null, null), [fixture.Cluster], user, monitor.GetSnapshot).ToArray();
        Assert.Equal(2, notices.Length);
        Assert.Equal("/clusters/alpha#cluster-content", notices[1].Url);
        var denied = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new Claim(QuasarClaimTypes.Cluster, "beta")], "service"));
        Assert.Single(UpdateNotices.All(web, new(null, null, null), [fixture.Cluster], denied, monitor.GetSnapshot));
        var receipt = new PushNotificationService.StoredSubscription("steam", "user", "endpoint", "key", "auth",
            notices[0].Key, [notices[0].Key]);
        Assert.Equal(notices[1], Assert.Single(PushNotificationService.PendingNotices(notices, receipt)));
        receipt = receipt with { DeliveredNoticeKeys = notices.Select(n => n.Key).ToArray() };
        receipt = JsonSerializer.Deserialize<PushNotificationService.StoredSubscription>(JsonSerializer.Serialize(receipt))!;
        Assert.Empty(PushNotificationService.PendingNotices(notices, receipt));
        Assert.Equal(2, PushNotificationService.PendingNotices(notices, receipt with { LastNoticeKey = null, DeliveredNoticeKeys = null }).Length);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "content-monitor-" + Guid.NewGuid());
        public string Path => System.IO.Path.Combine(_root, "observations.json");
        public ClusterDefinition Cluster => Clusters[0];
        public List<ClusterDefinition> Clusters { get; } = [new() { UniqueName = "alpha", DisplayName = "Alpha",
            ConfigProfileId = "profile", ActiveDeployment = new("revision", [], DateTimeOffset.UtcNow) }];
        public string Commit = new('a', 40), PinCommit = new('a', 40);
        public long Timestamp = 1700000000;
        public bool Offline, MissingPins, MissingProfile;
        public int PinReads, WorkshopReads, HubReads;
        public long[] RequestedIds = [];
        public Fixture() => Directory.CreateDirectory(_root);
        public ClusterContentMonitor Create() => new(() => Clusters,
            _ => MissingProfile ? null : new QuasarModSelection[] { new() { WorkshopId = 123456, DisplayName = "Example mod" } },
            (_, _) =>
            {
                PinReads++;
                if (MissingPins) throw new InvalidDataException("No active pins");
                return Task.FromResult(new[] { new ClusterPluginPin("plugin", "Example plugin", "CometWorks/plugin", PinCommit) });
            },
            (ids, _) =>
            {
                WorkshopReads++; RequestedIds = ids.ToArray();
                if (Offline) throw new HttpRequestException("offline");
                return Task.FromResult<IReadOnlyDictionary<long, WorkshopUpdateObservation>>(new Dictionary<long, WorkshopUpdateObservation>
                    { [123456] = new(123456, "Example mod", Timestamp, null) });
            },
            _ =>
            {
                HubReads++;
                if (Offline) throw new HttpRequestException("offline");
                return Task.FromResult<IReadOnlyList<QuasarPluginCatalogEntry>>(new[] { new QuasarPluginCatalogEntry
                    { PluginId = "plugin", SourceRepo = "CometWorks/plugin", SourceCommit = Commit } });
            }, Path, new QuasarUpdateOptions(), NullLogger<ClusterContentMonitor>.Instance);
        public void Dispose() => Directory.Delete(_root, true);
    }
}
