using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Host;

// Callers hold the execution gate, except for an online capture (see Capture). Snapshots never stop a process or infer a clean shutdown.
internal sealed class DeploymentSnapshots(string stateDirectory, string hostId, AttachmentStore attachments,
    NodeActualizer nodes, GatewayActualizer gateway)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record SnapshotManifest(int SchemaVersion, string ClusterId, string HostId, Guid SnapshotId,
        string Revision, string BundleManifestSha256, Dictionary<string, string> Files, string[] CredentialHashes, Dictionary<string, string>? StorageFormats, string CaptureFence,
        Dictionary<string, int> Modes, Dictionary<string, int> Directories, string[]? RebuiltMods = null);
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
        ValidArtifactId(artifactId);
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

    // Online captures copy a running cluster outside the execution gate, so Host supervision keeps going.
    // While one runs, nothing else may move or rewrite that cluster's runtime.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _online = new(StringComparer.OrdinalIgnoreCase);

    internal void RefuseDuringOnlineCapture(string clusterId)
    {
        if (_online.ContainsKey(clusterId))
            throw new InvalidOperationException("An online backup is copying this cluster's runtime; retry when it completes.");
    }

    internal void RefuseDuringRestore(string clusterId)
    {
        string directory = Path.Combine(stateDirectory, "restores");
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any(path =>
                JsonSerializer.Deserialize<HostContract.HostSnapshotRestore>(File.ReadAllBytes(path), Json)?.ClusterId
                    .Equals(clusterId, StringComparison.OrdinalIgnoreCase) != false))
            throw new InvalidOperationException("A restore of this cluster has not completed; finish it first.");
    }

    internal HostContract.HostSnapshot Capture(HostContract.HostSnapshotRequest request)
    {
        if (request.SnapshotId == Guid.Empty || request.CaptureFence.Length != 64 || !request.CaptureFence.All(Uri.IsHexDigit)) throw new InvalidDataException("Snapshot ID is required.");
        if (request.Online != request.BytesPerSecond > 0) throw new InvalidDataException("An online snapshot needs a byte rate, an offline one none.");
        var attachment = attachments.GetAll().Single(a => a.ClusterId == request.ClusterId);
        if (attachment.BundleManifestSha256 != request.ExpectedBundleManifestSha256)
            throw new InvalidOperationException("Active deployment changed before snapshot.");
        if (!request.Online)
        {
            RefuseDuringOnlineCapture(request.ClusterId);
            nodes.EnsureStopped(request.ClusterId);
            gateway.EnsureStopped(request.ClusterId);
            return CaptureCore(request, attachment);
        }
        if (!_online.TryAdd(request.ClusterId, true)) throw new InvalidOperationException("An online backup of this cluster is already running.");
        try { return CaptureCore(request, attachment); }
        finally { _online.TryRemove(request.ClusterId, out _); }
    }

    // One pass from the runtime into the archive, hashing on the way; the manifest goes last (schema 3).
    private HostContract.HostSnapshot CaptureCore(HostContract.HostSnapshotRequest request, HostContract.HostAttachmentSpec attachment)
    {
        string archive = ArchivePath(request.ClusterId, request.SnapshotId);
        if (File.Exists(archive)) return Describe(archive, request.ClusterId, request.SnapshotId, request.ExpectedBundleManifestSha256, request.CaptureFence);
        var bundle = ExecutionBundle.Load(attachment.BundleManifestPath!, attachment.BundleManifestSha256!);
        string runtime = bundle.Manifest.RuntimeRoot ?? throw new InvalidDataException("Snapshot requires a managed runtime root.");
        if (request.Online && (bundle.Manifest.Gateway is null) != (request.RegistryArtifactId is null))
            throw new InvalidDataException("An online snapshot takes the Gateway's registry copy on the Gateway Host, and only there.");
        string? registry = request.RegistryArtifactId is { } artifactId
            ? Path.Combine(runtime, "gateway", "exports", ValidArtifactId(artifactId), RegistryFile) : null;
        Directory.CreateDirectory(Root(request.ClusterId));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root(request.ClusterId), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = archive + ".tmp";
        try
        {
            var (directories, sources) = Walk(runtime, request.Online);
            string[] rebuilt = RebuiltMods(bundle.Manifest);
            bool Skip(string relative) => request.Online && LeftOutOnline(relative) || LeftOut(relative, rebuilt);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var modes = new Dictionary<string, int>(StringComparer.Ordinal);
            var directoryModes = new Dictionary<string, int>(StringComparer.Ordinal);
            var rate = request.Online ? new ByteRate(request.BytesPerSecond) : null;
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new TarWriter(output, leaveOpen: true))
                {
                    foreach (string source in directories.Order(StringComparer.Ordinal))
                    {
                        string relative = Path.GetRelativePath(runtime, source).Replace('\\', '/');
                        if (Skip(relative)) continue;
                        int mode;
                        try { mode = Mode(source); }
                        catch (Exception error) when (request.Online && error is FileNotFoundException or DirectoryNotFoundException) { continue; }
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "runtime/" + relative + "/") { Mode = (UnixFileMode)mode });
                        directoryModes.Add("runtime/" + relative + "/", mode);
                    }
                    foreach (string source in sources.Order(StringComparer.Ordinal))
                    {
                        string relative = Path.GetRelativePath(runtime, source).Replace('\\', '/');
                        if (Skip(relative)) continue;
                        WriteFile(writer, source, "runtime/" + relative, request.Online, rate, files, modes);
                    }
                    if (registry is not null)
                    {
                        string name = "runtime/gateway/registry/" + RegistryFile;
                        if (!WriteFile(writer, registry, name, false, rate, files, modes)
                            || !files[name].Equals(request.RegistrySha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The Gateway's registry copy does not match its checksum.");
                    }
                    if (files.Count == 0) throw new InvalidDataException("Runtime data is empty.");
                    var manifest = new SnapshotManifest(3, request.ClusterId, hostId, request.SnapshotId, bundle.Manifest.Revision,
                        request.ExpectedBundleManifestSha256, files, Credentials(bundle.Manifest, attachment.TokenEnvironmentVariable), bundle.Manifest.StorageFormats, request.CaptureFence, modes, directoryModes,
                        rebuilt.Length == 0 ? null : rebuilt);
                    using var metadata = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "snapshot.json") { DataStream = metadata });
                }
                output.Flush(true);
            }
            File.Move(temporary, archive);
            return Describe(archive, request.ClusterId, request.SnapshotId, request.ExpectedBundleManifestSha256, request.CaptureFence);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private const string RegistryFile = "registry.db";

    // #1397: what no snapshot copies. Each node's profile-pinned mods: every restore activates a newly prepared
    // revision, whose profile migration writes them again from the bundle (they were 16 of a 36 GB backup).
    // The round-trip gate's handover-diff dumps: diagnostics, not world state (15 of those 36 GB).
    internal static bool LeftOut(string relative, string[] rebuiltMods)
    {
        string[] parts = relative.Split('/');
        return parts.Length >= 5 && parts[0] == "nodes" && parts[2] == "data" && parts[3] == "cluster" && parts[4] == "handover-diff"
            || rebuiltMods.Any(mods => relative == mods || relative.StartsWith(mods + "/", StringComparison.Ordinal));
    }

    // The runtime-relative Mods directory of every node whose mods the bundle's profile pins.
    internal static string[] RebuiltMods(BundleManifest manifest) => manifest.ProfileContent is not { } profile ? []
        : manifest.Nodes.Where(node => profile.NodeModSeeds.ContainsKey(node.SlotKey))
            .Select(node => "nodes/" + ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(node.SlotKey))[..24] + "/data/Mods")
            .Order(StringComparer.Ordinal).ToArray();

    // A stopped runtime must be complete and free of links. A live one changes under the walk: entries that
    // vanish (retention deletes old saves) are skipped, and files written after the walk are not copied.
    private static (string[] Directories, string[] Files) Walk(string runtime, bool online)
    {
        if (!online)
        {
            string[] files = ExecutionBundle.RefuseTree(runtime);
            return (Directory.EnumerateDirectories(runtime, "*", SearchOption.AllDirectories).ToArray(), files);
        }
        var entries = new DirectoryInfo(runtime).EnumerateFileSystemInfos("*",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToArray();
        if ((File.GetAttributes(runtime) & FileAttributes.ReparsePoint) != 0
            || entries.Any(entry => (entry.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Runtime paths must not contain symbolic links.");
        return (entries.OfType<DirectoryInfo>().Select(entry => entry.FullName).ToArray(),
            entries.OfType<FileInfo>().Select(entry => entry.FullName).ToArray());
    }

    // What an online copy leaves out: the live registry database, which the Gateway's consistent copy
    // replaces; the export area, which holds that copy; temporary names of writes still in flight; sockets.
    // A restore is a crash at the registry copy time, and none of these survive a crash usefully.
    internal static bool LeftOutOnline(string relative)
    {
        string name = Path.GetFileName(relative);
        return relative.StartsWith("gateway/exports/", StringComparison.Ordinal)
            || relative.StartsWith("gateway/registry/" + RegistryFile, StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.Ordinal) || name.Contains(".tmp-", StringComparison.Ordinal)
            || name.EndsWith(".part", StringComparison.Ordinal) || name.EndsWith(".partial", StringComparison.Ordinal)
            || name.EndsWith(".sock", StringComparison.Ordinal);
    }

    // False when an online source vanished before it was opened.
    private static bool WriteFile(TarWriter writer, string source, string name, bool online, ByteRate? rate,
        Dictionary<string, string> files, Dictionary<string, int> modes)
    {
        FileStream input;
        try { input = File.OpenRead(source); }
        catch (Exception error) when (online && error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        using (input)
        {
            int mode = OperatingSystem.IsWindows() ? 448 : (int)File.GetUnixFileMode(input.SafeFileHandle) & 511;
            using var data = new SnapshotFileStream(input, rate);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = (UnixFileMode)mode });
            if (data.Shrunk) Console.Error.WriteLine($"snapshot: {name} shrank while it was copied; its end is zero-filled.");
            files.Add(name, data.Sha256());
            modes.Add(name, mode);
        }
        return true;
    }

    private static string ValidArtifactId(string artifactId) =>
        artifactId.Length is < 1 or > 128 || artifactId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            ? throw new InvalidDataException("Invalid artifact ID.") : artifactId;

    internal HostContract.HostSnapshot Describe(string archive, string clusterId, Guid id, string? expectedBundle = null, string? expectedFence = null)
    {
        using var input = File.OpenRead(archive);
        var manifest = ReadManifest(input);
        using var reader = new TarReader(input);
        if (manifest.ClusterId != clusterId || manifest.HostId != hostId || manifest.SnapshotId != id
            || expectedBundle is not null && manifest.BundleManifestSha256 != expectedBundle
            || expectedFence is not null && manifest.CaptureFence != expectedFence)
            throw new InvalidDataException("Snapshot provenance mismatch.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.Name == ManifestEntry) continue;
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
        RefuseDuringOnlineCapture(request.ClusterId);
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
            var manifest = ReadManifest(input);
            using var reader = new TarReader(input);
            if (manifest.ClusterId != request.ClusterId || manifest.HostId != hostId || manifest.SnapshotId != request.SnapshotId)
                throw new InvalidDataException("Snapshot belongs to a different cluster or Host.");
            ExecutionBundle.RequireCompatibleStorage(candidate.Manifest.StorageFormats, manifest.StorageFormats);
            if ((manifest.RebuiltMods ?? []).Except(RebuiltMods(candidate.Manifest), StringComparer.Ordinal).Any())
                throw new InvalidDataException("The snapshot left out node mods that the candidate deployment does not pin; prepare it from a profile with its mods.");
            var credentials = Credentials(candidate.Manifest, request.CandidateExecutorTokenEnvironmentVariable);
            if (credentials.Length == 0 || credentials.Intersect(manifest.CredentialHashes).Any()
                || credentials.Intersect(Credentials(current.Manifest, attachment.TokenEnvironmentVariable)).Any())
                throw new InvalidDataException("Restore requires new runtime credentials; use new environment references and rotate every node/admin/executor token.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.Name == ManifestEntry) continue;
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

    private const string ManifestEntry = "snapshot.json";

    // Schema 2 archives start with the manifest. Schema 3 archives are written in one pass and end with it.
    // Leaves the stream at the start for the caller's own pass.
    private static SnapshotManifest ReadManifest(Stream archive)
    {
        SnapshotManifest? manifest = null;
        bool first = false, last = false;
        using (var reader = new TarReader(archive, leaveOpen: true))
            for (int index = 0; reader.GetNextEntry() is { } entry; index++)
            {
                last = false;
                if (entry.Name != ManifestEntry) continue;
                if (manifest is not null || entry.EntryType != TarEntryType.RegularFile
                    || entry.Length is <= 0 or > 64 * 1024 * 1024 || entry.DataStream is null)
                    throw new InvalidDataException("Invalid snapshot manifest.");
                manifest = JsonSerializer.Deserialize<SnapshotManifest>(entry.DataStream, Json)
                    ?? throw new InvalidDataException("Missing snapshot manifest.");
                first = index == 0;
                last = true;
            }
        archive.Position = 0;
        if (manifest is null || !(manifest.SchemaVersion == 2 && first || manifest.SchemaVersion == 3 && last)
            || manifest.Files.Count is < 1 or > 500_000
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

// A source file read once into the archive. Its length is fixed when it is opened: a file that keeps
// growing is cut there, and one that shrinks is zero-filled to keep the archive valid. The bytes are
// hashed as they pass and paced to the online byte rate.
internal sealed class SnapshotFileStream(FileStream source, ByteRate? rate) : Stream
{
    private readonly long _length = source.Length;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _position;
    internal bool Shrunk { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => true; // TarWriter reads the entry size from Length.
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int wanted = (int)Math.Min(buffer.Length, _length - _position);
        if (wanted == 0) return 0;
        int read = Shrunk ? 0 : source.Read(buffer[..wanted]);
        if (read == 0)
        {
            Shrunk = true;
            buffer[..wanted].Clear();
            read = wanted;
        }
        _hash.AppendData(buffer[..read]);
        _position += read;
        rate?.Pace(read);
        return read;
    }
    internal string Sha256() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

// Keeps an online copy to its byte rate, so it does not starve the game's own saves of disk bandwidth.
internal sealed class ByteRate(long bytesPerSecond)
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _bytes;

    internal void Pace(int count)
    {
        _bytes += count;
        long ahead = (long)(_bytes * 1000.0 / bytesPerSecond) - _clock.ElapsedMilliseconds;
        if (ahead > 0) Thread.Sleep(TimeSpan.FromMilliseconds(ahead));
    }
}
