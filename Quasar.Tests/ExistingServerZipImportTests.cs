using System.IO.Compression;
using Bunit;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ExistingServerZipImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-server-zip-tests-" + Guid.NewGuid().ToString("N"));
    private string Staging => Path.Combine(_root, "staging");
    private readonly ExistingServerImportService _importer = new(null!, null!, null!, NullLogger<ExistingServerImportService>.Instance);

    [Theory]
    [InlineData(ExistingServerKind.Vanilla, "")]
    [InlineData(ExistingServerKind.Vanilla, "Outer/Server/")]
    [InlineData(ExistingServerKind.Torch, "")]
    [InlineData(ExistingServerKind.Torch, "Outer/Server/")]
    public async Task ArchiveRetainsMultipleWorldsAndCleansUp(ExistingServerKind kind, string prefix)
    {
        using var zip = CreateServerZip(kind, prefix);
        string source;
        using (var staged = await _importer.StageZipAsync(kind, zip, Staging))
        {
            var analysis = staged.Analysis;
            source = analysis.SourcePath;
            Assert.Equal("Uploaded Server", analysis.ServerName);
            Assert.Equal(2, analysis.Worlds.Count);
            Assert.True(analysis.Worlds[0].IsConfiguredWorld);
            Assert.Equal("Primary", analysis.Worlds[0].Name);
            Assert.Equal("voxels", File.ReadAllText(Path.Combine(analysis.Worlds[0].Path, "Planet.vx2")));
            var request = new ExistingServerImportRequest(kind, source, ExistingServerTransferMode.Move,
                "Uploaded", "uploaded", [analysis.Worlds[0].Path], analysis.Worlds[0].Path, ExistingServerImportSections.Identity, true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _importer.ImportArchiveAsync(staged, request));
        }
        Assert.False(Directory.Exists(source));
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
        Assert.True(zip.CanRead);
    }

    [Theory]
    [InlineData(ExistingServerKind.Vanilla)]
    [InlineData(ExistingServerKind.Torch)]
    public async Task UploadedConfigCannotReadWorldsOrInstancesOutsideArchive(ExistingServerKind kind)
    {
        string external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        File.WriteAllText(Path.Combine(external, "SpaceEngineers-Dedicated.cfg"), "<MyConfigDedicated />");
        using var zip = Zip(("SpaceEngineers-Dedicated.cfg", $"<MyConfigDedicated><LoadWorld>{external}</LoadWorld></MyConfigDedicated>"),
            ("Torch.cfg", $"<TorchConfig><InstancePath>{external}</InstancePath></TorchConfig>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _importer.StageZipAsync(kind, zip, Staging));
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
        Assert.True(File.Exists(Path.Combine(external, "Sandbox.sbc")));
    }

    [Fact]
    public async Task UploadedTorchCannotFollowExternalInstancePath()
    {
        string external = Path.Combine(_root, "external");
        Directory.CreateDirectory(Path.Combine(external, "Saves", "Primary"));
        File.WriteAllText(Path.Combine(external, "SpaceEngineers-Dedicated.cfg"), "<MyConfigDedicated />");
        File.WriteAllText(Path.Combine(external, "Saves", "Primary", "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        using var zip = Zip(("Torch.cfg", $"<TorchConfig><InstancePath>{external}</InstancePath></TorchConfig>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _importer.StageZipAsync(ExistingServerKind.Torch, zip, Staging));
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/drive")]
    [InlineData("CON.txt")]
    [InlineData("storage/data")]
    public async Task UnsafeOrCaseConflictingArchiveIsRejectedAndCleaned(string path)
    {
        using var zip = Zip(("SpaceEngineers-Dedicated.cfg", "<MyConfigDedicated />"),
            ("Saves/Primary/Sandbox.sbc", "<MyObjectBuilder_Checkpoint />"), ("Storage/data", "a"), (path, "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _importer.StageZipAsync(ExistingServerKind.Vanilla, zip, Staging));
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
    }

    [Fact]
    public async Task CorruptAndAmbiguousArchivesAreRejectedAndCleaned()
    {
        using var corrupt = new MemoryStream("not a zip"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => _importer.StageZipAsync(ExistingServerKind.Vanilla, corrupt, Staging));
        using var ambiguous = Zip(("One/SpaceEngineers-Dedicated.cfg", "<MyConfigDedicated />"),
            ("Two/SpaceEngineers-Dedicated.cfg", "<MyConfigDedicated />"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _importer.StageZipAsync(ExistingServerKind.Vanilla, ambiguous, Staging));
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
    }

    [Fact]
    public async Task CancellationRemovesPartialUpload()
    {
        using var zip = CreateServerZip(ExistingServerKind.Vanilla);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _importer.StageZipAsync(ExistingServerKind.Vanilla,
            zip, Staging, new CancelProgress(cancellation), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
    }

    [Theory]
    [InlineData(ExistingServerKind.Vanilla)]
    [InlineData(ExistingServerKind.Torch)]
    public async Task BrowserTransfersConfigsAndDetectedWorldsWithoutUnusedPayloads(ExistingServerKind kind)
    {
        await using var context = new BunitContext();
        string instance = kind == ExistingServerKind.Torch ? "Instance/" : "";
        string large = new('x', 1024 * 1024);
        string[] ignored = ["DedicatedServer64/engine.dll", "Torch.exe", "Plugins/plugin.dll", "Logs/server.log",
            instance + "Logs/instance.log", instance + "Saves/Primary/Backup/old/Planet.vx2", "Content/CustomWorlds/Preset/Planet.vx2"];
        using var zip = CreateServerZip(kind, "Wrapper/", ignored.Select(path => ("Wrapper/" + path, large))
            .Append(("Wrapper/Content/CustomWorlds/Preset/Sandbox.sbc", "<MyObjectBuilder_Checkpoint />"))
            .Append(("Wrapper/" + instance + "Saves/Primary/Storage/Mod/state.xml", "persistent mod data")).ToArray());
        byte[] bytes = zip.ToArray();
        var file = BrowserZipTestFile.Setup(context, bytes);
        await using var browser = await BrowserZipFileStream.OpenAsync(context.JSInterop.JSRuntime, default, default);
        using var staged = await _importer.StageZipAsync(kind, browser, Staging);
        Assert.Equal(2, staged.Analysis.Worlds.Count);
        string primary = staged.Analysis.Worlds.Single(world => world.Name == "Primary").Path;
        Assert.Equal("persistent mod data", File.ReadAllText(Path.Combine(primary, "Storage", "Mod", "state.xml")));
        Assert.False(Directory.Exists(Path.Combine(primary, "Backup")));
        Assert.False(Directory.Exists(Path.Combine(staged.Analysis.SourcePath, "Plugins")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(staged.Analysis.SourcePath)!.FullName, "upload.zip")));
        AssertPayloadsNotRequested(bytes, file, ignored.Select(path => "Wrapper/" + path));
        Assert.True(file.Ranges.Sum(range => range.Length) < bytes.LongLength / 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidConfigOrCheckpointFailsBeforeTerrainTransfer(bool invalidCheckpoint)
    {
        await using var context = new BunitContext();
        using var zip = Zip(("SpaceEngineers-Dedicated.cfg", invalidCheckpoint ? "<MyConfigDedicated />" : "<broken"),
            ("Saves/Primary/Sandbox.sbc", invalidCheckpoint ? "<not-a-world />" : "<MyObjectBuilder_Checkpoint />"),
            ("Saves/Primary/Planet.vx2", new string('v', 1024 * 1024)));
        byte[] bytes = zip.ToArray();
        var file = BrowserZipTestFile.Setup(context, bytes);
        await using var browser = await BrowserZipFileStream.OpenAsync(context.JSInterop.JSRuntime, default, default);
        await Assert.ThrowsAnyAsync<Exception>(() => _importer.StageZipAsync(ExistingServerKind.Vanilla, browser, Staging));
        AssertPayloadsNotRequested(bytes, file, ["Saves/Primary/Planet.vx2"]);
        if (!invalidCheckpoint) AssertPayloadsNotRequested(bytes, file, ["Saves/Primary/Sandbox.sbc"]);
        Assert.Empty(Directory.GetFileSystemEntries(Staging));
    }

    [Theory]
    [InlineData(ExistingServerKind.Vanilla)]
    [InlineData(ExistingServerKind.Torch)]
    public async Task ArchiveDiscoveryUsesConfiguredInstanceAndWorldPaths(ExistingServerKind kind)
    {
        string instance = kind == ExistingServerKind.Torch ? "CustomInstance/" : "";
        var files = new List<(string, string)>
        {
            (instance + "SpaceEngineers-Dedicated.cfg", "<MyConfigDedicated><ServerName>Custom</ServerName><LoadWorld>Custom/LiveWorld</LoadWorld></MyConfigDedicated>"),
            (instance + "Custom/LiveWorld/Sandbox.sbc", "<MyObjectBuilder_Checkpoint />"),
            (instance + "Custom/LiveWorld/Planet.vx2", "custom voxels"),
        };
        if (kind == ExistingServerKind.Torch)
        {
            files.Add(("Torch.cfg", "<TorchConfig><InstancePath>C:\\Torch\\CustomInstance</InstancePath></TorchConfig>"));
            files.Add(("Old/SpaceEngineers-Dedicated.cfg", "this unused config is invalid"));
        }
        using var zip = Zip(files.ToArray());
        using var staged = await _importer.StageZipAsync(kind, zip, Staging);
        var world = Assert.Single(staged.Analysis.Worlds);
        Assert.True(world.IsConfiguredWorld);
        Assert.Equal("custom voxels", File.ReadAllText(Path.Combine(world.Path, "Planet.vx2")));
        Assert.False(Directory.Exists(Path.Combine(staged.Analysis.SourcePath, "Old")));
    }

    private static void AssertPayloadsNotRequested(byte[] bytes, BrowserZipTestFile file, IEnumerable<string> names)
    {
        using var input = new MemoryStream(bytes);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        foreach (string name in names)
        {
            var entry = archive.GetEntry(name)!;
            using var content = entry.Open();
            long start = input.Position, end = start + entry.CompressedLength;
            // The ZIP reader probes up to 4 KiB before the end record to locate its signature.
            // This bounded metadata read may overlap the final payload; no content request may.
            Assert.DoesNotContain(file.Ranges, range =>
                !(range.Length <= 4096 && range.Offset + range.Length == bytes.LongLength - 18)
                && range.Offset < end && range.Offset + range.Length > start);
        }
    }

    internal static MemoryStream CreateServerZip(ExistingServerKind kind, string prefix = "", params (string Path, string Content)[] additionalFiles)
    {
        string instance = kind == ExistingServerKind.Torch ? "Instance/" : "";
        var files = new List<(string, string)>
        {
            (prefix + instance + "SpaceEngineers-Dedicated.cfg", "<MyConfigDedicated><ServerName>Uploaded Server</ServerName><LoadWorld>C:\\OldServer\\Saves\\Primary</LoadWorld></MyConfigDedicated>"),
        };
        if (kind == ExistingServerKind.Torch)
            files.Add((prefix + "Torch.cfg", "<TorchConfig><InstancePath>Instance</InstancePath><RestartOnCrash>true</RestartOnCrash></TorchConfig>"));
        foreach (string name in new[] { "Primary", "Other" })
        {
            files.Add((prefix + instance + $"Saves/{name}/Sandbox.sbc", "<MyObjectBuilder_Checkpoint />"));
            files.Add((prefix + instance + $"Saves/{name}/Sandbox_config.sbc", $"<MyObjectBuilder_WorldConfiguration><SessionName>{name}</SessionName><Settings><MaxPlayers>12</MaxPlayers></Settings></MyObjectBuilder_WorldConfiguration>"));
            files.Add((prefix + instance + $"Saves/{name}/Planet.vx2", "voxels"));
        }
        files.AddRange(additionalFiles);
        return Zip(files.ToArray());
    }

    private static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path, CompressionLevel.NoCompression).Open());
                writer.Write(content);
            }
        stream.Position = 0;
        return stream;
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<WorldImportProgress>
    { public void Report(WorldImportProgress value) => cancellation.Cancel(); }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
