using System.Text.Json;
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
