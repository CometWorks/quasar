using Magnetar.Protocol.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.PluginSdk;
using Xunit;

namespace Quasar.Tests;

public sealed class PluginConfigSnapshotPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quasar-plugin-config-" + Guid.NewGuid().ToString("N"));
    private readonly MetricsStoreService _metrics = new(null!, new AnalyticsStoreOptions(), NullLogger<MetricsStoreService>.Instance);
    private AgentRegistry Registry() => new(null!, _metrics, new ProfilerStoreService(), new PluginStatsStoreService());
    private PluginConfigService Service(AgentRegistry registry) => new(registry, NullLogger<PluginConfigService>.Instance, _directory);
    private static void Connect(AgentRegistry registry, string connection, bool cluster = false) => registry.UpsertHello(
        new AgentHello { AgentId = "agent", UniqueName = "server", ClusterMode = cluster, ClusterId = cluster ? "fleet" : "" },
        connection, (_, _) => Task.CompletedTask);
    private static PluginConfigSnapshot Snapshot(string value = "original") => new()
    {
        AgentId = "agent", Plugins = [new() { PluginId = "plugin", ConfigType = "Plugin.Settings", ConfigJson = value }],
    };

    [Fact]
    public async Task StoppedServerRetainsHistoricalSnapshotAcrossServiceRestartWithoutLiveConfigs()
    {
        var registry = Registry();
        var service = Service(registry);
        await service.StartAsync(default);
        Connect(registry, "connection");
        var before = DateTimeOffset.UtcNow;
        service.IngestSnapshot(Snapshot(), "connection");
        registry.MarkDisconnected("connection");
        Assert.Empty(service.GetConfigsForAgent("agent"));
        Assert.False(service.HasConfigs("agent"));
        var restarted = Service(registry);
        var stored = Assert.IsType<LastKnownPluginConfigSnapshot>(restarted.GetLastKnownConfigsForServer("SERVER"));
        Assert.Equal("server", stored.ServerUniqueName);
        Assert.Equal("agent", stored.AgentId);
        Assert.InRange(stored.CapturedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal("Plugin.Settings", Assert.Single(stored.Plugins).ConfigType);
        Assert.Equal("original", Assert.Single(restarted.GetLastKnownConfigsForAgent("agent")!.Plugins).ConfigJson);
        Assert.Empty(restarted.GetConfigsForAgent("agent"));
        await service.StopAsync(default);
    }

    [Fact]
    public void SupersededOrClusterConnectionCannotReplaceStandaloneSnapshot()
    {
        var registry = Registry();
        var service = Service(registry);
        Connect(registry, "old");
        service.IngestSnapshot(Snapshot(), "old");
        Connect(registry, "new");
        service.IngestSnapshot(Snapshot("current"), "new");
        service.IngestSnapshot(Snapshot("stale"), "old");
        Assert.Equal("current", service.GetLastKnownConfigsForServer("server")!.Plugins.Single().ConfigJson);
        Connect(registry, "cluster", cluster: true);
        service.IngestSnapshot(Snapshot("cluster"), "cluster");
        Assert.Equal("current", service.GetLastKnownConfigsForServer("server")!.Plugins.Single().ConfigJson);
        Assert.Equal("cluster", service.GetConfigsForAgent("agent").Single().ConfigJson);
    }

    [Fact]
    public void HistoricalStorageHashesNamesAndPreservesUnsupportedProviderType()
    {
        var registry = Registry();
        registry.UpsertHello(new() { AgentId = "agent", UniqueName = "../../escape" }, "connection", (_, _) => Task.CompletedTask);
        var snapshot = Snapshot();
        snapshot.Plugins[0].ConfigType = "";
        var service = Service(registry);
        service.IngestSnapshot(snapshot, "connection");
        snapshot.Plugins[0].ConfigJson = "mutated";
        var path = Assert.Single(Directory.GetFiles(_directory));
        Assert.Matches("^[A-F0-9]{64}\\.json$", Path.GetFileName(path));
        var stored = service.GetLastKnownConfigsForServer("../../escape")!;
        Assert.Equal("", stored.Plugins.Single().ConfigType);
        Assert.Equal("original", stored.Plugins.Single().ConfigJson);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    public void Dispose()
    {
        _metrics.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
