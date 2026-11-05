using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.ClusterDeployment;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Quasar.Services.PluginSdk;
using Xunit;
using HostContract = Quasar.Host.Contract.V1;

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
    private readonly ClusterSetupService setup;
    private readonly ClusterCredentialStore credentials;
    private readonly ClusterHostCatalog machines;

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
        credentials = new(new EphemeralDataProtectionProvider(), Path.Combine(root, "credentials.json"));
        machines = new(credentials, Path.Combine(root, "hosts"));
        setup = new(clusters, machines, null!, credentials, null!, null!, null!, null!, null!, profiles, null!,
            new ClusterOperationStore(Path.Combine(root, "setup-operations")), servers, null!) { HostStartWait = TimeSpan.Zero };
        service = new(clusters, servers, supervisor, reservations, profiles, configs, null!, null!, null!, null!, null!,
            new ClusterOperationStore(Path.Combine(root, "operations")), options, null!, setup);
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
    public async Task AutomaticConversionStopsSourceAndAcceptsAnUnpreparedExistingCluster()
    {
        await Source(DedicatedServerGoalState.On);
        var request = ServerToClusterRequest.ForServer(Guid.NewGuid(), "source", createCluster: false);
        var result = await service.ToClusterAsync("demo", request, "automatic", "test", default);
        // No Host is enrolled in this fixture: reaching automatic placement proves no manual inputs are required.
        Assert.Equal(ClusterOperationState.Failed, result.State);
        Assert.Contains("No cluster host is available", result.Error!.Message);
        Assert.Equal(DedicatedServerGoalState.Off, servers.GetServer("source")!.GoalState);
        Assert.False(servers.GetServer("source")!.AutoStart);
        Assert.False(reservations.IsRestoreInProgress("source"));
        Assert.True(service.GetRequest(request.Id).Request.GetProperty("automatic").GetBoolean());
    }

    [Fact]
    public async Task AutomaticConversionCreatesDestinationAndReusesItOnRetry()
    {
        await Source();
        var request = ServerToClusterRequest.ForServer(Guid.NewGuid(), "source", createCluster: true);
        var first = await service.ToClusterAsync("new-cluster", request, "create", "test", default);
        var destination = clusters.GetCluster("new-cluster")!;
        Assert.True(ClusterConversionService.IsEmpty(destination));
        Assert.StartsWith("QSR_MANAGED_", destination.GatewayAdminTokenEnvironmentVariable);
        Assert.NotNull(credentials.Resolve(destination.GatewayAdminTokenEnvironmentVariable));
        var second = await service.ToClusterAsync("new-cluster", request, "retry", "test", default);
        Assert.Equal(first.Error!.Message, second.Error!.Message);
        Assert.Equal(destination.GatewayAdminTokenEnvironmentVariable, clusters.GetCluster("new-cluster")!.GatewayAdminTokenEnvironmentVariable);
        Assert.Single(servers.GetServers());
    }

    [Fact]
    public async Task AutomaticConversionPreservesRunningSourceWhenDestinationIsOccupiedOrAccessCannotTransfer()
    {
        await Source(DedicatedServerGoalState.On);
        await clusters.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var occupied = await service.ToClusterAsync("demo", ServerToClusterRequest.ForServer(Guid.NewGuid(), "source", false), "occupied", "test", default);
        Assert.Contains("cannot be overwritten", occupied.Error!.Message);
        Assert.Equal(DedicatedServerGoalState.On, servers.GetServer("source")!.GoalState);
        await clusters.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        var profile = profiles.GetProfile("profile")!;
        profile.RootSettings.ServerPassword = "private";
        await profiles.UpsertAsync(profile);
        var restricted = await service.ToClusterAsync("demo", ServerToClusterRequest.ForServer(Guid.NewGuid(), "source", false), "restricted", "test", default);
        Assert.Contains("access restrictions", restricted.Error!.Message);
        Assert.Equal(DedicatedServerGoalState.On, servers.GetServer("source")!.GoalState);
        Assert.Null(clusters.GetCluster("demo")!.PackageSelection);
    }

    [Fact]
    public async Task AutomaticResumeKeepsStoppedSourceRevisionAndRefusesLaterEdits()
    {
        await Source();
        var forward = Request();
        string work = Path.Combine(root, "backups/Conversions", forward.Id.ToString("N"));
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "automatic-request.json"), JsonSerializer.Serialize(forward, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var request = ServerToClusterRequest.ForServer(forward.Id, "source", false);
        var first = await service.ToClusterAsync("demo", request, "resume", "test", default);
        // This fixture has only one regular node, so topology validation fails after the revision check passes.
        Assert.Contains("at least two regular nodes", first.Error!.Message);
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Changed" });
        var second = await service.ToClusterAsync("demo", request, "changed", "test", default);
        Assert.Contains("settings changed", second.Error!.Message);
    }

    [Fact]
    public async Task AutomaticPortSelectionAvoidsStandaloneAndReservedClusterRanges()
    {
        await Source();
        var server = servers.GetServer("source")!;
        server.ServerPort = 28000;
        await servers.UpsertAsync(server);
        var machine = await machines.RegisterAsync("local", "Local", "127.0.0.1", 18400, default);
        var placement = new ClusterSetupRequest("new", "New", "", "profile", "local", 28000, [new("local", 2)]);
        int port = setup.FindConversionPort(placement, [machine]);
        Assert.False(new[] { 28000, 29000, 30000 }.Any(p => ClusterSetupService.IsReserved(port, p)));
        var other = await clusters.CreateAsync(new("other", "Other", "http://127.0.0.1:" + (port + 16),
            credentials.Create("cluster:other", "admin")), default);
        int next = setup.FindConversionPort(placement, [machine]);
        Assert.False(ClusterSetupService.ReservedPorts(port).Any(a => ClusterSetupService.ReservedPorts(next)
            .Any(b => a.First <= b.Last && b.First <= a.Last)));
        Assert.True(ClusterConversionService.IsEmpty(other));
    }

    [Fact]
    public async Task AutomaticPlacementUsesExistingHostAndKeepsGeneratedSettingsOnRetry()
    {
        await Source();
        await machines.RegisterAsync("local", "Local", "127.0.0.1", 18400, default);
        using var http = new HttpClient(new WrongHostIdentity());
        var automaticSetup = new ClusterSetupService(clusters, machines, new ClusterHostClient(http, credentials), credentials,
            null!, null!, null!, null!, null!, profiles, null!, new ClusterOperationStore(Path.Combine(root, "auto-setup")), servers, null!)
            { HostStartWait = TimeSpan.Zero };
        var source = servers.GetServer("source")!;
        string work = Path.Combine(root, "placement-test");
        Directory.CreateDirectory(work);
        async Task Attempt() => await Assert.ThrowsAsync<InvalidDataException>(() => clusters.WithLifecycleAsync("demo",
            current => automaticSetup.PrepareConversionAsync(current, source, profiles.GetProfile("profile")!, Guid.NewGuid(),
                service.ReviewServer("source").SourceRevision, work, _ => Task.CompletedTask, default), default));
        await Attempt();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string first = File.ReadAllText(Path.Combine(work, "placement.json"));
        var placement = JsonSerializer.Deserialize<ClusterSetupRequest>(first, json)!;
        Assert.Equal("local", placement.GatewayHost);
        Assert.Equal(2, Assert.Single(placement.Machines).RegularNodes);
        Assert.Equal(1, placement.HotSpares);
        Assert.False(placement.DefaultPlugins);
        var destination = clusters.GetCluster("demo")!;
        Assert.Equal(placement.PlayerPort + 16, new Uri(destination.GatewayUrl).Port);
        Assert.NotNull(credentials.Resolve(destination.GatewayAdminTokenEnvironmentVariable));
        await machines.RegisterAsync("another", "A first host", "10.1.0.2", 18400, default);
        await Attempt();
        Assert.Equal(first, File.ReadAllText(Path.Combine(work, "placement.json")));
        Assert.Equal(destination.GatewayUrl, clusters.GetCluster("demo")!.GatewayUrl);
    }

    private sealed class WrongHostIdentity : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var status = new HostContract.HostStatus("executor", "wrong-host", [], [], GatewayStopFencing: true, SteamClientLibrary: true);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new HostContract.HostEnvelope<HostContract.HostStatus>(
                    HostContract.HostProtocol.Version, DateTimeOffset.UtcNow, status), new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
            };
            response.Headers.Add(HostContract.HostProtocol.HeaderName, HostContract.HostProtocol.Version.ToString());
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task LegacyManualReceiptWithoutAutomaticFieldsRemainsResumable()
    {
        await Source(DedicatedServerGoalState.On);
        var request = Request();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var receipt = JsonSerializer.SerializeToNode(new { cluster = "demo", direction = "to-cluster", request }, json)!;
        receipt["request"]!.AsObject().Remove("automatic");
        receipt["request"]!.AsObject().Remove("createCluster");
        string work = Path.Combine(root, "backups/Conversions", request.Id.ToString("N"));
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "request.json"), receipt.ToJsonString());
        var result = await service.ToClusterAsync("demo", request, "legacy", "test", default);
        Assert.Contains("Stop the source", result.Error!.Message);
    }

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

    [Fact]
    public async Task HotSparesFollowTheRegularNodesOfTheGatewayHost()
    {
        await Source();
        var request = Request() with { Hosts = [new("host", "http://127.0.0.1:29500", "HOST", "EXEC", "127.0.0.1", 2),
            new("second", "http://127.0.0.2:29500", "HOST2", "EXEC2", "127.0.0.2", 1)], HotSpares = 1 };
        string world = Path.Combine(root, "split"); Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var paths = request.Hosts.ToDictionary(h => h.HostId, h => new Quasar.Host.Contract.V1.HostConversionPaths(h.HostId, "/world", "/config", "/runtime"));
        string json = await ClusterConversionService.SpecificationAsync(clusters.GetCluster("demo")!, servers.GetServer("source")!,
            profiles.GetProfile("profile")!, null, request, world, paths, default);
        var nodes = JsonDocument.Parse(json).RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        var spare = Assert.Single(nodes, n => n.TryGetProperty("spare", out _));
        Assert.True(spare.GetProperty("spare").GetBoolean());
        // The spare follows every regular node's catalog slot (the seeded slots 1 and 2 belong to regular nodes).
        Assert.Equal(("host-node-2", "regular", 4, "127.0.0.1:28419"), (spare.GetProperty("nodeId").GetString(), spare.GetProperty("role").GetString(),
            spare.GetProperty("catalogSlot").GetInt32(), spare.GetProperty("backend").GetString()));
        Assert.Equal(3, nodes.Single(n => n.GetProperty("nodeId").GetString() == "second-node-0").GetProperty("catalogSlot").GetInt32());
        Assert.Equal(5, nodes.Single(n => n.GetProperty("role").GetString() == "WA").GetProperty("catalogSlot").GetInt32());
        Assert.Equal(1, ClusterConversionService.HotSpares(json));
        Assert.Equal(0, ClusterConversionService.HotSpares(null));
    }

    [Fact]
    public async Task ChangingHotSparesGivesTheSlotsOfASpecWrittenWithThatCount()
    {
        await Source();
        var request = Request() with { Hosts = [new("host", "http://127.0.0.1:29500", "HOST", "EXEC", "127.0.0.1", 2),
            new("second", "http://127.0.0.2:29500", "HOST2", "EXEC2", "127.0.0.2", 1)] };
        string world = Path.Combine(root, "split"); Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var paths = request.Hosts.ToDictionary(h => h.HostId, h => new Quasar.Host.Contract.V1.HostConversionPaths(h.HostId, "/world", "/config", "/runtime"));
        async Task<System.Text.Json.Nodes.JsonObject> Spec(int spares) => System.Text.Json.Nodes.JsonNode.Parse(await ClusterConversionService.SpecificationAsync(
            clusters.GetCluster("demo")!, servers.GetServer("source")!, profiles.GetProfile("profile")!, null, request with { HotSpares = spares },
            world, paths, default))!.AsObject();
        static string[] Slots(System.Text.Json.Nodes.JsonObject spec) => spec["nodes"]!.AsArray().Select(n =>
            $"{n!["slotKey"]} {n["host"]} {n["role"]} {n["backend"]} {n["control"]} {n["spare"]}").ToArray();
        var spec = await Spec(1);
        foreach (int spares in new[] { 2, 0, 1, 3 })
        {
            ClusterUpdatePreparationService.SetHotSpares(spec, spares);
            Assert.Equal(Slots(await Spec(spares)), Slots(spec));
            Assert.Equal(spares, ClusterConversionService.HotSpares(spec.ToJsonString()));
            var nodes = spec["nodes"]!.AsArray().Select(n => n!).ToArray();
            Assert.Equal(nodes.Length, nodes.Select(n => n["catalogSlot"]!.GetValue<int>()).Distinct().Count());
            // A spare never takes a seeded catalog slot: every one follows the serving regular nodes.
            Assert.All(nodes.Where(n => n["spare"] is not null), spare => Assert.True(spare["catalogSlot"]!.GetValue<int>() > 3));
        }
        Assert.Contains("already has 3", Assert.Throws<InvalidOperationException>(() => ClusterUpdatePreparationService.SetHotSpares(spec, 3)).Message);
        Assert.Throws<InvalidDataException>(() => ClusterUpdatePreparationService.SetHotSpares(spec, 62));
        Assert.Throws<InvalidDataException>(() => ClusterUpdatePreparationService.SetHotSpares(spec, -1));
        spec["nodes"]!.AsArray().Single(n => n!["slotKey"]!.GetValue<string>() == "host-node-1")!["slotKey"] = "custom";
        Assert.Contains("not laid out by Quasar", Assert.Throws<InvalidOperationException>(() => ClusterUpdatePreparationService.SetHotSpares(spec, 1)).Message);
    }

    [Fact]
    public void HotSparesNeedAReleaseThatDeclaresThem()
    {
        string package = Path.Combine(root, "package"); Directory.CreateDirectory(Path.Combine(package, "cli"));
        string capabilities = Path.Combine(package, "cli/deployment-capabilities.json");
        File.WriteAllText(capabilities, """{"schemaVersion":1,"frozenPluginBundles":true,"managedProfileMigration":1}""");
        Assert.Contains("does not support hot spares", Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.RequireHotSpares(package)).Message);
        File.WriteAllText(capabilities, """{"schemaVersion":1,"frozenPluginBundles":true,"managedHotSpares":1}""");
        ClusterDeploymentFiles.RequireHotSpares(package);
    }

    [Fact]
    public void UpdateDespiteADirtyShutdownNeedsAReleaseThatDeclaresIt()
    {
        string package = Path.Combine(root, "package"); Directory.CreateDirectory(Path.Combine(package, "cli"));
        string capabilities = Path.Combine(package, "cli/deployment-capabilities.json");
        File.WriteAllText(capabilities, """{"schemaVersion":1,"frozenPluginBundles":true,"managedHotSpares":1}""");
        Assert.Contains("cannot start an update after a dirty shutdown", Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.RequireRecoveryUpdate(package)).Message);
        File.WriteAllText(capabilities, """{"schemaVersion":1,"frozenPluginBundles":true,"managedRecoveryUpdate":1}""");
        ClusterDeploymentFiles.RequireRecoveryUpdate(package);
    }

    [Fact]
    public async Task TestFrontendAddsDirectTransportAndCanDropSteamButProductionSpecIsSteamOnly()
    {
        await Source(); var request = Request();
        string world = Path.Combine(root, "split"); Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var paths = request.Hosts.ToDictionary(h => h.HostId, h => new Quasar.Host.Contract.V1.HostConversionPaths(h.HostId, "/world", "/config", "/runtime"));
        async Task<JsonElement> Spec(ClusterTestFrontend frontend) => JsonDocument.Parse(await ClusterConversionService.SpecificationAsync(
            clusters.GetCluster("demo")!, servers.GetServer("source")!, profiles.GetProfile("profile")!, null, request, world, paths, default, frontend)).RootElement;

        var production = await Spec(ClusterTestFrontend.Create(null, null));
        Assert.True(production.TryGetProperty("steamListen", out _));
        Assert.False(production.TryGetProperty("directListen", out _));
        Assert.Equal("/runtime/plugin-shared", production.GetProperty("sharedStorageRoot").GetString());

        var both = await Spec(ClusterTestFrontend.Create(" 127.0.0.1:31600 ", "false"));
        Assert.Equal("127.0.0.1:31600", both.GetProperty("directListen").GetString());
        Assert.True(both.TryGetProperty("steamListen", out _));

        var directOnly = await Spec(ClusterTestFrontend.Create("10.0.0.5:31600", "TRUE"));
        Assert.False(directOnly.TryGetProperty("steamListen", out _));
        Assert.False(ClusterTestFrontend.Create("10.0.0.5:31600", "true").UsesSteam);

        // The accept-all frontend never goes on a public address, and a cluster keeps at least one frontend.
        Assert.Throws<InvalidDataException>(() => ClusterTestFrontend.Create("8.8.8.8:31600", null));
        Assert.Throws<InvalidDataException>(() => ClusterTestFrontend.Create("0.0.0.0:31600", null));
        Assert.Throws<InvalidDataException>(() => ClusterTestFrontend.Create("localhost", null));
        Assert.Throws<InvalidDataException>(() => ClusterTestFrontend.Create(null, "true"));
    }

    [Fact]
    public async Task SteamClientLibraryComesFromManagedSteamCmdAndIsNotSentToADirectOnlyCluster()
    {
        string steamCmd = Path.Combine(root, "SteamCMD"), home = Path.Combine(root, "home");
        Assert.Null(ClusterSteamClientLibrary.FindSource(steamCmd, home));
        Directory.CreateDirectory(Path.Combine(home, ".steam/sdk64"));
        File.WriteAllText(Path.Combine(home, ".steam/sdk64/steamclient.so"), "account");
        Assert.Equal(Path.Combine(home, ".steam/sdk64/steamclient.so"), ClusterSteamClientLibrary.FindSource(steamCmd, home));
        Directory.CreateDirectory(Path.Combine(steamCmd, "linux64"));
        File.WriteAllText(Path.Combine(steamCmd, "linux64/steamclient.so"), "managed");
        Assert.Equal(Path.Combine(steamCmd, "linux64/steamclient.so"), ClusterSteamClientLibrary.FindSource(steamCmd, home));
        // Direct Transport only: no Host call at all (a null client would throw).
        Assert.Null(await ClusterSteamClientLibrary.ProvisionAsync(null!, new() { UniqueName = "demo" },
            ClusterTestFrontend.Create("127.0.0.1:31600", "true"), null, default));
    }

    [Fact]
    public async Task SpecificationEmitsNumericAdministratorsAndRejectsValuesTheGatewayRefuses()
    {
        await Source(); var request = Request();
        string world = Path.Combine(root, "split"); Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var paths = request.Hosts.ToDictionary(h => h.HostId, h => new Quasar.Host.Contract.V1.HostConversionPaths(h.HostId, "/world", "/config", "/runtime"));
        var profile = profiles.GetProfile("profile")!;
        profile.RootSettings.Administrators = [" 76561198000000001 ", "76561198000000002", ""];
        using var spec = JsonDocument.Parse(await ClusterConversionService.SpecificationAsync(clusters.GetCluster("demo")!,
            servers.GetServer("source")!, profile, null, request, world, paths, default));
        var administrators = spec.RootElement.GetProperty("administrators").EnumerateArray().ToArray();
        Assert.All(administrators, a => Assert.Equal(JsonValueKind.Number, a.ValueKind));
        Assert.Equal(new[] { 76561198000000001UL, 76561198000000002UL }, administrators.Select(a => a.GetUInt64()));

        profile.RootSettings.Administrators = ["admin-name"];
        Assert.Contains("admin-name", Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateAdmission(profile)).Message);
        profile.RootSettings.Administrators = ["12345"];
        Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateAdmission(profile));
        profile.RootSettings.Administrators = [];
        profile.SessionSettings.MaxPlayers = 1;
        Assert.Contains("at least 2", Assert.Throws<InvalidDataException>(() => ClusterConversionService.ValidateAdmission(profile)).Message);
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
        ClusterUpdatePreparationService.SetPluginValues(specification, "plugin", "Primary", "{\"limit\":9}");
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
