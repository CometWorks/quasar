using System.IO.Compression;
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

    internal static MemoryStream CreateServerZip(ExistingServerKind kind, string prefix = "")
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
        return Zip(files.ToArray());
    }

    private static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        stream.Position = 0;
        return stream;
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<WorldImportProgress>
    { public void Report(WorldImportProgress value) => cancellation.Cancel(); }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
