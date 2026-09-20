using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Quasar.Services.PluginSdk;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ClusterConversionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-conversion-" + Guid.NewGuid());
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previousRoot;
    private readonly ClusterCatalog clusters;
    private readonly DedicatedServerCatalog servers;
    private readonly QuasarConfigProfileCatalog profiles;
    private readonly ServerRestoreCoordinator reservations = new();
    private readonly ClusterConversionService service;

    public ClusterConversionTests()
    {
        previousRoot = cache.GetValue(null); cache.SetValue(null, root);
        Directory.CreateDirectory(Path.Combine(root, "clusters/demo"));
        File.WriteAllText(Path.Combine(root, "clusters/demo/cluster.json"), """
            {"uniqueName":"demo","gatewayUrl":"http://127.0.0.1:28000","gatewayAdminTokenEnvironmentVariable":"ADMIN","goalState":"Off"}
            """);
        clusters = new(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        servers = new(NullLogger<DedicatedServerCatalog>.Instance);
        profiles = new(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var options = new WebServiceOptions { BackupDirectory = Path.Combine(root, "backups") };
        var supervisor = new DedicatedServerSupervisor(servers, null!, null!, null!, null!, options,
            reservations, NullLogger<DedicatedServerSupervisor>.Instance);
        var configs = new PluginConfigService(null!, NullLogger<PluginConfigService>.Instance, Path.Combine(root, "configs"));
        service = new(clusters, servers, supervisor, reservations, profiles, configs, null!, null!, null!, null!, null!,
            new ClusterOperationStore(Path.Combine(root, "operations")), options);
    }

    private async Task Source(DedicatedServerGoalState goal = DedicatedServerGoalState.Off)
    {
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Source" });
        await servers.UpsertAsync(new() { UniqueName = "source", WorldSaveName = "World", ConfigProfileId = "profile", GoalState = goal });
    }

    private ServerToClusterRequest Request() => new(Guid.NewGuid(), "source", service.ReviewServer("source").SourceRevision,
        "1.208", "host", 27016, "JOIN", "TOKENS", ["127.0.0.0/8"],
        [new("host", "http://127.0.0.1:29500", "HOST_TOKEN", "EXECUTOR_TOKEN", "127.0.0.1")]);

    [Fact]
    public async Task ForwardRefusesRunningSourceAndReleasesOfflineReservation()
    {
        await Source(DedicatedServerGoalState.On);
        var request = Request();
        var result = await service.ToClusterAsync("demo", request, "forward", "test", default);
        Assert.Equal(ClusterOperationState.Failed, result.State);
        Assert.Contains("Stop the source", result.Error!.Message);
        Assert.False(reservations.IsRestoreInProgress("source"));
        Assert.Equal("Failed", service.GetStatus(request.Id)!.Phase);
        Assert.Null(clusters.GetCluster("demo")!.ActiveDeployment);
        Assert.Equal(DedicatedServerGoalState.On, servers.GetServer("source")!.GoalState);
    }

    [Fact]
    public async Task ForwardRefusesChangedReviewAndCannotRebindConversionId()
    {
        await Source(); var request = Request();
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Changed" });
        var first = await service.ToClusterAsync("demo", request, "first", "test", default);
        Assert.Equal(ClusterOperationState.Failed, first.State);
        Assert.Contains("settings changed", first.Error!.Message);
        var replay = await service.ToClusterAsync("demo", request, "first", "test", default);
        Assert.Equal(first.OperationId, replay.OperationId);
        var rebound = await service.ToClusterAsync("demo", request with { SteamPort = 27020 }, "another", "test", default);
        Assert.Equal("conversion_identity_conflict", rebound.Error!.Code);
        Assert.Equal(request.SteamPort, service.GetRequest(request.Id).Request.GetProperty("steamPort").GetInt32());
    }

    [Fact]
    public async Task ForwardCannotOverlapAnotherOfflineOperation()
    {
        await Source();
        Assert.True(reservations.TryBeginRestore("source", out var reservation));
        using (reservation)
        {
            var result = await service.ToClusterAsync("demo", Request(), "reserved", "test", default);
            Assert.Equal(ClusterOperationState.Failed, result.State);
            Assert.Contains("offline operation", result.Error!.Message);
            Assert.True(reservations.IsRestoreInProgress("source"));
        }
    }

    [Fact]
    public async Task ReverseRequiresCleanMatchingLifecycleBeforePublishingAnything()
    {
        var request = new ClusterToServerRequest(Guid.NewGuid(), "destination", "Destination", 27017, "profile", "stale");
        var result = await service.ToServerAsync("demo", request, "reverse", "test", default);
        Assert.Equal(ClusterOperationState.Failed, result.State);
        Assert.Contains("cleanly stopped", result.Error!.Message);
        Assert.Empty(servers.GetServers());
        Assert.Empty(profiles.GetProfiles());
    }

    [Fact]
    public async Task ReviewBlocksAccessRestrictionsInsteadOfDroppingThem()
    {
        await Source();
        var profile = profiles.GetProfile("profile")!;
        profile.RootSettings.ServerPassword = "private";
        await profiles.UpsertAsync(profile);
        Assert.Contains(service.ReviewServer("source").Warnings, w => w.Contains("access restrictions"));
    }

    [Fact]
    public void FrozenPluginSelectionRequiresSameInventoryAndExplicitCommit()
    {
        string common = Path.Combine(root, "installation/Dependencies/payload/CommonPlugins");
        void Plugin(string id)
        {
            Directory.CreateDirectory(Path.Combine(common, id));
            File.WriteAllText(Path.Combine(common, id, id + ".xml"),
                $"<PluginData><Id>{id}</Id><Commit>{new string('a', 40)}</Commit></PluginData>");
        }
        Plugin("linux-compat"); Plugin("dotnet-compat"); Plugin("example");
        var profile = new QuasarConfigProfile { Plugins = [new() { PluginId = "example", SelectedVersion = new('a', 40) }] };
        string installation = Path.Combine(root, "installation");
        ClusterConversionService.VerifyPluginSelection(profile, installation);
        profile.Plugins[0].SelectedVersion = new('b', 40);
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.VerifyPluginSelection(profile, installation));
        profile.Plugins.Clear();
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.VerifyPluginSelection(profile, installation));
    }

    [Fact]
    public async Task RegularNodesOwnSeedSlotsEvenWhenGatewayHostAppearsFirst()
    {
        await Source();
        var request = Request() with { Hosts = [new("host", "http://127.0.0.1:29500", "HOST", "EXEC", "127.0.0.1"),
            new("second", "http://127.0.0.2:29500", "HOST2", "EXEC2", "127.0.0.2")] };
        var cluster = clusters.GetCluster("demo")!;
        ClusterConversionService.ValidateTopology(cluster, request);
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateTopology(cluster, request with { Hosts = [request.Hosts[0]] }));
        string world = Path.Combine(root, "split"); Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var paths = request.Hosts.ToDictionary(h => h.HostId, h => new Quasar.Host.Contract.V1.HostConversionPaths(h.HostId, "/world", "/config", "/runtime"));
        using var spec = JsonDocument.Parse(await ClusterConversionService.SpecificationAsync(cluster, servers.GetServer("source")!,
            profiles.GetProfile("profile")!, null, request, world, paths, default));
        var nodes = spec.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2 }, nodes.Where(n => n.GetProperty("role").GetString() == "regular").Select(n => n.GetProperty("catalogSlot").GetInt32()));
        Assert.Equal(3, nodes.Single(n => n.GetProperty("role").GetString() == "WA").GetProperty("catalogSlot").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionPreservesEveryConfigurationTypeAndSingleTypeWireShape(bool multiple)
    {
        await Source(); var request = Request();
        string world = Path.Combine(root, "split"); Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var config = new Magnetar.Protocol.Model.PluginConfigData { PluginId = "plugin", ConfigType = "Plugin.Primary",
            ConfigJson = "{\"values\":{\"enabled\":true}}", AdditionalConfigurations = multiple
                ? [new() { ConfigType = "Plugin.Secondary", ConfigJson = "{\"values\":{\"limit\":5}}" }] : [] };
        var snapshot = new LastKnownPluginConfigSnapshot("source", "agent", DateTimeOffset.UtcNow, [config]);
        var paths = request.Hosts.ToDictionary(h => h.HostId,
            h => new Quasar.Host.Contract.V1.HostConversionPaths(h.HostId, "/world", "/config", "/runtime"));
        using var spec = JsonDocument.Parse(await ClusterConversionService.SpecificationAsync(clusters.GetCluster("demo")!,
            servers.GetServer("source")!, profiles.GetProfile("profile")!, snapshot, request, world, paths, default));
        var plugin = spec.RootElement.GetProperty("pluginConfigurations").GetProperty("plugin");
        var configs = multiple ? plugin.GetProperty("configurations").EnumerateArray().ToArray() : [plugin];
        Assert.Equal(multiple ? 2 : 1, configs.Length);
        Assert.Equal("Plugin.Primary", configs[0].GetProperty("configType").GetString());
        Assert.True(configs[0].GetProperty("configuration").GetProperty("values").GetProperty("enabled").GetBoolean());
        if (multiple)
        {
            Assert.Equal("Plugin.Secondary", configs[1].GetProperty("configType").GetString());
            Assert.Equal(5, configs[1].GetProperty("configuration").GetProperty("values").GetProperty("limit").GetInt32());
        }
        else Assert.False(plugin.TryGetProperty("configurations", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClusterEditorChangesOnlySelectedConfigurationType(bool multiple)
    {
        var primary = System.Text.Json.Nodes.JsonNode.Parse("""
            {"configType":"Primary","configuration":{"schema":{"name":"primary"},"values":{"limit":1}}}
            """)!;
        var other = System.Text.Json.Nodes.JsonNode.Parse("""
            {"configType":"Secondary","configuration":{"schema":{"name":"other"},"values":{"limit":2}}}
            """)!;
        string unchanged = other.ToJsonString();
        var plugin = multiple ? new System.Text.Json.Nodes.JsonObject { ["configurations"] = new System.Text.Json.Nodes.JsonArray(primary, other) } : primary;
        var specification = new System.Text.Json.Nodes.JsonObject {
            ["pluginConfigurations"] = new System.Text.Json.Nodes.JsonObject { ["plugin"] = plugin } };
        Quasar.Components.Dashboard.ClusterDeploymentPanel.SetPluginValues(specification, "plugin", "Primary", "{\"limit\":9}");
        Assert.Equal(9, primary["configuration"]!["values"]!["limit"]!.GetValue<int>());
        Assert.Equal("primary", primary["configuration"]!["schema"]!["name"]!.GetValue<string>());
        Assert.Equal(unchanged, other.ToJsonString());
    }

    public void Dispose()
    {
        clusters.Dispose(); servers.Dispose(); profiles.Dispose(); cache.SetValue(null, previousRoot);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
