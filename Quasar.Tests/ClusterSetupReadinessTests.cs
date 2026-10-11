using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using HostContract = Quasar.Host.Contract.V1;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ClusterSetupReadinessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-readiness-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;

    public ClusterSetupReadinessTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
    }

    [Theory]
    [InlineData("offline", false)]
    [InlineData("identity", false)]
    [InlineData("executor", false)]
    [InlineData("capability", false)]
    [InlineData("steam", false)]
    [InlineData("offline", true)]
    [InlineData("capability", true)]
    [InlineData("configuration", true)]
    [InlineData("healthy", true)]
    public async Task SetupRequiresEveryHostToBeReadyIncludingPreviouslyCompletedAttempts(string fault, bool completed)
    {
        var credentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(_root, "credentials.json"));
        var hosts = new ClusterHostCatalog(credentials, Path.Combine(_root, "hosts"));
        await hosts.RegisterAsync("one", "One", "10.1.0.1", 18400, default);
        await hosts.RegisterAsync("two", "Two", "10.1.0.2", 18400, default);
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        using var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var worlds = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Profile" });
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var world = await worlds.ImportAsync("World", "", source);
        using var http = new HttpClient(new HostResponses(fault));
        var setup = new ClusterSetupService(clusters, hosts, new ClusterHostClient(http, credentials), credentials,
            null!, null!, null!, null!, worlds, profiles, null!, new ClusterOperationStore(Path.Combine(_root, "operations")), servers);
        var request = new ClusterSetupRequest("demo", "Demo", world.WorldTemplateId, "profile", "one", 28000,
            [new("one", 1), new("two", 1)]);
        if (completed)
        {
            var cluster = await clusters.CreateAsync(new("demo", "Demo", "http://10.1.0.1:28016", "ADMIN"), default);
            var revision = new ClusterActiveRevision("revision", hosts.GetAll().Select(h => new ClusterHostRevision(
                h.Id, h.CommandUrl, h.CredentialReference, new HostContract.HostActiveDeployment("demo", "revision", Hash,
                    new("demo", cluster.GatewayUrl, "EXECUTOR", "/bundle/manifest.json", Hash, "/runs/" + h.Id),
                    h.Id == "one" ? new("demo", HostContract.GatewayGoal.Off, "/bundle/manifest.json", Hash, "revision", [28000, 28016], "/runs/one") : null,
                    ["one", "two"]))).ToArray(), DateTimeOffset.UtcNow);
            await clusters.RecordActiveDeploymentAsync(cluster, revision, default);
            string work = Path.Combine(_root, "ClusterSetup", "demo");
            Directory.CreateDirectory(work);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllText(Path.Combine(work, "request.json"), JsonSerializer.Serialize(request, json));
            File.WriteAllText(Path.Combine(work, "status.json"), JsonSerializer.Serialize(new ClusterSetupStatus(request,
                "Ready to start", null, DateTimeOffset.UtcNow), json));
        }

        var result = await setup.RunAsync(request, "first", "test", default);

        Assert.Equal(fault == "healthy" ? ClusterOperationState.Succeeded : ClusterOperationState.Failed, result.State);
        if (!completed) Assert.Empty(clusters.GetClusters());
        else Assert.NotNull(clusters.GetCluster("demo")!.ActiveDeployment);
        Assert.Equal(fault == "healthy" ? "Ready to start" : "Setup interrupted", setup.GetStatus("demo")!.Phase);
        if (fault == "healthy") Assert.Null(result.Error);
        else { Assert.NotNull(result.Error); Assert.NotNull(setup.GetStatus("demo")!.Error); }
        if (fault == "offline")
        {
            Assert.Contains("Host executor 'two'", result.Error!.Message);
            Assert.DoesNotContain(".quasar-host.invalid", result.Error.Message);
        }
        Assert.Equal(request.UniqueName, setup.GetStatus("demo")!.Request.UniqueName);
    }

    private const string Character = """<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Character"><EntityId>1</EntityId></MyObjectBuilder_EntityBase>""";
    private const string Voxel = """<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_VoxelMap"><EntityId>2</EntityId></MyObjectBuilder_EntityBase>""";
    private static string Grid(bool isStatic) => $"""
        <MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid"><EntityId>3</EntityId>
          <CubeBlocks><MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_Door"><IsStatic>true</IsStatic></MyObjectBuilder_CubeBlock></CubeBlocks>
          <IsStatic>{(isStatic ? "true" : "false")}</IsStatic>
        </MyObjectBuilder_EntityBase>
        """;
    private static string Sector(params string[] objects) =>
        """<?xml version="1.0"?><MyObjectBuilder_Sector xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><SectorObjects>"""
        + string.Concat(objects) + "</SectorObjects></MyObjectBuilder_Sector>";

    [Theory]
    [InlineData("character", true)]
    [InlineData("dynamic", true)]
    [InlineData("static", false)]
    [InlineData("static-gzip", false)]
    [InlineData("character-gzip", true)]
    [InlineData("voxels", false)]
    [InlineData("empty", false)]
    [InlineData("binary", false)]
    public void StaticGridCheckFollowsTheWorldConverter(string world, bool lacking)
    {
        string directory = Path.Combine(_root, world);
        Directory.CreateDirectory(directory);
        string sector = world.Split('-')[0] switch
        {
            "character" => Sector(Voxel, Character),
            "dynamic" => Sector(Grid(false)),
            "static" => Sector(Character, Grid(false), Grid(true)),
            "voxels" => Sector(Voxel),
            "empty" => """<MyObjectBuilder_Sector><SectorObjects /></MyObjectBuilder_Sector>""",
            _ => "",
        };
        string path = Path.Combine(directory, world == "binary" ? "SANDBOX_0_0_0_.sbsB5" : "SANDBOX_0_0_0_.sbs");
        if (world.EndsWith("-gzip"))
        {
            using var gzip = new System.IO.Compression.GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Fastest);
            gzip.Write(Encoding.UTF8.GetBytes(sector));
        }
        else File.WriteAllText(path, sector);

        Assert.Equal(lacking, ClusterSetupService.LacksStaticGrid(directory));
    }

    [Fact]
    public async Task SetupRefusesAWorldWithoutStaticGridBeforeTheIdIsBound()
    {
        var credentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(_root, "credentials.json"));
        var hosts = new ClusterHostCatalog(credentials, Path.Combine(_root, "hosts"));
        await hosts.RegisterAsync("one", "One", "10.1.0.1", 18400, default);
        await hosts.RegisterAsync("two", "Two", "10.1.0.2", 18400, default);
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        using var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var worlds = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Profile" });
        async Task<QuasarWorldTemplate> Import(string name, string sector)
        {
            string source = Path.Combine(_root, "source-" + name);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
            File.WriteAllText(Path.Combine(source, "SANDBOX_0_0_0_.sbs"), sector);
            return await worlds.ImportAsync(name, "", source);
        }
        var empty = await Import("Empty World", Sector(Character));
        var station = await Import("Station", Sector(Character, Grid(true)));
        using var http = new HttpClient(new HostResponses("offline"));
        var setup = new ClusterSetupService(clusters, hosts, new ClusterHostClient(http, credentials), credentials,
            null!, null!, null!, null!, worlds, profiles, null!, new ClusterOperationStore(Path.Combine(_root, "operations")), servers);
        var request = new ClusterSetupRequest("demo", "Demo", empty.WorldTemplateId, "profile", "one", 28000,
            [new("one", 1), new("two", 1)]);

        var refused = await setup.RunAsync(request, "first", "test", default);

        Assert.Equal(ClusterOperationState.Failed, refused.State);
        Assert.Contains("\"Empty World\" has no static grid", refused.Error!.Message);
        Assert.Null(setup.GetStatus("demo"));
        Assert.False(File.Exists(Path.Combine(_root, "ClusterSetup", "demo", "request.json")));
        Assert.Empty(clusters.GetClusters());

        // The same ID still takes another world; this attempt binds it and stops at the offline Host.
        var retried = await setup.RunAsync(request with { WorldTemplateId = station.WorldTemplateId }, "second", "test", default);

        Assert.Equal(ClusterOperationState.Failed, retried.State);
        Assert.Contains("Host executor 'two'", retried.Error!.Message);
        Assert.Equal(station.WorldTemplateId, setup.GetStatus("demo")!.Request.WorldTemplateId);
    }

    private static readonly string Hash = new('a', 64);
    private sealed class HostResponses(string fault) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string id = request.RequestUri!.Host.Split('.')[0];
            bool broken = id == (fault == "steam" ? "one" : "two");
            if (broken && fault == "offline") throw new HttpRequestException("offline");
            var status = new HostContract.HostStatus(broken && fault == "executor" ? "" : "exec-" + id,
                broken && fault == "identity" ? "wrong" : id,
                [new("demo", "http://10.1.0.1:28016", !(broken && fault == "configuration"), Hash, "/runs/" + id)], [],
                GatewayStopFencing: !(broken && fault == "capability"), SteamClientLibrary: !(broken && fault == "steam"));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new HostContract.HostEnvelope<HostContract.HostStatus>(
                    HostContract.HostProtocol.Version, DateTimeOffset.UtcNow, status), new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
            };
            response.Headers.Add(HostContract.HostProtocol.HeaderName, HostContract.HostProtocol.Version.ToString());
            return Task.FromResult(response);
        }
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
