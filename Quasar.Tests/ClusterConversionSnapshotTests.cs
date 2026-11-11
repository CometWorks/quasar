using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Tests;

public sealed class ClusterConversionSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "conversion-snapshots-" + Guid.NewGuid());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExtractsVerifiedCopiesFromAllHostsAndRejectsWrongLifecycle(bool wrongLifecycle, bool deduplicated)
    {
        var id = Guid.NewGuid();
        var cluster = new ClusterDefinition { UniqueName = "demo", ActiveDeployment = new("revision", [], DateTimeOffset.UtcNow) };
        string backupRoot = Path.Combine(root, "Clusters", Hash(Encoding.UTF8.GetBytes(cluster.UniqueName)), id.ToString("N"));
        Directory.CreateDirectory(backupRoot);
        var snapshots = new List<HostSnapshot>();
        foreach (string host in new[] { "host-1", "host-2" })
        {
            string name = "runtime/nodes/regular/data/cluster/saves/P-0001/save.mps";
            // Big enough to be an object of the store when the backup is deduplicated (SE-1349).
            byte[] payload = Encoding.UTF8.GetBytes(host + " saved world data" + new string('.', 5000));
            var metadata = new { schemaVersion = 2, clusterId = cluster.UniqueName, hostId = host, snapshotId = id,
                revision = "revision", bundleManifestSha256 = "bundle", captureFence = wrongLifecycle ? "wrong" : cluster.GetLifecycleId(),
                files = new Dictionary<string, string> { [name] = Hash(payload) } };
            string path = Path.Combine(backupRoot, Hash(Encoding.UTF8.GetBytes(host)) + ".tar");
            using (var file = File.Create(path))
            using (var writer = new TarWriter(file))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "snapshot.json")
                    { DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(metadata, Json)) });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(payload) });
            }
            byte[] archive = File.ReadAllBytes(path);
            if (deduplicated)
            {
                using (var input = File.OpenRead(path)) ClusterBackupStore.Write(Path.GetDirectoryName(backupRoot)!, path, input);
                File.Delete(path);
            }
            snapshots.Add(new(cluster.UniqueName, host, id, "revision", "bundle", Hash(archive), archive.Length));
        }
        var backup = new ClusterBackup(id, cluster.UniqueName, DateTimeOffset.UtcNow, Admin.ExportConsistency.Quiescent, cluster, snapshots.ToArray(), false);
        File.WriteAllBytes(Path.Combine(backupRoot, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(backup, Json));
        var service = new ClusterBackupService(null!, null!, null!, null!, new WebServiceOptions { BackupDirectory = root }, null!, null!);
        string target = Path.Combine(root, "converted-inputs");
        if (wrongLifecycle)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractConversionSnapshotAsync(backup, target, default));
            return;
        }
        var roots = await service.ExtractConversionSnapshotAsync(backup, target, default);
        Assert.Equal(2, roots.Count);
        foreach (var (host, directory) in roots)
            Assert.Equal(host + " saved world data" + new string('.', 5000), File.ReadAllText(Path.Combine(directory, "nodes/regular/data/cluster/saves/P-0001/save.mps")));
        Assert.Equal(2, (await service.ExtractConversionSnapshotAsync(backup, target, default)).Count);
        if (deduplicated)
        {
            Assert.False(File.Exists(Path.Combine(backupRoot, Hash(Encoding.UTF8.GetBytes("host-1")) + ".tar")));
            File.Delete(Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(backupRoot)!, ClusterBackupStore.Objects), "*", SearchOption.AllDirectories).First());
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractConversionSnapshotAsync(backup, target, default));
            return;
        }
        string retained = Path.Combine(backupRoot, Hash(Encoding.UTF8.GetBytes("host-1")) + ".tar");
        Assert.Equal(snapshots[0].ArchiveSha256, Hash(File.ReadAllBytes(retained)));
        File.AppendAllText(retained, "corruption");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractConversionSnapshotAsync(backup, target, default));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
