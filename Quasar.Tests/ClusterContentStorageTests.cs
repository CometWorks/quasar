using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.ClusterDeployment;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster storage")]
public sealed class ClusterContentStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-content-test-" + Guid.NewGuid().ToString("N"));
    public ClusterContentStorageTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    // SE-792: a prepared deployment lists its mods once per seed folder; they are links of one object.
    [Fact]
    public async Task AdoptingAReferenceOfItsOwnObjectKeepsTheFileAndHashesOnce()
    {
        if (!OperatingSystem.IsLinux()) return;
        string source = Path.Combine(root, "source");
        await File.WriteAllTextAsync(source, "mod payload");
        var pin = new DeploymentFile(ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(source)), 11, false);
        string snapshots = Path.Combine(root, "snapshots");
        string first = Path.Combine(snapshots, "seed-1/mod"), second = Path.Combine(snapshots, "seed-2/mod");
        using var content = await ClusterContentStore.OpenAsync(snapshots, default);
        await content.ReferenceAsync(source, first, pin, default);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        Assert.Equal(0, ClusterContentStore.Link(first, second));
        string identity = ClusterContentStore.FileIdentity(first);
        Assert.Equal(identity, ClusterContentStore.FileIdentity(second));

        await content.AdoptAsync(second, pin, default);
        Assert.Equal(identity, ClusterContentStore.FileIdentity(second));
        Assert.Equal(3u, ClusterContentStore.ReferenceCount(second));

        // A separate file is still verified and replaced by a reference.
        string copy = Path.Combine(snapshots, "seed-3/mod");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllTextAsync(copy, "mod payload");
        await content.AdoptAsync(copy, pin, default);
        Assert.Equal(identity, ClusterContentStore.FileIdentity(copy));
        await File.WriteAllTextAsync(Path.Combine(snapshots, "wrong"), "not the mod");
        await Assert.ThrowsAsync<InvalidDataException>(() => content.AdoptAsync(Path.Combine(snapshots, "wrong"), pin, default));
    }

    [Fact]
    public async Task InspectHashesHardLinksOnceAndStillPinsEveryPath()
    {
        if (!OperatingSystem.IsLinux()) return;
        string tree = Path.Combine(root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a"));
        Directory.CreateDirectory(Path.Combine(tree, "b"));
        await File.WriteAllTextAsync(Path.Combine(tree, "a/mod"), "payload");
        Assert.Equal(0, ClusterContentStore.Link(Path.Combine(tree, "a/mod"), Path.Combine(tree, "b/mod")));
        await File.WriteAllTextAsync(Path.Combine(tree, "b/other"), "other");
        var pins = await ClusterDeploymentFiles.InspectAsync(tree, default);
        Assert.Equal(3, pins.Count);
        Assert.Equal(pins["a/mod"], pins["b/mod"]);
        Assert.Equal(ClusterDeploymentFiles.Hash("payload"u8.ToArray()), pins["b/mod"].Sha256);
        Assert.Equal(ClusterDeploymentFiles.Hash("other"u8.ToArray()), pins["b/other"].Sha256);
    }

    [Fact]
    public async Task SnapshotsReferenceOneObjectAndLastReleasePrunesIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        string source = Path.Combine(root, "source");
        await File.WriteAllTextAsync(source, "version one");
        var pin = new DeploymentFile(ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(source)), 11, false);
        string snapshots = Path.Combine(root, "snapshots");
        string first = Path.Combine(snapshots, "one/file"), second = Path.Combine(snapshots, "two/file");
        using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
        {
            await content.ReferenceAsync(source, first, pin, default);
            await content.ReferenceAsync(source, second, pin, default);
            Assert.Equal(3u, ClusterContentStore.ReferenceCount(first));
            Assert.Equal(0, (int)(File.GetUnixFileMode(first) & UnixFileMode.UserWrite));
            string mutable = Path.Combine(root, "mutable-world");
            await ClusterWorldFiles.CopyAsync(Path.GetDirectoryName(first)!, mutable, default);
            await File.WriteAllTextAsync(Path.Combine(mutable, "file"), "runtime save");
            Assert.Equal("version one", await File.ReadAllTextAsync(first));
            await File.WriteAllTextAsync(source, "new source version");
            Assert.Equal("version one", await File.ReadAllTextAsync(first));
            Directory.Delete(Path.GetDirectoryName(first)!, true);
            content.Prune();
            Assert.Equal("version one", await File.ReadAllTextAsync(second));
            Assert.Equal(2u, ClusterContentStore.ReferenceCount(second));
            Directory.Delete(Path.GetDirectoryName(second)!, true);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(snapshots, ".objects")).Where(p => !Path.GetFileName(p).StartsWith('.')));
    }

    [Fact]
    public async Task CorruptObjectCannotBeReusedAndLinksAreRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        string source = Path.Combine(root, "source");
        await File.WriteAllTextAsync(source, "good");
        var pin = new DeploymentFile(ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(source)), 4, false);
        string snapshots = Path.Combine(root, "snapshots"), first = Path.Combine(snapshots, "one/file");
        using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
            await content.ReferenceAsync(source, first, pin, default);
        File.SetUnixFileMode(first, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllTextAsync(first, "evil");
        using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
            await Assert.ThrowsAsync<InvalidDataException>(() => content.ReferenceAsync(source, Path.Combine(snapshots, "two/file"), pin, default));
        string unsafeRoot = Path.Combine(root, "unsafe");
        Directory.CreateDirectory(unsafeRoot);
        Directory.CreateSymbolicLink(Path.Combine(unsafeRoot, ".objects"), Path.Combine(snapshots, ".objects"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterContentStore.OpenAsync(unsafeRoot, default));
    }

    [Fact]
    public async Task ModDeltaTransfersOnlyChangesAndOwnersPreventPrematurePruning()
    {
        if (!OperatingSystem.IsLinux()) return;
        string source = Path.Combine(root, "source"), store = Path.Combine(root, "host-mods");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), new string('s', 100_000));
        await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(source, "removed.txt"), "remove");
        string archive = Path.Combine(root, "mods.tar");
        string original = await ClusterWorldFiles.PackAsync(source, archive, default, false);
        using (var input = File.OpenRead(archive)) await ClusterWorldFiles.UnpackAsync(input, original, store, default, false);
        var previous = await ClusterWorldFiles.ReadPinsAsync(store, original, default);
        await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "new");
        File.Delete(Path.Combine(source, "removed.txt"));
        string next = await ClusterWorldFiles.PackAsync(source, archive, default, false, previous);
        Assert.True(new FileInfo(archive).Length < 20_000);
        string installed;
        using (var input = File.OpenRead(archive)) installed = await ClusterWorldFiles.UnpackAsync(input, next, store, default, false, original);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(installed, "changed.txt")));
        Assert.False(File.Exists(Path.Combine(installed, "removed.txt")));
        Assert.Equal(3u, ClusterContentStore.ReferenceCount(Path.Combine(installed, "same.txt")));
        ClusterInputOwners.Track(store, original, "one");
        ClusterInputOwners.Track(store, next, "one");
        ClusterInputOwners.Track(store, next, "two");
        await ClusterInputOwners.RetainAsync(store, "one", new HashSet<string>(), default);
        Assert.False(Directory.Exists(Path.Combine(store, original)));
        Assert.True(Directory.Exists(installed));
        await ClusterInputOwners.RetainAsync(store, "two", new HashSet<string>(), default);
        Assert.False(Directory.Exists(installed));
        Assert.Empty(Directory.GetFiles(Path.Combine(store, ".objects")).Where(p => !Path.GetFileName(p).StartsWith('.')));
        using var missingBase = File.OpenRead(archive);
        await Assert.ThrowsAnyAsync<IOException>(() => ClusterWorldFiles.UnpackAsync(missingBase, next, store, default, false, original));
    }

    [Fact]
    public async Task CollectionWaitsForNestedWorkflowLeases()
    {
        var first = await ClusterStorageActivity.BeginAsync(default);
        var nested = await ClusterStorageActivity.BeginAsync(default);
        var waiting = ClusterStorageActivity.CollectAsync(default);
        Assert.False(waiting.IsCompleted);
        nested.Dispose();
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var collection = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(ClusterStorageActivity.TryBegin());
    }

    // #1423: every reconciler lease ends in Idle. A collection started then takes the Writer when the next
    // lease is released, so TryBegin of the cluster after it failed on every 2 s pass: the third cluster
    // of a fresh setup ignored its goal On and kept a stale lifecycle for hours.
    [Fact]
    public async Task IdleCollectionDoesNotStarveTheReconcilersLaterClusters()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        string[] names = ["a", "b", "c"];
        foreach (string name in names)
            await catalog.CreateAsync(new(name, name, "http://127.0.0.1:1", "TOKEN"), default);
        using var maintenance = new ClusterStorageMaintenance(catalog,
            new ClusterDependencyService(() => [], Path.Combine(root, "dependencies"), null!),
            new ClusterHostClient(new HttpClient()), new WebServiceOptions { BackupDirectory = Path.Combine(root, "backups") },
            NullLogger<ClusterStorageMaintenance>.Instance);
        await maintenance.StartAsync(default);
        try
        {
            await Task.Delay(500); // The start-up collection.
            var reconciled = names.ToDictionary(name => name, _ => 0);
            for (int pass = 0; pass < 20; pass++)
            {
                foreach (string name in names)
                    await catalog.TryWithLifecycleAsync(name, async _ => { await Task.Delay(5); reconciled[name]++; }, default);
                await Task.Delay(50);
            }
            Assert.All(names, name => Assert.True(reconciled[name] >= 18, $"{name} reconciled {reconciled[name]} of 20 passes"));
        }
        finally { await maintenance.StopAsync(default); }
    }

    [Fact]
    public async Task IdleCollectsOnlyAfterStorageChanged()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        string dependencies = Path.Combine(root, "dependencies");
        using var maintenance = new ClusterStorageMaintenance(catalog, new ClusterDependencyService(() => [], dependencies, null!),
            new ClusterHostClient(new HttpClient()), new WebServiceOptions { BackupDirectory = Path.Combine(root, "backups") },
            NullLogger<ClusterStorageMaintenance>.Instance);
        await maintenance.StartAsync(default);
        try
        {
            await Task.Delay(500); // The start-up collection.
            // A collection that does work removes abandoned staging folders; one left in place shows that none did.
            string stage = Directory.CreateDirectory(Path.Combine(dependencies, ".stage-test")).FullName;
            (await ClusterStorageActivity.BeginAsync(default)).Dispose();
            await Task.Delay(300);
            Assert.True(Directory.Exists(stage));
            ClusterStorageActivity.MarkChanged();
            (await ClusterStorageActivity.BeginAsync(default)).Dispose();
            for (int i = 0; i < 50 && Directory.Exists(stage); i++) await Task.Delay(100);
            Assert.False(Directory.Exists(stage));
        }
        finally { await maintenance.StopAsync(default); }
    }

    private sealed class RecordingHost : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = await request.Content!.ReadAsStringAsync(token);
            lock (Requests) Requests.Add((request.RequestUri!.AbsolutePath, body));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent($"{{\"protocolVersion\":{HostProtocol.Version},\"data\":{(request.RequestUri.AbsolutePath.Contains("/deleted-clusters/") ? "[]" : "0")}}}") };
            response.Headers.Add(HostProtocol.HeaderName, HostProtocol.Version.ToString());
            return response;
        }
    }

    [Fact]
    public async Task DeletedClusterReleasesItsHostInputs()
    {
        if (!OperatingSystem.IsLinux()) return;
        Environment.SetEnvironmentVariable("SE775_HOST_TOKEN", "token");
        foreach (string name in new[] { "kept", "gone" })
        {
            Directory.CreateDirectory(Path.Combine(root, "clusters", name));
            await File.WriteAllTextAsync(Path.Combine(root, "clusters", name, "cluster.json"), JsonSerializer.Serialize(new ClusterDefinition
                { UniqueName = name, GatewayUrl = "http://127.0.0.1:1", HostCommandUrl = "http://host.test:18432",
                    HostCommandTokenEnvironmentVariable = "SE775_HOST_TOKEN" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        await catalog.DeleteDefinitionAsync(catalog.GetCluster("gone")!, default);
        Assert.Equal(["gone"], catalog.GetDeletedClusters().Select(c => c.Cluster.UniqueName));
        Assert.Equal([("http://host.test:18432", "SE775_HOST_TOKEN")], ClusterStorageMaintenance.DeletedEndpoints(catalog.GetDeletedClusters()[0].Cluster));

        var host = new RecordingHost();
        using var maintenance = new ClusterStorageMaintenance(catalog, new ClusterDependencyService(() => [], Path.Combine(root, "dependencies"), null!),
            new ClusterHostClient(new HttpClient(host)), new WebServiceOptions { BackupDirectory = Path.Combine(root, "backups") },
            NullLogger<ClusterStorageMaintenance>.Instance);
        await maintenance.StartAsync(default);
        try { for (int i = 0; i < 50 && host.Requests.Count == 0; i++) await Task.Delay(100); }
        finally { await maintenance.StopAsync(default); }
        var (path, body) = Assert.Single(host.Requests);
        Assert.EndsWith("/deployment-references/gone", path);
        var references = JsonSerializer.Deserialize<HostDeploymentReferences>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(references.Installations.Concat(references.Mods).Concat(references.Worlds).Concat(references.Configurations));
    }

    // SE-776: the retention of a deleted cluster ends with its backups and Host data, whatever it pinned goes with them.
    [Fact]
    public async Task DeletedClusterRetentionEndsWithItsData()
    {
        if (!OperatingSystem.IsLinux()) return;
        Environment.SetEnvironmentVariable("SE776_HOST_TOKEN", "token");
        string Run(string cluster) => $"/state/conversions/{cluster}run/runtime";
        ClusterDefinition Definition(string name) => new()
        {
            UniqueName = name, GatewayUrl = "http://127.0.0.1:1", HostCommandUrl = "http://host.test:18432", HostCommandTokenEnvironmentVariable = "SE776_HOST_TOKEN",
            Preparation = new(new string('a', 64), JsonSerializer.Serialize(new { hosts = new[] { new { id = "h", runRoot = Run(name) } } }), "",
                [new("h", "http://host.test:18432", "SE776_HOST_TOKEN", "EXEC", "/i", "/w", $"/state/conversions/{name}cfg/configuration")]),
        };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        string backups = Path.Combine(root, "backups");
        foreach (string name in new[] { "kept", "fresh", "expired" })
        {
            Directory.CreateDirectory(Path.Combine(root, "clusters", name));
            await File.WriteAllTextAsync(Path.Combine(root, "clusters", name, "cluster.json"), JsonSerializer.Serialize(Definition(name), json));
            string backup = Path.Combine(ClusterBackupService.Root(backups, name), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            await File.WriteAllBytesAsync(Path.Combine(backup, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(new ClusterBackup(Guid.NewGuid(), name, DateTimeOffset.UtcNow,
                CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.Quiescent, Definition(name), [], false), json));
        }
        string setup = Path.Combine(Magnetar.Protocol.Runtime.MagnetarPaths.GetQuasarDirectory(), "ClusterSetup", "expired");
        Directory.CreateDirectory(setup);
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        await catalog.DeleteDefinitionAsync(catalog.GetCluster("fresh")!, default);
        await catalog.DeleteDefinitionAsync(catalog.GetCluster("expired")!, default);
        string history = Path.Combine(root, "clusters", "expired", "History");
        string deleted = Directory.GetFiles(history).Single();
        File.Move(deleted, Path.Combine(history, DateTimeOffset.UtcNow.AddDays(-8).ToString("yyyyMMddHHmmssfff") + Path.GetFileName(deleted)[17..]));

        var host = new RecordingHost();
        using var maintenance = new ClusterStorageMaintenance(catalog, new ClusterDependencyService(() => [], Path.Combine(root, "dependencies"), null!),
            new ClusterHostClient(new HttpClient(host)), new WebServiceOptions { BackupDirectory = backups }, NullLogger<ClusterStorageMaintenance>.Instance);
        await maintenance.StartAsync(default);
        try { for (int i = 0; i < 50 && !host.Requests.Any(r => r.Path.Contains("/deleted-clusters/")); i++) await Task.Delay(100); }
        finally { await maintenance.StopAsync(default); }

        Assert.False(Directory.Exists(ClusterBackupService.Root(backups, "expired")));
        Assert.False(Directory.Exists(setup));
        Assert.True(Directory.Exists(ClusterBackupService.Root(backups, "fresh")), "the backup of a recently deleted cluster stays");
        Assert.True(Directory.Exists(ClusterBackupService.Root(backups, "kept")));
        var requests = host.Requests.Where(r => r.Path.EndsWith("/expired")).ToArray();
        Assert.DoesNotContain(host.Requests, r => r.Path.EndsWith("/deleted-clusters/fresh"));
        Assert.Equal(["/host/v1/deployment-references/expired", "/host/v1/deleted-clusters/expired"], requests.Select(r => r.Path));
        var data = JsonSerializer.Deserialize<HostDeletedClusterData>(requests[1].Body, json)!;
        Assert.Equal(["/state/conversions/expiredcfg", "/state/conversions/expiredrun"], data.Workspaces.Order(StringComparer.Ordinal));
    }

    // Nothing but time ends the retention: no catalog or storage change starts this collection.
    [Fact]
    public async Task DeletedClusterRetentionEndsWithoutAnyOtherChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.Combine(root, "clusters", "aging"));
        await File.WriteAllTextAsync(Path.Combine(root, "clusters", "aging", "cluster.json"),
            JsonSerializer.Serialize(new ClusterDefinition { UniqueName = "aging", GatewayUrl = "http://127.0.0.1:1" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        await catalog.DeleteDefinitionAsync(catalog.GetCluster("aging")!, default);
        string backups = Path.Combine(root, "backups"), kept = ClusterBackupService.Root(backups, "aging");
        Directory.CreateDirectory(kept);
        using var maintenance = new ClusterStorageMaintenance(catalog, new ClusterDependencyService(() => [], Path.Combine(root, "dependencies"), null!),
            new ClusterHostClient(new HttpClient(new RecordingHost())), new WebServiceOptions { BackupDirectory = backups }, NullLogger<ClusterStorageMaintenance>.Instance)
        { RetentionCheck = TimeSpan.FromMilliseconds(100) };
        await maintenance.StartAsync(default);
        try
        {
            await Task.Delay(500);
            Assert.True(Directory.Exists(kept));
            string history = Path.Combine(root, "clusters", "aging", "History"), deleted = Directory.GetFiles(history).Single();
            File.Move(deleted, Path.Combine(history, DateTimeOffset.UtcNow.AddDays(-8).ToString("yyyyMMddHHmmssfff") + Path.GetFileName(deleted)[17..]));
            for (int i = 0; i < 50 && Directory.Exists(kept); i++) await Task.Delay(100);
        }
        finally { await maintenance.StopAsync(default); }
        Assert.False(Directory.Exists(kept));
    }

    [Fact]
    public void StagingSweepRemovesOnlyOldMatchingEntries()
    {
        if (!OperatingSystem.IsLinux()) return;
        string parent = Path.Combine(root, "sweep");
        DateTime started = DateTime.UtcNow;
        string old = Path.Combine(parent, ".stage-old"), locked = Path.Combine(old, "locked"), young = Path.Combine(parent, ".stage-young");
        string published = Path.Combine(parent, "published"), link = Path.Combine(parent, ".stage-link"), target = Path.Combine(root, "target");
        foreach (string path in new[] { locked, young, published, target }) Directory.CreateDirectory(path);
        File.CreateSymbolicLink(link, target);
        File.WriteAllText(Path.Combine(target, "file"), "");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        foreach (string path in new[] { old, published }) Directory.SetLastWriteTimeUtc(path, started.AddHours(-1));
        Directory.SetLastWriteTimeUtc(young, started.AddHours(1));

        ClusterStagingSweep.Sweep(parent, started, _ => { }, ".stage-*");

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(young) && Directory.Exists(published) && File.Exists(Path.Combine(target, "file")));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task ReleaseCacheKeepsReferencedAndNewestReleases()
    {
        if (!OperatingSystem.IsLinux()) return;
        string cache = Path.Combine(root, "releases");
        string[] versions = ["1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5"];
        for (int i = 0; i < versions.Length; i++)
        {
            Directory.CreateDirectory(Path.Combine(cache, versions[i]));
            Directory.SetLastWriteTimeUtc(Path.Combine(cache, versions[i]), DateTime.UtcNow.AddHours(i - 10));
        }
        Directory.CreateDirectory(Path.Combine(cache, ".objects"));
        ClusterActiveRevision Revision(string version) => new("r", [], DateTimeOffset.UtcNow, version);
        var references = ClusterStorageMaintenance.PackageReferences([new ClusterDefinition
        {
            UniqueName = "c", PackageSelection = new(1, "1.0.2", "", "", ""), ActiveDeployment = Revision("1.0.1"),
        }]);
        Assert.Equal(["1.0.1", "1.0.2"], references.Order());

        await new ClusterPackageService(null!, () => "", cache).CollectAsync(references, default);

        Assert.Equal(["1.0.1", "1.0.2", "1.0.4", "1.0.5"], Directory.GetDirectories(cache).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')).Order());
    }

    // SE-791: an update's checkpoints keep the same content, so they must not start a collection.
    [Fact]
    public void ReferenceIdentityChangesOnlyWithRetainedContent()
    {
        ClusterPreparationRequest Preparation(string hash) => new(hash, "{}", "", []);
        ClusterActiveRevision Revision(string name, string hash) => new(name, [], DateTimeOffset.UtcNow)
        { Provenance = new(new(1, "v", "package", "commit", "key"), hash, Preparation(hash)) };
        var active = Revision("active", new string('a', 64));
        var cluster = new ClusterDefinition
        {
            UniqueName = "c", ActiveDeployment = active, PreviousDeployment = Revision("old", new string('b', 64)),
            Preparation = Preparation(new string('c', 64)),
        };
        string before = ClusterStorageMaintenance.ReferenceIdentity([cluster]);

        var update = new ClusterUpdate(Guid.NewGuid(), new("active", "next", []), active, false, ClusterUpdatePhase.Stopping,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        cluster.Update = update;
        Assert.Equal(before, ClusterStorageMaintenance.ReferenceIdentity([cluster]));
        cluster.Update = update with { Phase = ClusterUpdatePhase.Starting, UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(5) };
        Assert.Equal(before, ClusterStorageMaintenance.ReferenceIdentity([cluster]));
        cluster.Update = update with { Phase = ClusterUpdatePhase.Complete };
        Assert.Equal(before, ClusterStorageMaintenance.ReferenceIdentity([cluster]));

        // Activation releases the oldest revision: that one is worth a collection.
        cluster.PreviousDeployment = active;
        cluster.ActiveDeployment = Revision("next", new string('c', 64));
        Assert.NotEqual(before, ClusterStorageMaintenance.ReferenceIdentity([cluster]));
    }

    [Fact]
    public void ReferencesIncludeActivePreparedRollbackAndInterruptedUpdate()
    {
        static ClusterActiveRevision Revision(string hash) => new(hash, [], DateTimeOffset.UtcNow)
        { Provenance = new(new(1, "v", "package", "commit", "key"), hash, new(hash, "{}", "", [])) };
        var cluster = new ClusterDefinition { DependencyManifestSha256 = "prepared", ActiveDeployment = Revision("active"),
            PreviousDeployment = Revision("rollback"), Update = new(Guid.NewGuid(), new("active", "pending", []),
                Revision("interrupted"), false, ClusterUpdatePhase.Activating, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        Assert.True(ClusterStorageMaintenance.DependencyReferences([cluster]).SetEquals(["prepared", "active", "rollback", "interrupted"]));
        cluster.Update = cluster.Update with { Phase = ClusterUpdatePhase.Complete };
        Assert.DoesNotContain("interrupted", ClusterStorageMaintenance.DependencyReferences([cluster]));
        cluster.ActiveDeployment = cluster.ActiveDeployment with { Provenance = null };
        Assert.Throws<InvalidDataException>(() => ClusterStorageMaintenance.DependencyReferences([cluster]));
    }

    [Fact]
    public async Task ModReceiptRequiresMatchingVersionAndVerifiedBytes()
    {
        string payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(payload, "mod.txt"), "mod");
        var receipt = new ClusterWorkshopModStager.Receipt(123, 42, await ClusterDeploymentFiles.InspectAsync(payload, default));
        await File.WriteAllBytesAsync(Path.Combine(root, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, ClusterDeploymentFiles.JsonOptions));
        Assert.Equal(42, (await ClusterWorkshopModStager.ReadReceiptAsync(root, 123, 42, default)).Version);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterWorkshopModStager.ReadReceiptAsync(root, 123, 43, default));
        await File.WriteAllTextAsync(Path.Combine(payload, "mod.txt"), "bad");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterWorkshopModStager.ReadReceiptAsync(root, 123, 42, default));
        Assert.Throws<InvalidOperationException>(() => ClusterWorkshopModStager.ObservedVersion(123,
            new Dictionary<long, WorkshopUpdateObservation> { [123] = new(123, "mod", null, "unavailable") }));
    }
}

// These tests intentionally hold the process-wide collector lease.
[CollectionDefinition("Cluster storage", DisableParallelization = true)]
public sealed class ClusterStorageCollection;
