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
