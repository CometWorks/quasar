using System.Formats.Tar;
using System.Security.Cryptography;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

// SE-1349: cluster backups as skeletons plus content-addressed objects.
public sealed class ClusterBackupStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-backup-store-" + Guid.NewGuid().ToString("N"));

    public ClusterBackupStoreTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    // A tar as the Host writes one: pax entries with the time of writing, the manifest last.
    internal static byte[] Tar(params (string Name, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "runtime/"));
            foreach (var (name, data) in files)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        }
        return output.ToArray();
    }

    internal static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private (long Bytes, string Sha256) Store(string backup, byte[] archive, int buffered = ClusterBackupStore.BufferedBytes)
    {
        Directory.CreateDirectory(Path.Combine(root, backup));
        return ClusterBackupStore.Write(root, Path.Combine(root, backup, "host.tar"), new MemoryStream(archive), buffered);
    }

    private byte[] Read(string backup)
    {
        using var input = ClusterBackupStore.Open(root, Path.Combine(root, backup, "host.tar"));
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    private string[] Objects() => Directory.Exists(Path.Combine(root, ClusterBackupStore.Objects))
        ? Directory.GetFiles(Path.Combine(root, ClusterBackupStore.Objects), "*", SearchOption.AllDirectories) : [];

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void UnchangedBackupAddsNoObjectAndChangedOneOnlyItsNewFiles()
    {
        byte[] save = Bytes(100_000, 1), voxel = Bytes(30_000, 2);
        byte[] first = Tar(("runtime/nodes/a/save.mps", save), ("runtime/nodes/b/save.mps", save), ("runtime/world/v.vx2", voxel),
            ("runtime/world/small.cfg", Bytes(100, 3)), ("snapshot.json", Bytes(20_000, 4)));
        var stored = Store("first", first);
        Assert.Equal((first.LongLength, Hash(first)), stored);
        // The save replica on the second node is the same object, the small file and the manifest stay in the skeleton.
        Assert.Equal(2, Objects().Length);

        byte[] second = Tar(("runtime/nodes/a/save.mps", save), ("runtime/nodes/b/save.mps", save), ("runtime/world/v.vx2", voxel),
            ("runtime/world/small.cfg", Bytes(100, 3)), ("snapshot.json", Bytes(20_000, 5)));
        Store("second", second);
        Assert.Equal(2, Objects().Length);

        byte[] changed = Bytes(100_000, 6);
        byte[] third = Tar(("runtime/nodes/a/save.mps", changed), ("runtime/nodes/b/save.mps", save), ("runtime/world/v.vx2", voxel),
            ("snapshot.json", Bytes(20_000, 7)));
        Store("third", third);
        Assert.Equal(3, Objects().Length);
        Assert.Contains(Objects(), path => Path.GetFileName(path) == Hash(changed));

        Assert.Equal(first, Read("first"));
        Assert.Equal(second, Read("second"));
        Assert.Equal(third, Read("third"));
    }

    [Fact]
    public void CollectRemovesOnlyObjectsNoBackupRefersTo()
    {
        byte[] kept = Bytes(10_000, 1), dropped = Bytes(10_000, 2), staged = Bytes(10_000, 3);
        byte[] first = Tar(("runtime/kept", kept), ("runtime/dropped", dropped));
        Store("first", first);
        Store("second", Tar(("runtime/kept", kept)));
        // A capture still in its staging folder holds its objects too.
        Store(".capture-running", Tar(("runtime/staged", staged)));
        Directory.Delete(Path.Combine(root, "first"), true);

        Assert.Equal(dropped.Length, ClusterBackupStore.Collect(root));
        Assert.Equal(new[] { Hash(kept), Hash(staged) }.Order(), Objects().Select(Path.GetFileName).Order());
        Assert.Equal(0, ClusterBackupStore.Collect(root));
    }

    [Fact]
    public void LargeFilesStreamThroughTheStoreToo()
    {
        byte[] big = Bytes(200_000, 1);
        byte[] archive = Tar(("runtime/big", big), ("runtime/other", Bytes(5_000, 2)));
        Store("first", archive, buffered: 8192);
        Store("second", archive, buffered: 8192);
        Assert.Equal(2, Objects().Length);
        Assert.Contains(Objects(), path => Path.GetFileName(path) == Hash(big));
        Assert.DoesNotContain(Directory.GetFiles(root, "*incoming*", SearchOption.AllDirectories), _ => true);
        Assert.Equal(archive, Read("second"));
    }

    // Whatever the Host sends comes back unchanged, even when it is not a whole tar.
    [Theory]
    [InlineData("not a tar")]
    [InlineData("cut in a header")]
    [InlineData("cut in a buffered file")]
    [InlineData("cut in a streamed file")]
    [InlineData("trailing bytes")]
    public void ArchivesThatDoNotParseComeBackByteForByte(string shape)
    {
        byte[] tar = Tar(("runtime/a", Bytes(100_000, 1)), ("runtime/b", Bytes(10, 2)));
        byte[] archive = shape switch
        {
            "not a tar" => Bytes(5_000, 3),
            "cut in a header" => tar[..700],
            "cut in a buffered file" or "cut in a streamed file" => tar[..50_000],
            _ => [.. tar, .. Bytes(1_000, 4)],
        };
        var stored = Store("odd", archive, buffered: shape == "cut in a streamed file" ? 8192 : ClusterBackupStore.BufferedBytes);
        Assert.Equal((archive.LongLength, Hash(archive)), stored);
        Assert.Equal(archive, Read("odd"));
        if (shape == "not a tar") Assert.Empty(Objects());
    }

    [Fact]
    public void BackupsKeptAsTarFilesStillRead()
    {
        byte[] archive = Tar(("runtime/a", Bytes(10_000, 1)));
        Directory.CreateDirectory(Path.Combine(root, "old"));
        File.WriteAllBytes(Path.Combine(root, "old", "host.tar"), archive);
        Assert.True(ClusterBackupStore.Exists(Path.Combine(root, "old", "host.tar")));
        Assert.Equal(archive, Read("old"));
    }

    [Fact]
    public void AMissingOrResizedObjectFailsTheRead()
    {
        byte[] archive = Tar(("runtime/a", Bytes(10_000, 1)));
        Store("first", archive);
        string path = Assert.Single(Objects());
        File.Delete(path);
        Assert.ThrowsAny<IOException>(() => Read("first"));
    }
}
