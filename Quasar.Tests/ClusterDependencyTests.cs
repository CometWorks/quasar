using System.Security.Claims;
using System.Text.Json;
using Quasar.ClusterDeployment;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster storage")]
public sealed class ClusterDependencyTests
{
    [Fact]
    public void ProfileSelectionAcceptsSessionAndContentChangesButRejectsRootSettings()
    {
        var current = new QuasarConfigProfile();
        var selected = new QuasarConfigProfile();
        selected.SessionSettings.MaxPlayers = 40;
        ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected);
        selected.SessionSettings.InventorySizeMultiplier = 10;
        selected.Mods.Add(new QuasarModSelection { WorkshopId = 123456789 });
        selected.Plugins.Add(new QuasarPluginSelection { PluginId = "example", SelectedVersion = new string('a', 40) });
        selected.RootSettings.Administrators.Add("76561198000000000");
        ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected);
        selected.RootSettings.ServerPassword = "unsupported";
        Assert.Throws<InvalidOperationException>(() =>
            ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected));
    }

    [Fact]
    public void ModUpdatesAcceptUnchangedNondefaultRootSettings()
    {
        var current = new QuasarConfigProfile();
        current.RootSettings.AutodetectDependencies = false;
        current.RootSettings.ServerDescription = "Existing cluster";
        current.RootSettings.PauseGameWhenEmpty = true;
        current.RootSettings.AutoRestartTimeInMin = 120;
        current.Mods = [new() { WorkshopId = 123 }, new() { WorkshopId = 456 }];
        var selected = JsonSerializer.Deserialize<QuasarConfigProfile>(JsonSerializer.Serialize(current))!;

        // Same-profile content staging uses the existing settings, not factory defaults.
        ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, current);
        ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected);

        selected.RootSettings.Administrators.Add("76561198000000000");
        ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected);
        selected.RootSettings.NetworkType = QuasarNetworkType.EOS;
        Assert.Throws<InvalidOperationException>(() =>
            ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected));
        selected.RootSettings.NetworkType = current.RootSettings.NetworkType;
        selected.RootSettings.ServerDescription = "Changed cluster";
        Assert.Throws<InvalidOperationException>(() =>
            ClusterUpdatePreparationService.EnsureCompatibleProfileSelection(current, selected));
    }

    [Fact]
    public void StagedProfileActivationRejectsChangesAndMissingProfiles()
    {
        var selected = new QuasarConfigProfile { ConfigProfileId = "survival" };
        var prepared = new ClusterPreparedProfile("survival", "candidate",
            ClusterUpdatePreparationService.ProfileHash(selected));
        ClusterDeploymentService.EnsurePreparedProfileCurrent(prepared, "candidate", selected);
        selected.SessionSettings.MaxPlayers++;
        Assert.Throws<InvalidOperationException>(() =>
            ClusterDeploymentService.EnsurePreparedProfileCurrent(prepared, "candidate", selected));
        Assert.Throws<InvalidOperationException>(() =>
            ClusterDeploymentService.EnsurePreparedProfileCurrent(prepared, "candidate", null));
        ClusterDeploymentService.EnsurePreparedProfileCurrent(prepared, "another-revision", null);
    }

    [Theory]
    [InlineData(1700000010L, null, true)]
    [InlineData(1700000011L, null, false)]
    [InlineData(null, "Workshop unavailable", false)]
    public void QueuedModsRequireTheRequestedTimestamp(long? timestamp, string? error, bool valid)
    {
        var queue = new ClusterContentUpdateQueue("active", DateTimeOffset.UtcNow,
            [new("mod", "123", "Mod", "1700000010", DateTimeOffset.UtcNow)]);
        var mods = new[] { new QuasarModSelection { WorkshopId = 123 } };
        var observations = new Dictionary<long, WorkshopUpdateObservation> { [123] = new(123, "Mod", timestamp, error) };
        if (valid) ClusterUpdatePreparationService.ValidateQueuedMods(queue, mods, observations);
        else Assert.Throws<InvalidOperationException>(() => ClusterUpdatePreparationService.ValidateQueuedMods(queue, mods, observations));
        Assert.Throws<InvalidOperationException>(() => ClusterUpdatePreparationService.ValidateQueuedMods(queue, [], observations));
    }

    [Theory]
    [InlineData('b', true)]
    [InlineData('c', false)]
    public void QueuedPluginsRequireTheExportedCommit(char commit, bool valid)
    {
        var queue = new ClusterContentUpdateQueue("active", DateTimeOffset.UtcNow,
            [new("plugin", "example", "Example", new string('b', 40), DateTimeOffset.UtcNow)]);
        var export = System.Text.Json.Nodes.JsonNode.Parse($$"""{"plugins":[{"id":"example","commit":"{{new string(commit, 40)}}"}]}""")!.AsObject();
        if (valid) ClusterUpdatePreparationService.ValidateQueuedPlugins(queue, export);
        else Assert.Throws<InvalidOperationException>(() => ClusterUpdatePreparationService.ValidateQueuedPlugins(queue, export));
        Assert.Throws<InvalidOperationException>(() => ClusterUpdatePreparationService.ValidateQueuedPlugins(queue,
            System.Text.Json.Nodes.JsonNode.Parse("{\"plugins\":[]}")!.AsObject()));
    }

    [LinuxFact]
    public async Task SavingPluginConfigurationStagesEveryHostAndRetainsActiveRevision()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        string hash = ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions));
        string credential = "CONFIG_STAGE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credential, "token");
        try
        {
            fixture.Cluster.ActiveDeployment = new("active", [], DateTimeOffset.UtcNow, fixture.Cluster.PackageSelection!.Version);
            fixture.Cluster.Preparation = new(hash, """
                {"clusterId":"demo","hosts":[{"id":"one"},{"id":"two"}],
                "pluginConfigurations":{"plugin":{"configType":"Primary","configuration":{"values":{"limit":1}}}}}
                """, "http://gateway.test", new[] { "one", "two" }.Select(id => new ClusterPreparationHost(
                    id, "http://" + id, credential, credential, "/installation", "/world", "/configuration/original")).ToArray());
            string directory = Path.Combine(fixture.Root, "clusters");
            Directory.CreateDirectory(Path.Combine(directory, "demo"));
            File.WriteAllText(Path.Combine(directory, "demo", "cluster.json"), JsonSerializer.Serialize(fixture.Cluster, ClusterDeploymentFiles.JsonOptions));
            using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = directory }).Build());
            using var handler = new ConfigurationPreparationHandler();
            using var http = new HttpClient(handler);
            var hosts = new ClusterHostClient(http);
            var deployments = new ClusterDeploymentService(catalog, hosts, new ClusterOperationStore(Path.Combine(fixture.Root, "operations")));
            var preparation = new ClusterUpdatePreparationService(catalog, fixture.Service, hosts, deployments,
                null!, null!, null!, null!, null!, NullLogger<ClusterUpdatePreparationService>.Instance);
            var result = await preparation.StagePluginConfigurationAsync("demo", "plugin", "Primary", "{\"limit\":9}", "test", default);
            Assert.Equal(ClusterOperationState.Succeeded, result.State);
            var saved = catalog.GetCluster("demo")!;
            Assert.Equal("active", saved.ActiveDeployment!.Revision);
            Assert.True(saved.HasPreparedUpdate);
            Assert.True(saved.PreparedForSelectedRelease);
            Assert.Equal(new[] { "one", "two" }, handler.Hosts);
            Assert.All(saved.Preparation!.Hosts, host => Assert.Contains("config-", host.ConfigurationDirectory));
            using var spec = JsonDocument.Parse(saved.Preparation.SpecificationJson);
            Assert.Equal(9, spec.RootElement.GetProperty("pluginConfigurations").GetProperty("plugin")
                .GetProperty("configuration").GetProperty("values").GetProperty("limit").GetInt32());
        }
        finally { Environment.SetEnvironmentVariable(credential, null); }
    }

    [Fact]
    public void ConfigurationDirectoryIsKeyedOnSpecificationAndInputs()
    {
        // A rebuilt dev-folder plugin changes only the inputs; it must not reuse the active deployment's directory (#1359).
        string a = new('a', 64), b = new('b', 64), spec = "{\"clusterId\":\"demo\"}";
        string active = ClusterUpdatePreparationService.ConfigurationDirectory("/conversions/x/configuration", spec, a);
        Assert.Equal("/conversions/x", Path.GetDirectoryName(active));
        Assert.StartsWith("config-", Path.GetFileName(active));
        Assert.Equal(active, ClusterUpdatePreparationService.ConfigurationDirectory(active, spec, a));
        Assert.NotEqual(active, ClusterUpdatePreparationService.ConfigurationDirectory(active, spec, b));
        Assert.NotEqual(active, ClusterUpdatePreparationService.ConfigurationDirectory(active, "{\"clusterId\":\"other\"}", a));
    }

    private static ClusterPreparationRequest Preparation(string inputs, string direct, int limit = 1) => new(inputs, $$"""
        {"clusterId":"demo","hosts":[{"id":"one"}],"steamListen":"0.0.0.0:27016","directListen":"{{direct}}",
        "pluginConfigurations":{"plugin":{"configType":"Primary","configuration":{"values":{"limit":{{limit}} } } } } }
        """, "http://gateway.test", [new("one", "http://one", "TOKEN", "TOKEN", "/installation", "/world", "/configuration/original")]);

    [Fact]
    public void ReprepareBuildsOnTheActivePreparationUnlessTheSavedOneIsActiveOrStaged()
    {
        var active = Preparation(new string('a', 64), "192.168.1.80:27116");
        var stale = Preparation(new string('a', 64), "192.168.1.80:8116");
        var selection = new ClusterPackageSelection(1, "0.1.1296", new string('b', 64), new string('c', 40), "package");
        var cluster = new ClusterDefinition { UniqueName = "demo", Preparation = stale,
            ActiveDeployment = new(ClusterDependencyService.DeploymentRevision(active), [], DateTimeOffset.UtcNow)
            { Provenance = new(selection, new string('d', 64), active) } };
        var service = new ClusterUpdatePreparationService(null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ClusterUpdatePreparationService>.Instance);
        var messages = new List<string>();
        var progress = new SynchronousProgress(messages.Add);

        // A failed restore left its backup's specification saved, with its prepared deployment still recorded.
        cluster.PreparedDeployment = new(cluster.ActiveDeployment.Revision, ClusterDependencyService.DeploymentRevision(stale), []);
        Assert.Same(active, service.BasePreparation(cluster, progress));
        Assert.Contains("Direct Transport stays 192.168.1.80:27116 (the saved preparation had 192.168.1.80:8116).", Assert.Single(messages));
        Assert.DoesNotContain("Steam stays", messages[0]);

        // A change staged by Quasar on top of the active deployment stays the base, and so does the active one.
        cluster.PreparedForSelectedRelease = true;
        Assert.Same(stale, service.BasePreparation(cluster, progress));
        cluster.Preparation = active with { Hosts = [.. active.Hosts] };
        cluster.PreparedForSelectedRelease = false;
        Assert.Same(cluster.Preparation, service.BasePreparation(cluster, progress));
        // A catalog from before provenance keeps its saved preparation.
        cluster.Preparation = stale;
        cluster.ActiveDeployment = cluster.ActiveDeployment with { Provenance = null };
        Assert.Same(stale, service.BasePreparation(cluster, progress));
        Assert.Single(messages);
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }

    [LinuxFact]
    public async Task StagingAfterAFailedRestoreKeepsTheActiveFrontends()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        string hash = ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions));
        string credential = "CONFIG_STAGE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credential, "token");
        try
        {
            ClusterPreparationRequest Prepared(string direct) => Preparation(hash, direct) with { Hosts =
                [new("one", "http://one", credential, credential, "/installation", "/world", "/configuration/original")] };
            var active = Prepared("192.168.1.80:27116");
            var restore = Prepared("192.168.1.80:8116");
            fixture.Cluster.ActiveDeployment = new(ClusterDependencyService.DeploymentRevision(active), [], DateTimeOffset.UtcNow,
                fixture.Cluster.PackageSelection!.Version)
                { Provenance = new(fixture.Cluster.PackageSelection, candidate.ManifestSha256, active) };
            fixture.Cluster.Preparation = restore;
            fixture.Cluster.PreparedDeployment = new(fixture.Cluster.ActiveDeployment.Revision, ClusterDependencyService.DeploymentRevision(restore), []);
            string directory = Path.Combine(fixture.Root, "clusters");
            Directory.CreateDirectory(Path.Combine(directory, "demo"));
            File.WriteAllText(Path.Combine(directory, "demo", "cluster.json"), JsonSerializer.Serialize(fixture.Cluster, ClusterDeploymentFiles.JsonOptions));
            using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = directory }).Build());
            using var handler = new ConfigurationPreparationHandler();
            using var http = new HttpClient(handler);
            var hosts = new ClusterHostClient(http);
            var deployments = new ClusterDeploymentService(catalog, hosts, new ClusterOperationStore(Path.Combine(fixture.Root, "operations")));
            var preparation = new ClusterUpdatePreparationService(catalog, fixture.Service, hosts, deployments,
                null!, null!, null!, null!, null!, NullLogger<ClusterUpdatePreparationService>.Instance);
            var result = await preparation.StagePluginConfigurationAsync("demo", "plugin", "Primary", "{\"limit\":9}", "test", default);
            Assert.Equal(ClusterOperationState.Succeeded, result.State);
            using var spec = JsonDocument.Parse(catalog.GetCluster("demo")!.Preparation!.SpecificationJson);
            Assert.Equal("192.168.1.80:27116", spec.RootElement.GetProperty("directListen").GetString());
            Assert.Equal(9, spec.RootElement.GetProperty("pluginConfigurations").GetProperty("plugin")
                .GetProperty("configuration").GetProperty("values").GetProperty("limit").GetInt32());
        }
        finally { Environment.SetEnvironmentVariable(credential, null); }
    }

    [LinuxFact]
    public async Task RenamingStagesNewInGameNamesAndKeepsTheIdentity()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        string hash = ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions));
        string credential = "CONFIG_STAGE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credential, "token");
        try
        {
            ClusterPreparationRequest Prepared(string direct) => Preparation(hash, direct) with { Hosts =
                [new("one", "http://one", credential, credential, "/installation", "/world", "/configuration/original")] };
            var active = Prepared("192.168.1.80:27116");
            var restore = Prepared("192.168.1.80:8116");
            fixture.Cluster.ActiveDeployment = new(ClusterDependencyService.DeploymentRevision(active), [], DateTimeOffset.UtcNow,
                fixture.Cluster.PackageSelection!.Version)
                { Provenance = new(fixture.Cluster.PackageSelection, candidate.ManifestSha256, active) };
            fixture.Cluster.Preparation = restore;
            fixture.Cluster.PreparedDeployment = new(fixture.Cluster.ActiveDeployment.Revision, ClusterDependencyService.DeploymentRevision(restore), []);
            string directory = Path.Combine(fixture.Root, "clusters");
            Directory.CreateDirectory(Path.Combine(directory, "demo"));
            File.WriteAllText(Path.Combine(directory, "demo", "cluster.json"), JsonSerializer.Serialize(fixture.Cluster, ClusterDeploymentFiles.JsonOptions));
            using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = directory }).Build());
            using var handler = new ConfigurationPreparationHandler();
            using var http = new HttpClient(handler);
            var hosts = new ClusterHostClient(http);
            var deployments = new ClusterDeploymentService(catalog, hosts, new ClusterOperationStore(Path.Combine(fixture.Root, "operations")));
            var preparation = new ClusterUpdatePreparationService(catalog, fixture.Service, hosts, deployments,
                null!, null!, null!, null!, null!, NullLogger<ClusterUpdatePreparationService>.Instance);
            await Assert.ThrowsAsync<ArgumentException>(() => preparation.StageNamesAsync("demo", " ", null, "test", default));
            var result = await preparation.StageNamesAsync("demo", null, " Octahedron ", "test", default);
            Assert.Equal(ClusterOperationState.Succeeded, result.State);
            using var spec = JsonDocument.Parse(catalog.GetCluster("demo")!.Preparation!.SpecificationJson);
            // SE-755: only the names change; a blank server name stays as it was.
            Assert.Equal("Octahedron", spec.RootElement.GetProperty("worldName").GetString());
            Assert.False(spec.RootElement.TryGetProperty("serverName", out _));
            Assert.Equal("demo", spec.RootElement.GetProperty("clusterId").GetString());
            Assert.Equal("192.168.1.80:27116", spec.RootElement.GetProperty("directListen").GetString());
        }
        finally { Environment.SetEnvironmentVariable(credential, null); }
    }

    private sealed class ConfigurationPreparationHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Contains("/deployment-preparations/", request.RequestUri!.AbsolutePath);
            Hosts.Add(request.RequestUri.Host);
            var data = new Quasar.Host.Contract.V1.HostPreparedConfiguration(request.RequestUri.Host, "candidate", "/manifest.json", new string('c', 64));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(
                new Quasar.Host.Contract.V1.HostEnvelope<Quasar.Host.Contract.V1.HostPreparedConfiguration>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add(Quasar.Host.Contract.V1.HostProtocol.HeaderName, "1");
            return Task.FromResult(response);
        }
    }

    // #1313: world Storage files staged as a new world seed revision; each Host receives only the changed files.
    [LinuxFact]
    public async Task StagingWorldFilesSendsOnlyChangedFilesAndPinsTheNewSeed()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        string hash = ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions));
        const string mes = "Storage/1521905890.sbm_ModularEncountersSystems/Config-General.xml";
        string world = Path.Combine(fixture.Root, "world");
        foreach (var (name, text) in new[] { ("Sandbox.sbc", "<checkpoint/>"), (mes, "<MaxNpcGrids>100</MaxNpcGrids>"),
                     ("Storage/111.sbm_Other/state.dat", "state"), ("SANDBOX_0_0_0_.sbs", "sector") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(world, name))!);
            File.WriteAllText(Path.Combine(world, name), text);
        }
        string seedArchive = Path.Combine(fixture.Root, "seed.tar");
        string seedHash = await ClusterWorldFiles.PackAsync(world, seedArchive, default);
        var seedPins = (await ClusterDeploymentFiles.InspectAsync(world, default)).ToDictionary(p => p.Key, p => p.Value.Sha256);
        using var handler = new WorldFilesHandler(Path.Combine(fixture.Root, "hosts"));
        foreach (string host in new[] { "one", "two" })
            await using (var input = File.OpenRead(seedArchive))
                await ClusterWorldFiles.UnpackAsync(input, seedHash, handler.Worlds(host), default);
        string credential = "WORLD_FILES_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credential, "token");
        try
        {
            var preparationHosts = new[] { "one", "two" }.Select(id => new ClusterPreparationHost(id, "http://" + id, credential, credential,
                "/installation", Path.Combine(handler.Worlds(id), seedHash), "/configuration/original")).ToArray();
            fixture.Cluster.Preparation = new(hash, JsonSerializer.Serialize(new { clusterId = "demo", hosts = new[] { new { id = "one" }, new { id = "two" } }, worldFiles = seedPins }),
                "http://gateway.test", preparationHosts);
            fixture.Cluster.ActiveDeployment = new(ClusterDependencyService.DeploymentRevision(fixture.Cluster.Preparation),
                preparationHosts.Select(h => new ClusterHostRevision(h.HostId, h.CommandUrl, credential,
                    new("demo", "active", new string('a', 64), null!, null))).ToArray(), DateTimeOffset.UtcNow, fixture.Cluster.PackageSelection!.Version);
            string directory = Path.Combine(fixture.Root, "clusters");
            Directory.CreateDirectory(Path.Combine(directory, "demo"));
            File.WriteAllText(Path.Combine(directory, "demo", "cluster.json"), JsonSerializer.Serialize(fixture.Cluster, ClusterDeploymentFiles.JsonOptions));
            using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = directory }).Build());
            using var http = new HttpClient(handler);
            var hosts = new ClusterHostClient(http);
            var deployments = new ClusterDeploymentService(catalog, hosts, new ClusterOperationStore(Path.Combine(fixture.Root, "operations")));
            var preparation = new ClusterUpdatePreparationService(catalog, fixture.Service, hosts, deployments,
                null!, null!, null!, null!, null!, NullLogger<ClusterUpdatePreparationService>.Instance);
            Assert.Equal(new[] { mes, "Storage/111.sbm_Other/state.dat" }.Order(StringComparer.Ordinal), preparation.GetWorldFiles("demo").Keys);
            await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.StageWorldFilesAsync("demo",
                new Dictionary<string, byte[]> { [mes] = "<MaxNpcGrids>100</MaxNpcGrids>"u8.ToArray() }, "test", default));

            byte[] updated = "<MaxNpcGrids>40</MaxNpcGrids>"u8.ToArray();
            var result = await preparation.StageWorldFilesAsync("demo", new Dictionary<string, byte[]> { [mes] = updated }, "test", default);
            Assert.Equal(ClusterOperationState.Succeeded, result.State);
            Assert.Equal(new[] { mes, mes }, handler.Transferred); // One changed file per Host, nothing else.
            var saved = catalog.GetCluster("demo")!;
            Assert.True(saved.HasPreparedUpdate);
            using var spec = JsonDocument.Parse(saved.Preparation!.SpecificationJson);
            var pins = spec.RootElement.GetProperty("worldFiles").Deserialize<Dictionary<string, string>>()!;
            Assert.Equal(ClusterDeploymentFiles.Hash(updated), pins[mes]);
            Assert.Equal(seedPins.Keys.Order(), pins.Keys.Order());
            foreach (var host in saved.Preparation.Hosts)
            {
                // The Host's new seed is the old one plus the file, exactly as the spec pins it.
                var actual = await ClusterDeploymentFiles.InspectAsync(host.WorldDirectory, default);
                Assert.Equal(pins, actual.ToDictionary(p => p.Key, p => p.Value.Sha256));
                Assert.Equal("<MaxNpcGrids>40</MaxNpcGrids>", File.ReadAllText(Path.Combine(host.WorldDirectory, mes)));
                Assert.Contains("config-", host.ConfigurationDirectory);
            }
            Assert.Equal(preparation.GetWorldFiles("demo")[mes], ClusterDeploymentFiles.Hash(updated));
        }
        finally { Environment.SetEnvironmentVariable(credential, null); }
    }

    [Theory]
    [InlineData("Storage/1521905890.sbm_ModularEncountersSystems/Config-General.xml", true)]
    [InlineData("Storage/mod/sub/file.txt", true)]
    [InlineData("Sandbox.sbc", false)]
    [InlineData("Storage/file.txt", false)]
    [InlineData("Storage/../Sandbox.sbc", false)]
    [InlineData("Storage//file.txt", false)]
    [InlineData("/Storage/mod/file.txt", false)]
    [InlineData("Storage\\mod\\file.txt", false)]
    [InlineData("Mods/123.sbm/file.txt", false)]
    public void WorldFilesMustBeModWorldStorage(string path, bool valid)
    {
        var files = new Dictionary<string, byte[]> { [path] = [1] };
        if (valid) ClusterUpdatePreparationService.ValidateWorldFiles(files);
        else Assert.Throws<ArgumentException>(() => ClusterUpdatePreparationService.ValidateWorldFiles(files));
    }

    [Fact]
    public void WorldFilesHaveSizeLimits()
    {
        Assert.Throws<ArgumentException>(() => ClusterUpdatePreparationService.ValidateWorldFiles(new Dictionary<string, byte[]>()));
        Assert.Throws<ArgumentException>(() => ClusterUpdatePreparationService.ValidateWorldFiles(new Dictionary<string, byte[]>
            { ["Storage/mod/big.bin"] = new byte[ClusterUpdatePreparationService.MaxWorldFileBytes + 1] }));
    }

    /// <summary>A Host's world input routes, backed by the real archive code.</summary>
    private sealed class WorldFilesHandler(string root) : HttpMessageHandler
    {
        public List<string> Transferred { get; } = [];
        public string Worlds(string host) => Path.Combine(root, host, "worlds");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string host = request.RequestUri!.Host, path = request.RequestUri.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            object data;
            if (path.Contains("/deployment-worlds/"))
                data = await ClusterWorldFiles.ReadPinsAsync(Worlds(host), path.Split('/')[^1], token);
            else if (path.Contains("/conversion-inputs/") && path.EndsWith("/world"))
            {
                using var buffer = new MemoryStream();
                await request.Content!.CopyToAsync(buffer, token);
                buffer.Position = 0;
                using (var reader = new System.Formats.Tar.TarReader(buffer, leaveOpen: true))
                    while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
                        if (entry.Name != "world-files.json") Transferred.Add(entry.Name);
                buffer.Position = 0;
                string directory = await ClusterWorldFiles.UnpackAsync(buffer, query["sha256"]!, Worlds(host), token,
                    baseHash: query["baseInputsSha256"]);
                data = new Quasar.Host.Contract.V1.HostConversionPaths(host, directory, "/c", "/r");
            }
            else if (path.Contains("/deployment-preparations/"))
                data = new Quasar.Host.Contract.V1.HostPreparedConfiguration(host, "candidate", "/manifest.json", new string('c', 64));
            else throw new InvalidOperationException("Unexpected Host route " + path);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(
                new Quasar.Host.Contract.V1.HostEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add(Quasar.Host.Contract.V1.HostProtocol.HeaderName, "1");
            return response;
        }
    }

    [LinuxFact]
    public async Task ContentInventoryMustMatchActivePreparationAndVerifiedDependencies()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        string hash = ClusterDeploymentFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions));
        fixture.Cluster.Preparation = new(hash, "{}", "http://gateway.test", []);
        fixture.Cluster.ActiveDeployment = new(ClusterDependencyService.DeploymentRevision(fixture.Cluster.Preparation), [], DateTimeOffset.UtcNow);
        var pins = await fixture.Service.ReadActivePluginPinsAsync(fixture.Cluster, default);
        Assert.Equal(3, pins.Length);
        Assert.Equal(new string('c', 40), Assert.Single(pins, p => p.Id == "direct-transport").Commit);
        fixture.Cluster.Preparation = fixture.Cluster.Preparation with { SpecificationJson = "{\"candidate\":true}" };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ReadActivePluginPinsAsync(fixture.Cluster, default));
        fixture.Cluster.Preparation = fixture.Cluster.Preparation with { SpecificationJson = "{}" };
        string metadata = Path.Combine(inputs.DependencyDirectory, "payload/CommonPlugins/linux-compat/linux-compat.xml");
        File.SetUnixFileMode(metadata, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.AppendAllText(metadata, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ReadActivePluginPinsAsync(fixture.Cluster, default));
    }

    [Fact]
    public void ClusterSdkPinRejectsMalformedReleaseMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-sdk-pin-" + Guid.NewGuid());
        string cli = Path.Combine(root, "cli");
        Directory.CreateDirectory(cli);
        string capability = Path.Combine(cli, "deployment-capabilities.json");
        try
        {
            string hash = new('a', 64);
            File.WriteAllText(capability, $$"""{"pluginServices":1,"pluginSdkSha256":"{{hash}}"}""");
            Assert.Equal((null, hash), ClusterDeploymentFiles.GetPluginSdkPin(root));
            File.WriteAllText(capability, """{"pluginServices":1,"pluginSdkSha256":"latest"}""");
            Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.GetPluginSdkPin(root));
            File.WriteAllText(capability, """{"pluginServices":1}""");
            Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.GetPluginSdkPin(root));
            File.WriteAllText(capability, $$"""{"pluginServices":1,"pluginSdkContract":"1","pluginSdkSha256":"{{hash}}"}""");
            Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.GetPluginSdkPin(root));
            File.WriteAllText(capability, """{"pluginServices":1,"pluginSdkContract":1.2}""");
            Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.GetPluginSdkPin(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [LinuxFact]
    public async Task SteamNativeLibraryBelongsToMagnetarRelease()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.False(File.Exists(Path.Combine(fixture.Sources["CommonPlugins"], "linux-compat/libsteam_api.so")));
        await fixture.Service.InspectAsync(fixture.Cluster, default);
        File.Delete(Path.Combine(fixture.Sources["Magnetar"], "Libraries/MagnetarInterim/libsteam_api.so"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
    }

    [LinuxFact]
    public async Task CopiesApprovedBytesAndReusesThemOfflineAfterSourcesChangeOrDisappear()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        var request = new ClusterDependencyRequest(candidate.ManifestSha256, 1, null);
        var copies = await Task.WhenAll(fixture.Service.StageAsync(fixture.Cluster, request, default),
            fixture.Service.StageAsync(fixture.Cluster, request, default));
        Assert.All(copies, copy => Assert.Equal(candidate, copy));
        string payload = Path.Combine(fixture.Store, candidate.ManifestSha256, "payload");
        string ds = Path.Combine(payload, "DedicatedServer/DedicatedServer64/SpaceEngineers.Game.dll");
        Assert.Equal("fixture", File.ReadAllText(ds));
        File.WriteAllText(Path.Combine(fixture.Sources["DedicatedServer/DedicatedServer64"], "SpaceEngineers.Game.dll"), "updated");
        Assert.Equal("fixture", File.ReadAllText(ds));
        Directory.Delete(Path.Combine(fixture.Root, "inputs"), true);
        Assert.Equal(candidate, await fixture.Service.StageAsync(fixture.Cluster, request, default));
        Assert.Equal(candidate, await fixture.Service.VerifyAsync(fixture.Cluster, candidate.ManifestSha256, default));
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var reused = await fixture.Service.ReusableSourcesAsync(fixture.Cluster, default);
        Assert.Equal(candidate, await fixture.Service.InspectAsync(fixture.Cluster, reused, default));
        Assert.Empty(Directory.GetDirectories(fixture.Store, ".stage-*"));
        File.SetUnixFileMode(ds, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(ds, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.VerifyAsync(fixture.Cluster, candidate.ManifestSha256, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Cluster, request, default));
    }

    [LinuxFact]
    public async Task ChangedInputsCannotBeProvisionedUnderAnOldApproval()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        File.WriteAllText(Path.Combine(fixture.Sources["DedicatedServer/Content"], "world.sbc"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Cluster,
            new(candidate.ManifestSha256, 1, null), default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Store, candidate.ManifestSha256)));
        Assert.NotEqual(candidate.ManifestSha256, (await fixture.Service.InspectAsync(fixture.Cluster, default)).ManifestSha256);
    }

    [LinuxFact]
    public async Task IncompleteUnpinnedAndLinkedInputsFailClosed()
    {
        using var fixture = await Fixture.CreateAsync();
        string metadata = Path.Combine(fixture.Sources["DirectTransport"], "DirectTransport.xml");
        File.WriteAllText(metadata, "<PluginData><Id>direct-transport</Id><Commit>TODO</Commit></PluginData>");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        File.Copy(metadata + ".original", metadata, true);
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("CoreCLR", "NETCoreApp"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        File.Copy(metadata + ".original", metadata, true);
        string extra = Path.Combine(fixture.Sources["DirectTransport"], "linked.dll");
        File.CreateSymbolicLink(extra, metadata);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        File.Delete(extra);
        File.Delete(Path.Combine(fixture.Sources["DirectTransport"], "LiteNetLib.dll"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
    }

    [LinuxFact]
    public async Task ProvisioningPersistsFencedSelectionWithoutActivatingAndReportsTampering()
    {
        using var fixture = await Fixture.CreateAsync();
        string catalogPath = Path.Combine(fixture.Root, "catalog");
        Directory.CreateDirectory(Path.Combine(catalogPath, "demo"));
        File.WriteAllText(Path.Combine(catalogPath, "demo", "cluster.json"), """
        {"uniqueName":"demo","gatewayUrl":"http://gateway.test","goalState":"On","updatedAtUtc":"2026-09-19T12:00:00Z"}
        """);
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Quasar:ClusterCatalogPath"] = catalogPath }).Build());
        var installed = await fixture.Packages.Service.GetInstalledAsync(fixture.Packages.Request, default);
        await catalog.SelectPackageAsync("demo", 0, installed, "package", default);
        var before = catalog.GetCluster("demo")!;
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "deps-1";
        var operations = new ClusterOperationStore(Path.Combine(fixture.Root, "operations"));
        var candidate = Data<ClusterDependencyCandidate>(await ClusterApi.GetDependencyCandidate(
            "demo", context, catalog, fixture.Service, default));
        var request = new ClusterDependencyRequest(candidate.ManifestSha256, 1, null);
        var op = Data<ClusterOperation>(await ClusterApi.StageDependencies("demo", request,
            context, catalog, fixture.Service, operations, default));
        Assert.Equal(ClusterOperationState.Succeeded, op.State);
        Assert.Equal(candidate.ManifestSha256, catalog.GetCluster("demo")!.DependencyManifestSha256);
        Assert.Equal(before.UpdatedAtUtc, catalog.GetCluster("demo")!.UpdatedAtUtc);
        Assert.Equal(before.GoalState, catalog.GetCluster("demo")!.GoalState);
        Assert.True(Data<ClusterDependencyStatus>(await ClusterApi.GetDependencies(
            "demo", context, catalog, fixture.Service, default)).Verified);
        await catalog.SelectDependenciesAsync("demo", request, default); // interrupted result persistence

        var alternative = request with { ManifestSha256 = new string('a', 64) };
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => catalog.SelectDependenciesAsync("demo", alternative, default));
        await catalog.SelectDependenciesAsync("demo", alternative with { ExpectedDependencySha256 = candidate.ManifestSha256 }, default);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => catalog.SelectDependenciesAsync("demo", request, default));
        await catalog.SelectDependenciesAsync("demo", request with { ExpectedDependencySha256 = alternative.ManifestSha256 }, default);

        File.AppendAllText(Path.Combine(fixture.Store, candidate.ManifestSha256, "manifest.json"), " ");
        Assert.False(Data<ClusterDependencyStatus>(await ClusterApi.GetDependencies(
            "demo", context, catalog, fixture.Service, default)).Verified);
        var replay = Data<ClusterOperation>(await ClusterApi.StageDependencies("demo", request,
            context, catalog, fixture.Service, new ClusterOperationStore(Path.Combine(fixture.Root, "operations")), default));
        Assert.Equal(op.OperationId, replay.OperationId);
        await catalog.SelectPackageAsync("demo", 1, installed, "next-package", default);
        Assert.Null(catalog.GetCluster("demo")!.DependencyManifestSha256);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => catalog.SelectDependenciesAsync("demo", request, default));

        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new Claim(QuasarClaimTypes.Cluster, "other")], "test"));
        Assert.Equal(403, ((IStatusCodeHttpResult)await ClusterApi.StageDependencies("demo", request,
            context, catalog, fixture.Service, operations, default)).StatusCode);
        Assert.Equal(403, ((IStatusCodeHttpResult)await ClusterApi.GetDependencyCandidate(
            "demo", context, catalog, fixture.Service, default)).StatusCode);
    }

    private static T Data<T>(IResult result) => Assert.IsType<AdminEnvelope<T>>(((IValueHttpResult)result).Value).Data;

    [LinuxFact]
    public async Task HostPreparationCopiesPinnedInputsAndCanVerifyOffline()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        string destination = Path.Combine(fixture.Root, "host-deployments");
        var prepared = await ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default);
        Assert.Equal(File.ReadAllBytes(Path.Combine(inputs.PackageDirectory, "plugins/ClusterNode/ClusterNode.dll")),
            File.ReadAllBytes(Path.Combine(prepared.Directory, "Package/plugins/ClusterNode/ClusterNode.dll")));
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(prepared.EnvironmentFile))!;
        Assert.Equal(Path.Combine(prepared.Directory, "Dependencies/payload/CommonPlugins"), environment["CLUSTER_COMMON_PLUGINS"]);
        Assert.Equal("", environment["DIRECT_TRANSPORT_SOURCE"]);
        Directory.Delete(inputs.PackageDirectory, true);
        Directory.Delete(inputs.DependencyDirectory, true);
        Assert.Equal(prepared, await ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        string native = Path.Combine(prepared.Directory, "Dependencies/payload/CommonPlugins/linux-compat/libHavok.so");
        File.SetUnixFileMode(native, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.AppendAllText(native, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        Assert.Empty(Directory.GetDirectories(destination, ".stage-*"));
    }

    [LinuxFact]
    public async Task MissingNativeAssetsAndUnsafeAssetPathsCannotBePinned()
    {
        using var fixture = await Fixture.CreateAsync();
        string linux = Path.Combine(fixture.Sources["CommonPlugins"], "linux-compat");
        string manifest = Path.Combine(linux, "linux-compat.xml");
        string original = File.ReadAllText(manifest);
        foreach (string asset in new[] { "<Asset Name=\"bad\" Path=\"../outside\"/>",
                     "<Asset Name=\"bad\" Url=\"https://example.test/latest\"/>",
                     "<DependencyIds><Id>not-in-the-snapshot</Id></DependencyIds>" })
        {
            File.WriteAllText(manifest, original.Replace("</PluginData>", asset + "</PluginData>"));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        }
        File.WriteAllText(manifest, original);
        File.Delete(Path.Combine(linux, "libHavok.so"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
    }

    [LinuxFact]
    public async Task HostRejectsChangedInputsLinksBadApprovalAndUnsupportedPackages()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        string destination = Path.Combine(fixture.Root, "host");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, new string('0', 64), destination, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, cancelled.Token));
        string native = Path.Combine(inputs.DependencyDirectory, "payload/CommonPlugins/linux-compat/libHavok.so");
        File.Delete(native);
        File.CreateSymbolicLink(native, Path.Combine(fixture.Sources["CommonPlugins"], "linux-compat/libHavok.so"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        File.Delete(native);
        File.WriteAllText(native, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        Assert.False(Directory.Exists(destination));
        using var package = new ClusterPackageTests.Fixture();
        var old = await package.Service.StageAsync(package.Request, default);
        File.WriteAllText(Path.Combine(old.PackagePath, "cli/deployment-capabilities.json"),
            "{\"schemaVersion\":1,\"frozenPluginBundles\":false}");
        Assert.Throws<InvalidDataException>(() => ClusterDeploymentFiles.ValidateCapabilities(old.PackagePath));
    }

    [LinuxFact]
    public async Task ReleaseSnapshotsShareContentAndSparseHostTransfersVerifyReusedBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var first = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(first, ClusterDeploymentFiles.JsonOptions);
        string originalHash = ClusterDeploymentFiles.Hash(bytes), host = Path.Combine(fixture.Root, "host");
        using var full = new MemoryStream();
        await ClusterDeploymentFiles.ExportInputsAsync(bytes, originalHash, full, default);
        full.Position = 0;
        var original = await ClusterDeploymentFiles.ImportAsync(full, originalHash, host, default);
        var second = first with { PackageSelectionRevision = 2 };
        bytes = JsonSerializer.SerializeToUtf8Bytes(second, ClusterDeploymentFiles.JsonOptions);
        string nextHash = ClusterDeploymentFiles.Hash(bytes);
        using var sparse = new MemoryStream();
        await ClusterDeploymentFiles.ExportInputsAsync(bytes, nextHash, sparse, default, first.Files);
        Assert.True(sparse.Length < full.Length);
        sparse.Position = 0;
        var next = await ClusterDeploymentFiles.ImportAsync(sparse, nextHash, host, default, originalHash);
        string relative = "Dependencies/payload/DedicatedServer/Content/world.sbc";
        Assert.True(ClusterContentStore.ReferenceCount(Path.Combine(next.Directory, relative)) > 2);
        Assert.Equal(1, await ClusterDeploymentFiles.CollectInstallationsAsync(host, fixture.Cluster.UniqueName,
            new HashSet<string> { nextHash }, [], default));
        Assert.False(Directory.Exists(original.Directory));
        Assert.Equal(next, await ClusterDeploymentFiles.PrepareAsync(bytes, nextHash, host, default));
        sparse.Position = 0;
        await Assert.ThrowsAnyAsync<IOException>(() => ClusterDeploymentFiles.ImportAsync(sparse, nextHash, host, default, originalHash));
        string corrupted = Path.Combine(next.Directory, relative);
        File.SetUnixFileMode(corrupted, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(corrupted, "corrupt");
        var third = second with { PackageSelectionRevision = 3 };
        bytes = JsonSerializer.SerializeToUtf8Bytes(third, ClusterDeploymentFiles.JsonOptions);
        using var badBase = new MemoryStream();
        await ClusterDeploymentFiles.ExportInputsAsync(bytes, ClusterDeploymentFiles.Hash(bytes), badBase, default, second.Files);
        badBase.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.ImportAsync(badBase,
            ClusterDeploymentFiles.Hash(bytes), host, default, nextHash));
    }

    [LinuxFact]
    public async Task UncommittedSnapshotIsPrunedWithoutRemovingRetainedContent()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(first.ManifestSha256, 1, null), default);
        string changed = Path.Combine(fixture.Sources["DedicatedServer/Content"], "world.sbc");
        File.WriteAllText(changed, "new world content");
        var second = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(second.ManifestSha256, 1, null), default);
        using (await ClusterStorageActivity.CollectAsync(default))
            await fixture.Service.CollectAsync(new HashSet<string> { first.ManifestSha256 }, default);
        Assert.False(Directory.Exists(Path.Combine(fixture.Store, second.ManifestSha256)));
        Assert.Equal(first, await fixture.Service.VerifyAsync(fixture.Cluster, first.ManifestSha256, default));
        string changedHash = ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes("new world content"));
        Assert.False(File.Exists(Path.Combine(fixture.Store, ".objects", changedHash + "-r")));
    }

    [Fact]
    public async Task ReleaseStagingCannotAcquireConfigPermissionFromAConcurrentContentQueue()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-staging-permission-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "demo"));
            var cluster = new ClusterDefinition { UniqueName = "demo", GatewayUrl = "http://gateway.test",
                ActiveDeployment = new("active", [], DateTimeOffset.UtcNow),
                QueuedContentUpdates = new("active", DateTimeOffset.UtcNow, [new("mod", "123", "mod", "42", DateTimeOffset.UtcNow)]) };
            await File.WriteAllTextAsync(Path.Combine(root, "demo/cluster.json"), JsonSerializer.Serialize(cluster, ClusterDeploymentFiles.JsonOptions));
            using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = root }).Build());
            var service = new ClusterUpdatePreparationService(catalog, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<ClusterUpdatePreparationService>.Instance);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync("demo", "release-manager", default));
            Assert.Contains("Configuration editing permission", error.Message);
            // The API's release update refuses before it contacts GitHub or changes the package selection.
            error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageLatestReleaseAsync("demo", "release-manager", default));
            Assert.Contains("Configuration editing permission", error.Message);
            Assert.NotNull(catalog.GetCluster("demo")!.QueuedContentUpdates);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class LinuxFactAttribute : FactAttribute
    { public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Cluster dependencies require Linux."; } }

    private sealed class Fixture : IDisposable
    {
        public ClusterPackageTests.Fixture Packages { get; } = new(ClusterPackageTests.CreateArchive());
        public string Root => Packages.Root;
        public string Store => Path.Combine(Root, "dependencies");
        public Dictionary<string, string> Sources { get; } = new();
        public ClusterDependencyService Service { get; private set; } = null!;
        public ClusterDefinition Cluster { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var package = await fixture.Packages.Service.StageAsync(fixture.Packages.Request, default);
            fixture.Packages.Offline = true;
            fixture.Cluster = new() { UniqueName = "demo", GatewayUrl = "http://gateway.test",
                PackageSelection = new(1, package.Release.Version, package.Release.Sha256, package.Commit, "package") };
            foreach (string prefix in new[] { "DedicatedServer/DedicatedServer64", "DedicatedServer/Content", "Magnetar", "DirectTransport", "CommonPlugins" })
            {
                string path = Path.Combine(fixture.Root, "inputs", prefix);
                fixture.Sources.Add(prefix, path);
                Directory.CreateDirectory(path);
            }
            foreach (string file in new[] { "DedicatedServer/DedicatedServer64/SpaceEngineersDedicated.exe",
                "DedicatedServer/DedicatedServer64/SpaceEngineers.Game.dll", "DedicatedServer/DedicatedServer64/Sandbox.Game.dll",
                "DedicatedServer/DedicatedServer64/VRage.dll", "DedicatedServer/Content/world.sbc", "Magnetar/MagnetarInterim.bin",
                "Magnetar/Libraries/MagnetarInterim/PluginSdk.dll", "Magnetar/Libraries/MagnetarInterim/libsteam_api.so",
                "DirectTransport/DirectTransport.dll", "DirectTransport/LiteNetLib.dll" })
            {
                string path = Path.Combine(fixture.Root, "inputs", file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(fixture.Sources["Magnetar"], "MagnetarInterim.bin"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            foreach (string name in new[] { "DirectTransport.xml", "DirectTransport.dll.xml", "DirectTransport.xml.original" })
                File.WriteAllText(Path.Combine(fixture.Sources["DirectTransport"], name),
                    "<PluginData><Id>direct-transport</Id><Commit>" + new string('c', 40)
                    + "</Commit><Runtimes>CoreCLR</Runtimes><Platforms>Linux</Platforms></PluginData>");
            fixture.Service = new(() => new(fixture.Sources), fixture.Store, fixture.Packages.Service);
            foreach (string id in new[] { "dotnet-compat", "linux-compat" })
            {
                string folder = Path.Combine(fixture.Sources["CommonPlugins"], id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, id + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(folder, id + ".xml"),
                    "<PluginData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"GitHubPlugin\">"
                    + "<Id>" + id + "</Id><Commit>" + new string('d', 40)
                    + "</Commit><Runtimes>CoreCLR</Runtimes><Platforms>Linux</Platforms><Asset Name=\"bundle\" Path=\".\"/></PluginData>");
                if (id == "linux-compat")
                    foreach (string name in new[] { "libHavok.so", "libRecastDetour.so", "libVRageNative.so", "libEOSSDK-Linux-Shipping.so" })
                        File.WriteAllText(Path.Combine(folder, name), "native");
            }
            return fixture;
        }
        public void Dispose() => Packages.Dispose();
    }
}
