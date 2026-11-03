using System.Reflection;
using System.Text.Json;
using CometWorks.ClusterGateway.AdminContract.V1;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

// SE-968: a purged cluster ID is set up again from scratch, not answered from the deleted cluster's records.
[Collection("Cluster storage")]
public sealed class ClusterPurgeTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-purge-test-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previous;

    public ClusterPurgeTests()
    {
        Directory.CreateDirectory(root);
        previous = cache.GetValue(null);
        cache.SetValue(null, root);
        Environment.SetEnvironmentVariable("SE968_HOST_TOKEN", "token");
    }

    public void Dispose()
    {
        cache.SetValue(null, previous);
        Directory.Delete(root, true);
    }

    private sealed class RecordingHost : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Requests = [];
        public bool Refuse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = await request.Content!.ReadAsStringAsync(token);
            lock (Requests) Requests.Add((request.RequestUri!.AbsolutePath, body));
            bool release = request.RequestUri.AbsolutePath.Contains("/deleted-clusters/");
            var response = Refuse && release
                ? new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
                    { Content = new StringContent($"{{\"protocolVersion\":{HostProtocol.Version},\"error\":{{\"code\":\"busy\",\"message\":\"still supervised\"}}}}") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent($"{{\"protocolVersion\":{HostProtocol.Version},\"data\":{(release ? "[\"/state/conversions/reuserun\"]" : "0")}}}") };
            response.Headers.Add(HostProtocol.HeaderName, HostProtocol.Version.ToString());
            return response;
        }
    }

    private static ClusterDefinition Definition(string name) => new()
    {
        UniqueName = name, GatewayUrl = "http://127.0.0.1:1", HostCommandUrl = "http://host.test:18432",
        HostCommandTokenEnvironmentVariable = "SE968_HOST_TOKEN", ConfigProfileId = "cluster-" + name, WorldTemplateId = name,
        Preparation = new(new string('a', 64), JsonSerializer.Serialize(new { hosts = new[] { new { id = "h", runRoot = $"/state/conversions/{name}run/runtime" } } }), "",
            [new("h", "http://host.test:18432", "SE968_HOST_TOKEN", "EXEC", "/i", "/w", $"/state/conversions/{name}cfg/configuration")]),
    };

    private ClusterCatalog Catalog() => new(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "Clusters") }).Build());

    private static Task<ClusterOperation> Setup(ClusterOperationStore operations, string name, Action run) =>
        operations.ExecuteAsync(name, "cluster.setup", "setup-" + name, "test", new { name }, _ =>
        {
            run();
            return Task.FromResult(new AdminEnvelope<string>(AdminProtocol.Version, DateTimeOffset.UtcNow, "set up"));
        }, CancellationToken.None);

    [Fact]
    public async Task PurgedIdIsSetUpAgainFromScratch()
    {
        if (!OperatingSystem.IsLinux()) return;
        string backups = Path.Combine(root, "Backups");
        foreach (string name in new[] { "reuse", "kept" })
        {
            Directory.CreateDirectory(Path.Combine(root, "Clusters", name));
            await File.WriteAllTextAsync(Path.Combine(root, "Clusters", name, "cluster.json"), JsonSerializer.Serialize(Definition(name), Json));
            string template = Directory.CreateDirectory(Path.Combine(root, "WorldTemplates", name, "World")).Parent!.FullName;
            await File.WriteAllTextAsync(Path.Combine(template, "template.json"), JsonSerializer.Serialize(new QuasarWorldTemplate { WorldTemplateId = name, Name = name }, Json));
        }
        string backup = Directory.CreateDirectory(Path.Combine(ClusterBackupService.Root(backups, "reuse"), "manual")).FullName;
        string setup = Directory.CreateDirectory(Path.Combine(root, "ClusterSetup", "reuse")).FullName;
        await File.WriteAllTextAsync(Path.Combine(setup, "request.json"), JsonSerializer.Serialize(new ClusterSetupRequest("reuse", "Reuse",
            "reuse", "reuse", "local", 27016, [new("local", 1)]), Json));

        using var catalog = Catalog();
        var operations = new ClusterOperationStore(Path.Combine(root, "Operations", "Clusters"));
        int setups = 0;
        await Setup(operations, "reuse", () => setups++);
        await Setup(operations, "kept", () => { });
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        foreach (string id in new[] { "cluster-reuse", "reuse", "cluster-kept", "shared" })
            await profiles.UpsertAsync(new QuasarConfigProfile { ConfigProfileId = id, Name = id });
        using var templates = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        using var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        var players = new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance);
        var host = new RecordingHost();
        var purge = new ClusterPurgeService(catalog, operations, new ClusterHostClient(new HttpClient(host)),
            new WebServiceOptions { BackupDirectory = backups }, profiles, templates, servers, players, NullLogger<ClusterPurgeService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => purge.PurgeAsync("reuse", default)); // Not deleted yet.
        await catalog.DeleteDefinitionAsync(catalog.GetCluster("reuse")!, default);
        Assert.Throws<InvalidOperationException>(() => catalog.CreateAsync(new("reuse", "Reuse", "http://127.0.0.1:1", "ADMIN"), default).GetAwaiter().GetResult());
        await Setup(operations, "reuse", () => setups++);
        Assert.Equal(1, setups); // Replayed, not run.

        // A Host that refuses leaves everything for the next attempt.
        host.Refuse = true;
        await Assert.ThrowsAsync<ClusterHostException>(() => purge.PurgeAsync("reuse", default));
        Assert.True(Directory.Exists(backup) && Directory.Exists(setup) && catalog.GetDeletedClusters().Count == 1);
        host.Refuse = false;
        host.Requests.Clear();

        var result = await purge.PurgeAsync("REUSE", default);

        Assert.Equal("reuse", result.ClusterId);
        Assert.Equal(["/state/conversions/reuserun"], result.HostWorkspacesRemoved);
        Assert.Equal(["/host/v1/deployment-references/reuse", "/host/v1/deleted-clusters/reuse"], host.Requests.Select(r => r.Path));
        var released = JsonSerializer.Deserialize<HostDeletedClusterData>(host.Requests[1].Body, Json)!;
        Assert.Equal(["/state/conversions/reusecfg", "/state/conversions/reuserun"], released.Workspaces.Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(ClusterBackupService.Root(backups, "reuse")));
        Assert.False(Directory.Exists(setup));
        Assert.Empty(catalog.GetDeletedClusters());
        Assert.Single(Directory.GetFiles(Path.Combine(result.Archive, "Clusters", "reuse", "History"), "*-deleted.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(result.Archive, "Operations")));
        Assert.True(File.Exists(Path.Combine(result.Archive, "WorldTemplates", "reuse", "template.json")));
        Assert.True(File.Exists(Path.Combine(result.Archive, "ConfigProfiles", "cluster-reuse", "profile.json")));
        Assert.True(File.Exists(Path.Combine(result.Archive, "ConfigProfiles", "reuse", "profile.json")));
        Assert.Null(profiles.GetProfile("cluster-reuse"));
        Assert.Null(templates.GetTemplate("reuse"));
        // The other cluster and what it uses stay.
        Assert.NotNull(catalog.GetCluster("kept"));
        Assert.NotNull(profiles.GetProfile("cluster-kept"));
        Assert.NotNull(profiles.GetProfile("shared"));
        Assert.NotNull(templates.GetTemplate("kept"));
        Assert.NotNull(operations.Find("kept", "cluster.setup", "setup-kept"));

        // The ID is free again, and the same setup key runs the setup again.
        Assert.NotNull(await catalog.CreateAsync(new("reuse", "Reuse", "http://127.0.0.1:1", "ADMIN"), default));
        Assert.Equal(ClusterOperationState.Succeeded, (await Setup(operations, "reuse", () => setups++)).State);
        Assert.Equal(2, setups);
        // A store loaded from disk doesn't bring the old record back either.
        Assert.Single(new ClusterOperationStore(Path.Combine(root, "Operations", "Clusters")).GetRecent(["reuse"], 10));
    }

    [Fact]
    public async Task PurgeRefusesALiveOrUnknownCluster()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.Combine(root, "Clusters", "live"));
        await File.WriteAllTextAsync(Path.Combine(root, "Clusters", "live", "cluster.json"), JsonSerializer.Serialize(Definition("live"), Json));
        using var catalog = Catalog();
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var templates = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        using var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        var players = new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance);
        var host = new RecordingHost();
        var purge = new ClusterPurgeService(catalog, new ClusterOperationStore(Path.Combine(root, "Operations", "Clusters")),
            new ClusterHostClient(new HttpClient(host)), new WebServiceOptions { BackupDirectory = Path.Combine(root, "Backups") },
            profiles, templates, servers, players, NullLogger<ClusterPurgeService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => purge.PurgeAsync("LIVE", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => purge.PurgeAsync("never", default));
        await Assert.ThrowsAsync<ArgumentException>(() => purge.PurgeAsync("../live", default));
        Assert.Empty(host.Requests);
        Assert.NotNull(catalog.GetCluster("live"));
    }
}
