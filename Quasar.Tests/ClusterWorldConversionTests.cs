using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Quasar.ClusterDeployment;
using Quasar.Host.Contract.V1;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterWorldConversionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-world-conversion-" + Guid.NewGuid());
    private string World()
    {
        string world = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(world, "Storage"));
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        File.WriteAllBytes(Path.Combine(world, "Storage", "voxel.vx2"), [0, 1, 2, 255]);
        return world;
    }

    [Fact]
    public async Task CopyTransferAndReplayPreserveSourceAndRejectChangedDestination()
    {
        string source = World(), copy = Path.Combine(root, "copy"), archive = Path.Combine(root, "world.tar");
        var original = await ClusterDeploymentFiles.InspectAsync(source, default);
        await ClusterWorldFiles.CopyAsync(source, copy, default);
        string hash = await ClusterWorldFiles.PackAsync(copy, archive, default);
        async Task<string> Import()
        {
            using var input = File.OpenRead(archive);
            return await ClusterWorldFiles.UnpackAsync(input, hash, Path.Combine(root, "host"), default);
        }
        string destination = await Import();
        Assert.Equal(destination, await Import());
        await ClusterWorldFiles.VerifyAsync(destination, original, default);
        File.WriteAllText(Path.Combine(destination, "Sandbox.sbc"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(Import);
        await ClusterWorldFiles.VerifyAsync(source, original, default);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "host"), ".world-*"));
    }

    [Theory]
    [InlineData("../escape", false)]
    [InlineData("C:/escape", false)]
    [InlineData("link", true)]
    [InlineData("Sandbox.sbc", false)]
    public async Task UnsafeOrDuplicateArchiveEntriesAreRejected(string name, bool link)
    {
        Directory.CreateDirectory(root);
        byte[] world = Encoding.UTF8.GetBytes("<MyObjectBuilder_Checkpoint />");
        var pins = new Dictionary<string, DeploymentFile> { ["Sandbox.sbc"] = new(ClusterDeploymentFiles.Hash(world), world.Length, false) };
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(pins, ClusterDeploymentFiles.JsonOptions);
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "world-files.json") { DataStream = new MemoryStream(metadata) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Sandbox.sbc") { DataStream = new MemoryStream(world) });
            var extra = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, name);
            if (link) extra.LinkName = "../escape"; else extra.DataStream = new MemoryStream(world);
            writer.WriteEntry(extra);
        }
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterWorldFiles.UnpackAsync(archive,
            ClusterDeploymentFiles.Hash(metadata), Path.Combine(root, "host"), default));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "host")));
    }

    [Fact]
    public async Task PayloadCorruptionAndChangedSourceCannotReuseOldCopy()
    {
        string source = World(), copy = Path.Combine(root, "copy"), archive = Path.Combine(root, "world.tar");
        await ClusterWorldFiles.CopyAsync(source, copy, default);
        string hash = await ClusterWorldFiles.PackAsync(source, archive, default);
        byte[] bytes = File.ReadAllBytes(archive);
        int offset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("<MyObjectBuilder_Checkpoint />"));
        Assert.True(offset >= 0);
        bytes[offset] = (byte)'!';
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterWorldFiles.UnpackAsync(new MemoryStream(bytes), hash,
            Path.Combine(root, "host"), default));
        File.AppendAllText(Path.Combine(source, "Sandbox.sbc"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterWorldFiles.CopyAsync(source, copy, default));
        Assert.Equal("<MyObjectBuilder_Checkpoint />", File.ReadAllText(Path.Combine(copy, "Sandbox.sbc")));
    }

    [Fact]
    public void ReverseConversionIncludesAllHostsAndSkipsUnusedNodeStores()
    {
        string one = Path.Combine(root, "host-1"), two = Path.Combine(root, "host-2");
        string regular = Path.Combine(one, "nodes/regular/data/cluster/saves");
        string wa = Path.Combine(two, "nodes/wa/data/saves");
        Directory.CreateDirectory(Path.Combine(regular, "P-0001"));
        Directory.CreateDirectory(Path.Combine(wa, "GLOBAL"));
        Directory.CreateDirectory(Path.Combine(two, "nodes/spare/data"));
        Assert.Equal(new[] { regular, wa }, ClusterConversionService.FindSaveStores([one, two]));
    }

    [Fact]
    public void ConverterRejectsWrongCheckpointOrExternalEntities()
    {
        string world = World();
        ClusterWorldConverter.Validate(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<wrong />");
        Assert.Throws<InvalidDataException>(() => ClusterWorldConverter.Validate(world));
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<!DOCTYPE x SYSTEM 'file:///missing'><MyObjectBuilder_Checkpoint />");
        Assert.Throws<System.Xml.XmlException>(() => ClusterWorldConverter.Validate(world));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
