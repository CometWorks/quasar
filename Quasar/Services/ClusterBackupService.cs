using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Models;
using Quasar.Services.Backup;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed class ClusterBackupService(ClusterCatalog catalog, ClusterHostClient hosts, ClusterOperationStore operations,
    ClusterDeploymentService deployments, WebServiceOptions options, QuasarBackupSettingsService settings, ClusterGatewayClient gateway)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private string Root(string cluster) => Path.Combine(options.BackupDirectory, "Clusters", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cluster))));
    public ClusterBackup[] List(string cluster) => !Directory.Exists(Root(cluster)) ? []
        : Directory.EnumerateFiles(Root(cluster), "backup.json", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(Path.GetDirectoryName(p)!).StartsWith('.'))
            .Select(Read).OrderByDescending(b => b.CreatedAt).ToArray();

    public Task<ClusterOperation> ArchiveExportAsync(string clusterId, string artifactId, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.export.archive", key, actor, new { artifactId }, async ct => await catalog.WithLifecycleAsync(clusterId, async cluster =>
        {
            if (cluster.ActiveDeployment is null) throw new InvalidOperationException("Export retrieval needs a managed Gateway Host.");
            var owner = cluster.ActiveDeployment.Hosts.Single(h => h.Deployment.Gateway is not null);
            string directory = Path.Combine(Root(clusterId), "exports", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(artifactId))));
            Admin.ArtifactDescriptor descriptor;
            if (Directory.Exists(directory))
            {
                descriptor = JsonSerializer.Deserialize<Admin.ArtifactDescriptor>(File.ReadAllBytes(Path.Combine(directory, "export.json")), Json)
                    ?? throw new InvalidDataException("Export backup metadata is missing.");
                if (descriptor.ArtifactId != artifactId) throw new InvalidDataException("Export backup identity mismatch.");
            }
            else
            {
                descriptor = (await gateway.GetArtifactAsync(cluster, artifactId, ct)).Data;
                string staging = directory + ".partial";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                try
                {
                    await hosts.RetrieveArtifactAsync(Target(cluster, owner), descriptor, Path.Combine(staging, "world"), ct);
                    await File.WriteAllBytesAsync(Path.Combine(staging, "export.json"), JsonSerializer.SerializeToUtf8Bytes(descriptor, Json), ct);
                    Directory.Move(staging, directory);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
            string manifest = string.Join('\n', descriptor.FileChecksums.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value));
            if (!Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest))).Equals(descriptor.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Stored export manifest checksum mismatch.");
            string world = Path.Combine(directory, "world");
            if (Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories).Count() != descriptor.FileChecksums.Count)
                throw new InvalidDataException("Stored export inventory mismatch.");
            long bytes = 0;
            foreach (var (relative, expected) in descriptor.FileChecksums)
            {
                if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".."))
                    throw new InvalidDataException("Export backup path is invalid.");
                using var file = File.OpenRead(Path.Combine(directory, "world", relative));
                bytes += file.Length;
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Stored export checksum mismatch; Gateway artifact was not released.");
            }
            if (bytes != descriptor.SizeBytes) throw new InvalidDataException("Stored export size mismatch.");
            // Release only after a complete verified local copy. Release itself is a durable Gateway operation.
            await operations.ExecuteGatewayAsync(cluster, "cluster.artifact.release", "DELETE", "artifacts/" + Uri.EscapeDataString(artifactId),
                new { }, "archived-" + descriptor.ManifestSha256, actor, gateway, ct);
            return new Admin.AdminEnvelope<Admin.ArtifactDescriptor>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, descriptor);
        }, ct), token);

    internal async Task CaptureDueAsync(CancellationToken token)
    {
        var rule = settings.GetSettings().Server;
        if (!rule.Enabled) return;
        foreach (var cluster in catalog.GetClusters())
        {
            if (cluster.GoalState != DedicatedServerGoalState.Off || cluster.ActiveDeployment is null
                || cluster.ShutdownProof?.LifecycleId != cluster.GetLifecycleId() || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }) continue;
            rule.LastBackupUtc = List(cluster.UniqueName).Where(b => b.Automatic).Select(b => (DateTimeOffset?)b.CreatedAt).FirstOrDefault();
            if (!AutomaticBackupService.IsDue(rule, DateTimeOffset.UtcNow)) continue;
            // Stable within the stopped lifecycle: a lost response does not capture another cut.
            var id = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cluster.GetLifecycleId() + ":" + rule.LastBackupUtc))[..16]);
            await CaptureAsync(cluster.UniqueName, new(id, Automatic: true), "scheduled-" + id.ToString("N"), "backup-scheduler", token);
        }
    }

    public Task<ClusterOperation> CaptureAsync(string clusterId, ClusterBackupRequest request, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.backup.create", key, actor, request,
            async ct => new Admin.AdminEnvelope<ClusterBackup>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
                await catalog.WithLifecycleAsync(clusterId, cluster => CaptureCoreAsync(cluster, request, ct), ct)), token);

    internal async Task<ClusterBackup> CaptureCoreAsync(ClusterDefinition cluster, ClusterBackupRequest request, CancellationToken token)
    {
        if (cluster.Update is { Phase: not ClusterUpdatePhase.Complete } || request.SnapshotId == Guid.Empty || cluster.GoalState != DedicatedServerGoalState.Off
            || cluster.ActiveDeployment is null || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw new InvalidOperationException("Backup requires a snapshot ID, goal Off and a complete managed deployment.");
        bool clean = cluster.ShutdownProof?.LifecycleId == cluster.GetLifecycleId();
        if (!clean && !request.AllowCrashConsistent)
            throw new InvalidOperationException("Clean-Down proof is absent; explicitly request a crash-consistent backup.");
        string destination = Path.Combine(Root(cluster.UniqueName), request.SnapshotId.ToString("N"));
        if (Directory.Exists(destination))
        {
            var existing = Verify(destination, cluster.UniqueName);
            if (existing.Deployment.GetLifecycleId() != cluster.GetLifecycleId())
                throw new InvalidOperationException("Snapshot ID belongs to a different stopped lifecycle.");
            await ReleaseHostCopiesAsync(existing, token);
            return existing;
        }
        Directory.CreateDirectory(Root(cluster.UniqueName));
        string staging = Path.Combine(Root(cluster.UniqueName), ".capture-" + request.SnapshotId.ToString("N"));
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var snapshots = new List<HostContract.HostSnapshot>();
            foreach (var host in cluster.ActiveDeployment.Hosts)
            {
                var target = Target(cluster, host);
                var snapshot = (await hosts.CaptureSnapshotAsync(target,
                    new(cluster.UniqueName, request.SnapshotId, host.Deployment.BundleManifestSha256, cluster.GetLifecycleId()), token)).Data;
                if (snapshot.ClusterId != cluster.UniqueName || snapshot.HostId != host.HostId
                    || snapshot.SnapshotId != request.SnapshotId || snapshot.Revision != cluster.ActiveDeployment.Revision
                    || snapshot.BundleManifestSha256 != host.Deployment.BundleManifestSha256)
                    throw new InvalidDataException("Host snapshot does not match the stopped deployment.");
                await hosts.TransferSnapshotAsync(target, snapshot, Path.Combine(staging, FileName(snapshot.HostId)), false, token);
                snapshots.Add(snapshot);
            }
            var backup = new ClusterBackup(request.SnapshotId, cluster.UniqueName, DateTimeOffset.UtcNow,
                clean ? Admin.ExportConsistency.Quiescent : Admin.ExportConsistency.CrashConsistent,
                cluster.Clone(), snapshots.ToArray(), request.Automatic);
            await File.WriteAllBytesAsync(Path.Combine(staging, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(backup, Json), token);
            Verify(staging, cluster.UniqueName);
            Directory.Move(staging, destination);
            await ReleaseHostCopiesAsync(backup, token);
            if (request.Automatic)
                foreach (var old in List(cluster.UniqueName).Where(b => b.Automatic)
                    .Skip(settings.GetSettings().Server.RetentionCount))
                    Directory.Delete(Path.Combine(Root(cluster.UniqueName), old.SnapshotId.ToString("N")), true);
            return backup;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    // Conversion works against this verified stopped cut, never a live Host runtime.
    internal async Task<Dictionary<string, string>> ExtractConversionSnapshotAsync(ClusterBackup backup, string destination, CancellationToken token)
    {
        string source = Path.Combine(Root(backup.ClusterId), backup.SnapshotId.ToString("N"));
        Verify(source, backup.ClusterId);
        Directory.CreateDirectory(destination);
        Quasar.ClusterDeployment.ClusterWorldFiles.Private(destination);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in backup.Hosts)
        {
            string root = Path.Combine(destination, Path.GetFileNameWithoutExtension(FileName(host.HostId)));
            if (Directory.Exists(root)) Directory.Delete(root, true); // Derived copy of the retained verified backup.
            Directory.CreateDirectory(root);
            using var input = File.OpenRead(Path.Combine(source, FileName(host.HostId)));
            using var reader = new System.Formats.Tar.TarReader(input);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                string name = entry.Name.TrimEnd('/');
                Quasar.ClusterDeployment.ClusterWorldFiles.Relative(name);
                if (!seen.Add(name) || seen.Count > 200_000 || (total += entry.Length) > host.ArchiveBytes
                    || entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.Directory))
                    throw new InvalidDataException("Invalid snapshot entry.");
                string path = Path.Combine(root, name);
                if (entry.EntryType == System.Formats.Tar.TarEntryType.Directory) Directory.CreateDirectory(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var output = new FileStream(path, FileMode.CreateNew);
                    if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token);
                }
            }
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "snapshot.json"), token));
            var metadata = manifest.RootElement;
            if (metadata.GetProperty("clusterId").GetString() != backup.ClusterId
                || metadata.GetProperty("hostId").GetString() != host.HostId
                || metadata.GetProperty("snapshotId").GetGuid() != backup.SnapshotId
                || metadata.GetProperty("revision").GetString() != host.Revision
                || metadata.GetProperty("bundleManifestSha256").GetString() != host.BundleManifestSha256
                || metadata.GetProperty("captureFence").GetString() != backup.Deployment.GetLifecycleId())
                throw new InvalidDataException("Snapshot conversion identity mismatch.");
            var expected = metadata.GetProperty("files").Deserialize<Dictionary<string, string>>()!;
            var actual = await Quasar.ClusterDeployment.ClusterDeploymentFiles.InspectAsync(Path.Combine(root, "runtime"), token);
            if (expected.Count != actual.Count || actual.Any(p => !expected.TryGetValue("runtime/" + p.Key, out var hash)
                || !p.Value.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Snapshot conversion inventory mismatch.");
            roots.Add(host.HostId, Path.Combine(root, "runtime"));
        }
        return roots;
    }

    private async Task ReleaseHostCopiesAsync(ClusterBackup backup, CancellationToken token)
    {
        foreach (var host in backup.Deployment.ActiveDeployment!.Hosts)
            await hosts.ReleaseSnapshotAsync(Target(backup.Deployment, host), backup.Hosts.Single(s => s.HostId == host.HostId), token);
    }

    public Task<ClusterOperation> RestoreAsync(string clusterId, ClusterRestoreRequest request, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.backup.restore", key, actor, request,
            async ct => new Admin.AdminEnvelope<ClusterActiveRevision>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
                await catalog.WithLifecycleAsync(clusterId, cluster => RestoreCoreAsync(cluster, request, ct), ct)), token);

    private async Task<ClusterActiveRevision> RestoreCoreAsync(ClusterDefinition cluster, ClusterRestoreRequest request, CancellationToken token)
    {
        if (cluster.Update is { Phase: not ClusterUpdatePhase.Complete } || request.RestoreId == Guid.Empty || cluster.GoalState != DedicatedServerGoalState.Off || cluster.ActiveDeployment is null)
            throw new InvalidOperationException("Restore requires a restore ID and a stopped managed cluster.");
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));
        if (cluster.LastRestoreHash == hash && cluster.ActiveDeployment.Revision == request.Deployment.Revision)
            return cluster.ActiveDeployment;
        string directory = Path.Combine(Root(cluster.UniqueName), request.SnapshotId.ToString("N"));
        var backup = Verify(directory, cluster.UniqueName);
        if (!backup.Hosts.Select(h => h.HostId).Order().SequenceEqual(request.Deployment.Hosts.Select(h => h.HostId).Order())
            || !backup.Hosts.Select(h => h.HostId).Order().SequenceEqual(cluster.ActiveDeployment.Hosts.Select(h => h.HostId).Order()))
            throw new InvalidDataException("Restore must retain the complete saved Host inventory.");

        if (cluster.PendingRestoreHash is { } pending && pending != hash)
            throw new InvalidOperationException("Resume the original interrupted restore before requesting another.");
        await deployments.ActivateCoreAsync(cluster, request.Deployment, token, restore: true, dryRun: true);
        var prepared = new List<(ClusterDefinition Target, HostContract.HostSnapshotRestore Request)>();
        foreach (var host in cluster.ActiveDeployment.Hosts)
        {
            var candidate = request.Deployment.Hosts.Single(h => h.HostId == host.HostId);
            if (candidate.CommandUrl != host.CommandUrl || candidate.TokenEnvironmentVariable != host.TokenEnvironmentVariable)
                throw new InvalidDataException("Restore cannot relocate Hosts.");
            var target = Target(cluster, host);
            var snapshot = backup.Hosts.Single(h => h.HostId == host.HostId);
            await hosts.TransferSnapshotAsync(target, snapshot, Path.Combine(directory, FileName(host.HostId)), true, token);
            var restore = new HostContract.HostSnapshotRestore(cluster.UniqueName, request.RestoreId, request.SnapshotId,
                snapshot.ArchiveSha256, candidate.Activation.BundleManifestPath, candidate.Activation.BundleManifestSha256,
                candidate.Activation.ExecutorTokenEnvironmentVariable);
            await hosts.RestoreSnapshotAsync(target, restore, token, preview: true);
            prepared.Add((target, restore));
        }
        await catalog.RecordPendingDeploymentAsync(cluster, hash, token, restore: true);
        await operations.FenceGatewayOperationsForRestoreAsync(cluster.UniqueName, token);
        foreach (var (target, restore) in prepared) await hosts.RestoreSnapshotAsync(target, restore, token);
        return await deployments.ActivateCoreAsync(cluster, request.Deployment, token, restore: true);
    }

    private static ClusterDefinition Target(ClusterDefinition cluster, ClusterHostRevision host)
    { var target = cluster.Clone(); target.HostCommandUrl = host.CommandUrl; target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable; return target; }
    private static string FileName(string host) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(host))) + ".tar";
    private static ClusterBackup Read(string path) => JsonSerializer.Deserialize<ClusterBackup>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException("Backup manifest is empty.");
    private static ClusterBackup Verify(string directory, string clusterId)
    {
        var backup = Read(Path.Combine(directory, "backup.json"));
        if (backup.ClusterId != clusterId || backup.Hosts.Length == 0
            || backup.Hosts.Select(h => h.HostId).Distinct().Count() != backup.Hosts.Length)
            throw new InvalidDataException("Backup identity or inventory is invalid.");
        foreach (var host in backup.Hosts)
        {
            string path = Path.Combine(directory, FileName(host.HostId));
            using var input = File.OpenRead(path);
            if (input.Length != host.ArchiveBytes || !Convert.ToHexString(SHA256.HashData(input)).Equals(host.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup archive checksum mismatch.");
        }
        return backup;
    }
}

public sealed record ClusterBackupRequest(Guid SnapshotId, bool AllowCrashConsistent = false, bool Automatic = false);
public sealed record ClusterRestoreRequest(Guid SnapshotId, Guid RestoreId, ClusterDeploymentRequest Deployment);
public sealed record ClusterBackup(Guid SnapshotId, string ClusterId, DateTimeOffset CreatedAt, Admin.ExportConsistency Consistency,
    ClusterDefinition Deployment, HostContract.HostSnapshot[] Hosts, bool Automatic);
