using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Host;

// Caller holds the execution gate. Snapshots never stop a process or infer a clean shutdown.
internal sealed class DeploymentSnapshots(string stateDirectory, string hostId, AttachmentStore attachments,
    NodeActualizer nodes, GatewayActualizer gateway)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record SnapshotManifest(int SchemaVersion, string ClusterId, string HostId, Guid SnapshotId,
        string Revision, string BundleManifestSha256, Dictionary<string, string> Files, string[] CredentialHashes, Dictionary<string, string>? StorageFormats, string CaptureFence,
        Dictionary<string, int> Modes, Dictionary<string, int> Directories);
    private string Root(string clusterId) => Path.Combine(stateDirectory, "snapshots", ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(clusterId)));
    internal string ArchivePath(string clusterId, Guid id) => Path.Combine(Root(clusterId), id.ToString("N") + ".tar");

    internal void Release(string clusterId, Guid id, string hash)
    {
        string archive = ArchivePath(clusterId, id);
        if (!File.Exists(archive)) return;
        if (HashFile(archive) != hash) throw new InvalidDataException("Snapshot release checksum mismatch.");
        string journals = Path.Combine(stateDirectory, "restores");
        if (Directory.Exists(journals) && Directory.EnumerateFiles(journals, "*.json").Any(path =>
            JsonSerializer.Deserialize<HostContract.HostSnapshotRestore>(File.ReadAllBytes(path), Json)?.SnapshotId == id))
            throw new InvalidOperationException("Snapshot is required by an interrupted restore.");
        File.Delete(archive);
    }

    internal void ExportArtifact(string clusterId, string artifactId, Stream output)
    {
        if (artifactId.Length is < 1 or > 128 || artifactId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new InvalidDataException("Invalid artifact ID.");
        var attachment = attachments.GetAll().Single(a => a.ClusterId == clusterId);
        var manifest = ExecutionBundle.ReadManifest(attachment.BundleManifestPath!, attachment.BundleManifestSha256!);
        if (manifest.Gateway is null || manifest.RuntimeRoot is null) throw new InvalidOperationException("This Host does not own the Gateway.");
        string root = Path.Combine(manifest.RuntimeRoot, "gateway", "exports", artifactId);
        var files = ExecutionBundle.RefuseTree(root);
        using var writer = new TarWriter(output, leaveOpen: true);
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            using var input = File.OpenRead(file);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, Path.GetRelativePath(root, file).Replace('\\', '/')) { DataStream = input });
        }
    }

    internal void Recover()
    {
        string directory = Path.Combine(stateDirectory, "restores");
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            string? clusterId = null;
            try
            {
                var request = JsonSerializer.Deserialize<HostContract.HostSnapshotRestore>(File.ReadAllBytes(path), Json)
                    ?? throw new InvalidDataException("Restore journal is empty.");
                clusterId = request.ClusterId;
                Restore(request);
            }
            // One cluster's unrecoverable restore must not take the Host and its other clusters down.
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                if (clusterId is null) Console.Error.WriteLine($"Restore journal {path} is unreadable and was ignored: {exception.Message}");
                else PausedClusters.Pause(clusterId, $"interrupted restore {path} could not be recovered: {exception.Message}");
            }
        }
    }

    internal async Task ReceiveAsync(string clusterId, Guid id, string hash, Stream source, CancellationToken token)
    {
        if (id == Guid.Empty || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid snapshot identity.");
        string archive = ArchivePath(clusterId, id), temporary = archive + ".upload-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Root(clusterId));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root(clusterId), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024]; long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > 1024L * 1024 * 1024 * 1024) throw new InvalidDataException("Snapshot exceeds 1 TiB.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                output.Flush(true);
            }
            if (!HashFile(temporary).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Snapshot upload checksum mismatch.");
            Describe(temporary, clusterId, id);
            if (File.Exists(archive))
            {
                if (!HashFile(archive).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Snapshot ID already identifies different bytes.");
            }
            else File.Move(temporary, archive);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal HostContract.HostSnapshot Capture(HostContract.HostSnapshotRequest request)
    {
        if (request.SnapshotId == Guid.Empty || request.CaptureFence.Length != 64 || !request.CaptureFence.All(Uri.IsHexDigit)) throw new InvalidDataException("Snapshot ID is required.");
        var attachment = attachments.GetAll().Single(a => a.ClusterId == request.ClusterId);
        if (attachment.BundleManifestSha256 != request.ExpectedBundleManifestSha256)
            throw new InvalidOperationException("Active deployment changed before snapshot.");
        nodes.EnsureStopped(request.ClusterId);
        gateway.EnsureStopped(request.ClusterId);
        string archive = ArchivePath(request.ClusterId, request.SnapshotId);
        if (File.Exists(archive)) return Describe(archive, request.ClusterId, request.SnapshotId, request.ExpectedBundleManifestSha256, request.CaptureFence);
        var bundle = ExecutionBundle.Load(attachment.BundleManifestPath!, attachment.BundleManifestSha256!);
        string runtime = bundle.Manifest.RuntimeRoot ?? throw new InvalidDataException("Snapshot requires a managed runtime root.");
        Directory.CreateDirectory(Root(request.ClusterId));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root(request.ClusterId), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string staging = Path.Combine(Root(request.ClusterId), ".capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var modes = new Dictionary<string, int>(StringComparer.Ordinal);
            var directories = new Dictionary<string, int>(StringComparer.Ordinal);
            ExecutionBundle.RefuseTree(runtime);
            foreach (string source in Directory.EnumerateDirectories(runtime, "*", SearchOption.AllDirectories))
                directories.Add("runtime/" + Path.GetRelativePath(runtime, source).Replace('\\', '/') + "/", Mode(source));
            foreach (string source in ExecutionBundle.RefuseTree(runtime))
            {
                string relative = Path.GetRelativePath(runtime, source);
                string copy = Path.Combine(staging, "runtime", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(copy, FileMode.CreateNew))
                { input.CopyTo(output); output.Flush(true); }
                files.Add("runtime/" + relative, HashFile(copy));
                modes.Add("runtime/" + relative, Mode(source));
            }
            if (files.Count == 0) throw new InvalidDataException("Runtime data is empty.");
            var manifest = new SnapshotManifest(2, request.ClusterId, hostId, request.SnapshotId, bundle.Manifest.Revision,
                request.ExpectedBundleManifestSha256, files, Credentials(bundle.Manifest, attachment.TokenEnvironmentVariable), bundle.Manifest.StorageFormats, request.CaptureFence, modes, directories);
            using (var output = new FileStream(archive + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new TarWriter(output, leaveOpen: true))
                {
                    using var metadata = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "snapshot.json") { DataStream = metadata });
                    foreach (var (name, mode) in directories.OrderBy(d => d.Key, StringComparer.Ordinal))
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name) { Mode = (UnixFileMode)mode });
                    foreach (var file in files.Keys.Order(StringComparer.Ordinal))
                    {
                        using var data = File.OpenRead(Path.Combine(staging, file));
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file) { DataStream = data, Mode = (UnixFileMode)modes[file] });
                    }
                }
                output.Flush(true);
            }
            File.Move(archive + ".tmp", archive);
            return Describe(archive, request.ClusterId, request.SnapshotId, request.ExpectedBundleManifestSha256, request.CaptureFence);
        }
        finally
        {
            Directory.Delete(staging, true);
            if (File.Exists(archive + ".tmp")) File.Delete(archive + ".tmp");
        }
    }

    internal HostContract.HostSnapshot Describe(string archive, string clusterId, Guid id, string? expectedBundle = null, string? expectedFence = null)
    {
        using var input = File.OpenRead(archive);
        using var reader = new TarReader(input);
        var manifest = ReadManifest(reader);
        if (manifest.ClusterId != clusterId || manifest.HostId != hostId || manifest.SnapshotId != id
            || expectedBundle is not null && manifest.BundleManifestSha256 != expectedBundle
            || expectedFence is not null && manifest.CaptureFence != expectedFence)
            throw new InvalidDataException("Snapshot provenance mismatch.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            ValidateEntryPath(entry.Name);
            if (entry.EntryType == TarEntryType.Directory && manifest.Directories.TryGetValue(entry.Name, out int mode)
                && (int)entry.Mode == mode && seen.Add(entry.Name)) continue;
            if (!manifest.Modes.TryGetValue(entry.Name, out int fileMode) || (int)entry.Mode != fileMode)
                throw new InvalidDataException("Snapshot permissions mismatch.");
            if (entry.EntryType != TarEntryType.RegularFile || !manifest.Files.TryGetValue(entry.Name, out var hash)
                || !seen.Add(entry.Name)) throw new InvalidDataException("Unexpected snapshot file.");
            string actual = entry.DataStream is null ? ExecutionBundle.Hash([])
                : Convert.ToHexString(SHA256.HashData(entry.DataStream)).ToLowerInvariant();
            if (actual != hash) throw new InvalidDataException("Snapshot file checksum mismatch.");
        }
        if (seen.Count != manifest.Files.Count + manifest.Directories.Count) throw new InvalidDataException("Incomplete snapshot.");
        return new(clusterId, hostId, id, manifest.Revision, manifest.BundleManifestSha256, HashFile(archive), new FileInfo(archive).Length);
    }

    internal void Restore(HostContract.HostSnapshotRestore request, bool preview = false)
    {
        if (request.RestoreId == Guid.Empty) throw new InvalidDataException("Restore ID is required.");
        nodes.EnsureStopped(request.ClusterId);
        gateway.EnsureStopped(request.ClusterId);
        var attachment = attachments.GetAll().Single(a => a.ClusterId == request.ClusterId);
        var candidate = ExecutionBundle.Load(request.CandidateManifestPath, request.CandidateManifestSha256);
        var current = ExecutionBundle.Load(attachment.BundleManifestPath!, attachment.BundleManifestSha256!);
        if (candidate.Manifest.ClusterId != request.ClusterId || candidate.Manifest.HostId != hostId
            || candidate.Manifest.RuntimeRoot != current.Manifest.RuntimeRoot )
            throw new InvalidDataException("Restore requires a verified candidate for the same Host and runtime root.");
        string runtime = candidate.Manifest.RuntimeRoot!;
        string archive = ArchivePath(request.ClusterId, request.SnapshotId);
        if (HashFile(archive) != request.ArchiveSha256) throw new InvalidDataException("Snapshot archive hash mismatch.");
        string parent = Path.GetDirectoryName(runtime)!;
        string staging = Path.Combine(parent, ".restore-" + request.RestoreId.ToString("N"));
        string previous = runtime + ".before-restore-" + request.RestoreId.ToString("N");
        string marker = Path.Combine(runtime, ".quasar-restore.json");
        string journal = Path.Combine(stateDirectory, "restores", request.RestoreId.ToString("N") + ".json");
        var identity = new { request.RestoreId, request.SnapshotId, request.ArchiveSha256, request.CandidateManifestSha256,
            revision = candidate.Manifest.Revision, activated = false };
        byte[] identityBytes = JsonSerializer.SerializeToUtf8Bytes(identity, Json);
        if (File.Exists(marker))
        {
            using var recorded = JsonDocument.Parse(File.ReadAllBytes(marker));
            if (recorded.RootElement.GetProperty("restoreId").GetGuid() == request.RestoreId)
            {
                if (recorded.RootElement.GetProperty("archiveSha256").GetString() != request.ArchiveSha256
                    || recorded.RootElement.GetProperty("candidateManifestSha256").GetString() != request.CandidateManifestSha256)
                    throw new InvalidOperationException("Restore ID is bound to different input.");
                if (File.Exists(journal)) File.Delete(journal);
                if (!preview) PausedClusters.Resume(request.ClusterId);
                return;
            }
        }
        if (candidate.Manifest.Revision == current.Manifest.Revision) throw new InvalidDataException("Restore requires a newly prepared deployment with fresh node/admin/executor credential environment references; credential rotation produces a new revision.");
        if (!Directory.Exists(runtime) && Directory.Exists(previous)) Directory.Move(previous, runtime);
        if (Directory.Exists(staging)) { ExecutionBundle.RefuseTree(staging); Directory.Delete(staging, true); }
        Directory.CreateDirectory(staging);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var input = File.OpenRead(archive);
            using var reader = new TarReader(input);
            var manifest = ReadManifest(reader);
            if (manifest.ClusterId != request.ClusterId || manifest.HostId != hostId || manifest.SnapshotId != request.SnapshotId)
                throw new InvalidDataException("Snapshot belongs to a different cluster or Host.");
            ExecutionBundle.RequireCompatibleStorage(candidate.Manifest.StorageFormats, manifest.StorageFormats);
            var credentials = Credentials(candidate.Manifest, request.CandidateExecutorTokenEnvironmentVariable);
            if (credentials.Length == 0 || credentials.Intersect(manifest.CredentialHashes).Any()
                || credentials.Intersect(Credentials(current.Manifest, attachment.TokenEnvironmentVariable)).Any())
                throw new InvalidDataException("Restore requires new runtime credentials; use new environment references and rotate every node/admin/executor token.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.GetNextEntry() is { } entry)
            {
                ValidateEntryPath(entry.Name);
                if (entry.EntryType == TarEntryType.Directory && manifest.Directories.TryGetValue(entry.Name, out int mode)
                    && (int)entry.Mode == mode && seen.Add(entry.Name))
                {
                    Directory.CreateDirectory(Path.Combine(staging, entry.Name["runtime/".Length..]));
                    continue;
                }
                if (!manifest.Modes.TryGetValue(entry.Name, out int fileMode) || (int)entry.Mode != fileMode)
                    throw new InvalidDataException("Snapshot permissions mismatch.");
                if (entry.EntryType != TarEntryType.RegularFile || !manifest.Files.TryGetValue(entry.Name, out string? hash)
                    || !entry.Name.StartsWith("runtime/", StringComparison.Ordinal) || !seen.Add(entry.Name))
                    throw new InvalidDataException("Unexpected snapshot entry.");
                string relative = entry.Name["runtime/".Length..];
                if (relative.Split('/').Any(p => p is "" or "." or "..") || relative.Contains('\\') || Path.IsPathRooted(relative))
                    throw new InvalidDataException("Snapshot entry escapes runtime root.");
                string path = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var output = new FileStream(path, FileMode.CreateNew))
                { entry.DataStream?.CopyTo(output); output.Flush(true); }
                if (HashFile(path) != hash) throw new InvalidDataException("Snapshot file checksum mismatch.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)fileMode);
            }
            if (seen.Count != manifest.Files.Count + manifest.Directories.Count) throw new InvalidDataException("Snapshot is incomplete.");
            if (!OperatingSystem.IsWindows())
                foreach (var (name, mode) in manifest.Directories.OrderByDescending(d => d.Key.Length))
                    File.SetUnixFileMode(Path.Combine(staging, name["runtime/".Length..]), (UnixFileMode)mode);
            if (preview) return;
            File.WriteAllBytes(Path.Combine(staging, ".quasar-restore.json"), identityBytes);
            if (candidate.Manifest.Gateway is not null)
            {
                string registry = Path.Combine(staging, "gateway/registry");
                if (!Directory.Exists(registry)) throw new InvalidDataException("Snapshot has no Gateway Registry.");
                File.WriteAllBytes(Path.Combine(registry, "managed-restore.json"), JsonSerializer.SerializeToUtf8Bytes(
                    new { restoreId = request.RestoreId, revision = candidate.Manifest.Revision }, Json));
            }
            if (Directory.Exists(previous)) throw new InvalidOperationException("Previous restore data exists; reconcile it before another restore.");
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            using (var file = new FileStream(journal + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(request, Json)); file.Flush(true); }
            File.Move(journal + ".tmp", journal, true);
            if (Directory.Exists(runtime)) Directory.Move(runtime, previous);
            Directory.Move(staging, runtime); // Keep previous data for explicit recovery; never prune it as a backup.
            File.Delete(journal);
            PausedClusters.Resume(request.ClusterId);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static SnapshotManifest ReadManifest(TarReader reader)
    {
        var entry = reader.GetNextEntry();
        if (entry?.Name != "snapshot.json" || entry.EntryType != TarEntryType.RegularFile
            || entry.Length is <= 0 or > 64 * 1024 * 1024 || entry.DataStream is null)
            throw new InvalidDataException("Invalid snapshot manifest.");
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(entry.DataStream, Json)
            ?? throw new InvalidDataException("Missing snapshot manifest.");
        if (manifest.SchemaVersion != 2 || manifest.Files.Count is < 1 or > 500_000
            || manifest.Directories is null || manifest.Modes is null || manifest.Directories.Count > 500_000
            || manifest.Modes.Count != manifest.Files.Count || manifest.Modes.Values.Concat(manifest.Directories.Values).Any(m => m < 0 || m > 511))
            throw new InvalidDataException("Unsupported snapshot schema or file count.");
        return manifest;
    }

    private static int Mode(string path) => OperatingSystem.IsWindows() ? 448 : (int)File.GetUnixFileMode(path) & 511;
    private static void ValidateEntryPath(string name)
    {
        if (!name.StartsWith("runtime/", StringComparison.Ordinal)) throw new InvalidDataException("Snapshot entry is outside runtime.");
        string relative = name["runtime/".Length..].TrimEnd('/');
        if (relative.Split('/').Any(p => p is "" or "." or "..") || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new InvalidDataException("Snapshot entry escapes runtime root.");
    }

    private static string[] Credentials(BundleManifest manifest, string executorVariable)
    {
        var variables = manifest.Nodes.SelectMany(n => n.SecretEnvironment ?? [])
            .Concat(manifest.Gateway?.SecretEnvironment ?? []).Append(new("executor", executorVariable)).Distinct();
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in variables)
        {
            string value = Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException("Missing runtime credential: " + name);
            if (key == "CLUSTER_ADMIN_TOKENS")
            {
                using var tokens = JsonDocument.Parse(File.ReadAllBytes(value));
                foreach (var entry in tokens.RootElement.GetProperty("tokens").EnumerateArray())
                    foreach (string field in new[] { "sha256", "previousSha256" })
                        if (entry.TryGetProperty(field, out var hash) && hash.ValueKind == JsonValueKind.String)
                        {
                            string text = hash.GetString()!;
                            if (text.Length != 64 || !text.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid scoped token hash.");
                            hashes.Add(text.ToLowerInvariant());
                        }
            }
            else hashes.Add(ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(value)));
        }
        return hashes.ToArray();
    }
    private static string HashFile(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
}
