using System.IO.Compression;
using System.Text;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class WorldSaveFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-world-files-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Staging => Path.Combine(_root, "staging");
    private const string Checkpoint = "<MyObjectBuilder_Checkpoint><SessionName>Imported</SessionName></MyObjectBuilder_Checkpoint>";

    public WorldSaveFilesTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "Sandbox.sbc"), Checkpoint);
    }

    [Fact]
    public async Task FolderPreservesWorldDataSkipsBackupAndCleansOwnedStaging()
    {
        Directory.CreateDirectory(Path.Combine(Source, "Storage", "Mod"));
        File.WriteAllText(Path.Combine(Source, "Storage", "Mod", "state.xml"), "mod data");
        File.WriteAllText(Path.Combine(Source, "Planet.vx2"), "voxel data");
        Directory.CreateDirectory(Path.Combine(Source, "Backup"));
        File.WriteAllText(Path.Combine(Source, "Backup", "old.sbc"), "old");
        string path;
        using (var world = await WorldSaveFiles.FromFolderAsync(Source, Staging))
        {
            path = world.WorldPath;
            Assert.Equal("mod data", File.ReadAllText(Path.Combine(path, "Storage", "Mod", "state.xml")));
            Assert.Equal("voxel data", File.ReadAllText(Path.Combine(path, "Planet.vx2")));
            Assert.False(Directory.Exists(Path.Combine(path, "Backup")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
        Assert.False(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(Source, "Sandbox.sbc")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Save/")]
    [InlineData("Outer/Save/")]
    public async Task ZipFindsOneWorldAtRootOrInsideWrapper(string prefix)
    {
        using var zip = Zip((prefix + "Sandbox.sbc", Checkpoint), (prefix + "Planet.vx2", "voxels"), (prefix + "Storage/Empty/", ""),
            (prefix + "Backup/old/Sandbox.sbc", Checkpoint));
        using var world = await WorldSaveFiles.FromZipAsync(zip, Staging);
        Assert.Equal("voxels", File.ReadAllText(Path.Combine(world.WorldPath, "Planet.vx2")));
        Assert.False(Directory.Exists(Path.Combine(world.WorldPath, "Backup")));
        Assert.True(Directory.Exists(Path.Combine(world.WorldPath, "Storage", "Empty")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/drive")]
    [InlineData("Folder/CON.txt")]
    [InlineData("Folder/trailing.")]
    public async Task ZipRejectsUnsafePathsAndCleansUp(string path)
    {
        using var zip = Zip(("Sandbox.sbc", Checkpoint), (path, "bad"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromZipAsync(zip, Staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task ZipRejectsMultipleWorldsAndCaseCollisions()
    {
        using var multiple = Zip(("One/Sandbox.sbc", Checkpoint), ("Two/Sandbox.sbc", Checkpoint));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromZipAsync(multiple, Staging));
        using var duplicate = Zip(("Sandbox.sbc", Checkpoint), ("planet.vx2", "a"), ("Planet.vx2", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromZipAsync(duplicate, Staging));
    }

    [Fact]
    public async Task ZipRejectsImplicitDirectoryCaseConflictsAndTooManyEntries()
    {
        using var collision = Zip(("Sandbox.sbc", Checkpoint), ("Storage/one", "a"), ("storage/two", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromZipAsync(collision, Staging));
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            for (int i = 0; i <= WorldSaveFiles.MaxEntries; i++) zip.CreateEntry("entry-" + i);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromZipAsync(stream, Staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task CorruptZipAndCheckpointEntitiesAreRejected()
    {
        using var badZip = new MemoryStream("not a zip"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => WorldSaveFiles.FromZipAsync(badZip, Staging));
        File.WriteAllText(Path.Combine(Source, "Sandbox.sbc"),
            "<!DOCTYPE root [<!ENTITY external SYSTEM 'file:///etc/passwd'>]><MyObjectBuilder_Checkpoint>&external;</MyObjectBuilder_Checkpoint>");
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => WorldSaveFiles.FromFolderAsync(Source, Staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task ZipRejectsLinks()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000);
        }
        zip.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromZipAsync(zip, Staging));
    }

    [Fact]
    public async Task FolderRejectsLinksOverlapAndInvalidCheckpoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(Source, "link"), Path.Combine(Source, "Sandbox.sbc"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromFolderAsync(Source, Staging));
            File.Delete(Path.Combine(Source, "link"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromFolderAsync(Source, Source));
        File.WriteAllText(Path.Combine(Source, "Sandbox.sbc"), "<not-a-world />");
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSaveFiles.FromFolderAsync(Source, Staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task CancellationRemovesPartialCopy()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorldSaveFiles.FromFolderAsync(Source, Staging, progress, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Theory]
    [InlineData("../world")]
    [InlineData(".hidden")]
    [InlineData("bad/name")]
    [InlineData("NUL")]
    public void SaveNamesArePortableSingleFolderNames(string name)
        => Assert.Throws<InvalidOperationException>(() => WorldSaveFiles.ValidateSaveName(name));

    private static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        stream.Position = 0;
        return stream;
    }

    private sealed class InlineProgress(Action<WorldImportProgress> report) : IProgress<WorldImportProgress>
    { public void Report(WorldImportProgress value) => report(value); }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
