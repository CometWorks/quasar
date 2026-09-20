using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quasar.Host;

internal sealed record ExecutionBundle(string Root, BundleManifest Manifest, IReadOnlyDictionary<string, string> Files)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    internal static BundleManifest ReadManifest(string path, string expectedHash)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Execution manifest exceeds 64 MiB");
        byte[] bytes = File.ReadAllBytes(path);
        if (!Hash(bytes).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Execution manifest failed SHA-256 verification");
        var manifest = JsonSerializer.Deserialize<BundleManifest>(bytes, Json)
            ?? throw new InvalidDataException("Execution manifest is empty");
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Revision)
            || manifest.Files is null || manifest.Nodes is null
            || manifest.Files.Length + (manifest.ConfigFiles?.Length ?? 0) > 200_000)
            throw new InvalidDataException("Execution manifest is invalid");
        return manifest;
    }

    internal static ExecutionBundle Load(string path, string expectedHash)
    {
        var manifest = ReadManifest(path, expectedHash);
        string configRoot = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string root = manifest.ArtifactRoot ?? configRoot;
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Artifact root must be absolute");
        Dictionary<string, string> Verify(string directory, BundleFile[] files)
        {
            var pins = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (Path.IsPathRooted(file.Path) || file.Path.Contains('\\')
                    || file.Path.Split('/').Any(part => part is "" or "." or ".."))
                    throw new InvalidDataException("Execution file path escapes its root");
                string full = Path.Combine(directory, file.Path);
                for (string? parent = full; parent is not null; parent = Path.GetDirectoryName(parent))
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Execution files must not contain symbolic links");
                using var stream = File.OpenRead(full);
                string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException($"Execution file '{file.Path}' failed SHA-256 verification");
                if (!pins.TryAdd(file.Path, hash)) throw new InvalidDataException("Execution file is duplicated");
            }
            return pins;
        }
        var pins = Verify(root, manifest.Files);
        Verify(configRoot, manifest.ConfigFiles ?? []);
        return new(root, manifest, pins) { ConfigRoot = configRoot };
    }

    // Only the stopped slot's private directory is refreshed. Plugin-owned extra files survive
    // deployment changes; files previously supplied by the release are replaced or removed.
    internal static void PrepareNodeConfiguration(string manifestPath, BundleManifest manifest, NodeSpawnSpec spec,
        string runDirectory, string clusterId)
    {
        if (spec.ConfigurationSeed is not { } source) return;
        ValidateRelative(source);
        string target = Path.Combine(runDirectory, "config"), previous = target + ".previous";
        string staging = target + ".staging";
        const string marker = ".quasar-config.json";
        if (!Directory.Exists(target) && Directory.Exists(previous)) Directory.Move(previous, target);
        // A complete target is committed; a retained previous directory is pre-commit recovery data.
        if (Directory.Exists(previous)) { RefuseTree(previous); Directory.Delete(previous, true); }
        if (Directory.Exists(staging)) { RefuseTree(staging); Directory.Delete(staging, true); }
        RefuseLinks(runDirectory);
        var pins = (manifest.ConfigFiles ?? []).Where(f => f.Path.StartsWith(source + "/", StringComparison.Ordinal))
            .ToDictionary(f => f.Path[(source.Length + 1)..], f => f.Sha256, StringComparer.Ordinal);
        if (pins.Count == 0 || pins.ContainsKey(marker)) throw new InvalidDataException("Missing or invalid configuration seed.");
        Directory.CreateDirectory(staging);
        try
        {
            if (Directory.Exists(target))
            {
                var old = JsonSerializer.Deserialize<ConfigurationMarker>(File.ReadAllBytes(Path.Combine(target, marker)), Json)
                    ?? throw new InvalidDataException("Configuration provenance is missing.");
                if (old.ClusterId != clusterId || old.SlotKey != spec.SlotKey)
                    throw new InvalidDataException("Configuration belongs to another cluster or slot.");
                RefuseTree(target);
                foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(target, directory)));
                foreach (var file in RefuseTree(target))
                {
                    string relative = Path.GetRelativePath(target, file);
                    if (relative == marker || old.Files.Contains(relative) || pins.ContainsKey(relative)) continue;
                    Copy(file, Path.Combine(staging, relative));
                }
            }
            string seed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, source);
            foreach (var (relative, hash) in pins)
            {
                ValidateRelative(relative);
                string file = Path.Combine(seed, relative);
                RefuseLinks(file);
                string copy = Path.Combine(staging, relative);
                Copy(file, copy);
                using var input = File.OpenRead(copy);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Configuration seed changed during copy.");
            }
            using (var file = new FileStream(Path.Combine(staging, marker), FileMode.CreateNew))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(new ConfigurationMarker(clusterId, spec.SlotKey, pins.Keys.ToArray()), Json)); file.Flush(true); }
            if (Directory.Exists(target) && !OperatingSystem.IsWindows())
            {
                foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                    File.SetUnixFileMode(Path.Combine(staging, Path.GetRelativePath(target, directory)), File.GetUnixFileMode(directory) & (UnixFileMode)511);
                File.SetUnixFileMode(staging, File.GetUnixFileMode(target) & (UnixFileMode)511);
            }
            if (Directory.Exists(target)) Directory.Move(target, previous);
            Directory.Move(staging, target);
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }

        static void Copy(string sourceFile, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = File.OpenRead(sourceFile);
            using var output = new FileStream(destination, FileMode.CreateNew);
            input.CopyTo(output); output.Flush(true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(sourceFile) & (UnixFileMode)511);
        }
    }

    private sealed record ConfigurationMarker(string ClusterId, string SlotKey, string[] Files);
    internal static string[] RefuseTree(string directory)
    {
        RefuseLinks(directory);
        var files = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            RefuseLinks(path);
            if (Directory.Exists(path)) files.AddRange(RefuseTree(path));
            else files.Add(path);
        }
        return files.ToArray();
    }

    internal static void RequireCompatibleStorage(Dictionary<string, string>? candidate, Dictionary<string, string>? data)
    {
        foreach (string key in new[] { "world", "registry", "plugins" })
            if (candidate is null || data is null || !candidate.TryGetValue(key, out string? format)
                || string.IsNullOrWhiteSpace(format) || !data.TryGetValue(key, out string? existing) || format != existing)
                throw new InvalidDataException("Storage format is missing or incompatible: " + key + ". Explicit migration is required.");
    }

    internal void ConfirmRestoreActivation()
    {
        if (Manifest.RuntimeRoot is not { } runtime) return;
        string path = Path.Combine(runtime, ".quasar-restore.json");
        if (!File.Exists(path)) return;
        var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!;
        if (marker["activated"]!.GetValue<bool>()) return;
        if (marker["revision"]!.GetValue<string>() != Manifest.Revision)
            throw new InvalidOperationException("Restored data requires its prepared deployment revision.");
        marker["activated"] = true;
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(JsonSerializer.SerializeToUtf8Bytes(marker, Json)); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }

    internal void InitializeData(string clusterId)
    {
        if (Manifest.RuntimeRoot is { } run && File.Exists(Path.Combine(run, ".quasar-restore.json")))
        {
            using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run, ".quasar-restore.json")));
            if (!marker.RootElement.GetProperty("activated").GetBoolean())
                throw new InvalidOperationException("Restored data cannot start before deployment activation completes.");
        }
        if (Manifest.InitialDirectories is not { Count: > 0 } seeds) return;
        string runtime = Manifest.RuntimeRoot ?? throw new InvalidDataException("Seeded deployments require a runtime root.");
        if (!Path.IsPathFullyQualified(runtime)) throw new InvalidDataException("Runtime root must be absolute.");
        string config = ConfigRoot ?? throw new InvalidDataException("Seeded deployments require a configuration root.");
        var pins = (Manifest.ConfigFiles ?? []).ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        foreach (var (destination, source) in seeds)
        {
            ValidateRelative(destination);
            ValidateRelative(source);
            string target = Path.Combine(runtime, destination);
            string marker = Path.Combine(target, ".quasar-seed.json");
            var identity = new { clusterId, destination };
            byte[] identityBytes = JsonSerializer.SerializeToUtf8Bytes(identity, Json);
            if (Directory.Exists(target))
            {
                RefuseLinks(target);
                if (!File.Exists(marker) || !File.ReadAllBytes(marker).SequenceEqual(identityBytes))
                    throw new InvalidDataException("Existing runtime data has no matching managed provenance: " + destination);
                continue; // Mutable data belongs to the running world, never to a later deployment's seed.
            }
            string parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            RefuseLinks(parent);
            string staging = Path.Combine(parent, ".seed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                foreach (var pin in pins.Where(pin => pin.Key.StartsWith(source + "/", StringComparison.Ordinal)))
                {
                    string relative = pin.Key[(source.Length + 1)..];
                    string copy = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    using (var input = File.OpenRead(Path.Combine(config, pin.Key)))
                    using (var output = new FileStream(copy, FileMode.CreateNew))
                    { input.CopyTo(output); output.Flush(true); }
                    using var stream = File.OpenRead(copy);
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(pin.Value, StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("Data seed changed during preparation.");
                }
                if (!Directory.EnumerateFileSystemEntries(staging).Any()) throw new InvalidDataException("Data seed is empty.");
                using (var stream = new FileStream(Path.Combine(staging, ".quasar-seed.json"), FileMode.CreateNew))
                { stream.Write(identityBytes); stream.Flush(true); }
                Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }

    private string? ConfigRoot { get; init; }
    private static void ValidateRelative(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Execution path escapes its root.");
    }
    private static void RefuseLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime paths must not contain symbolic links.");
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static void ApplySecrets(System.Diagnostics.ProcessStartInfo start, Dictionary<string, string>? references)
    {
        // Enrollment and other clusters' credentials belong to the Host, never its child processes.
        foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("QSR_MANAGED_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        foreach (var (target, source) in references ?? [])
        {
            string? value = Environment.GetEnvironmentVariable(source);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Required credential environment variable '{source}' is not set");
            start.Environment[target] = value;
        }
    }
}
