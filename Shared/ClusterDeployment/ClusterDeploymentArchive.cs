using System.Formats.Tar;
using System.Text.Json;
using Quasar.Host.Contract.V1;

namespace Quasar.ClusterDeployment;

internal static partial class ClusterDeploymentFiles
{
    internal static async Task<int> CollectInstallationsAsync(string directory, string clusterId,
        IReadOnlySet<string> retained, IEnumerable<string> activeRoots, CancellationToken token)
    {
        foreach (string hash in retained)
            if (hash.Length != 64 || hash != hash.ToLowerInvariant() || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid retained installation hash.");
        if (!Directory.Exists(directory)) return 0;
        var protectedRoots = activeRoots.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        var installations = new List<(string Path, string Hash, ClusterDeploymentInputs Inputs)>();
        foreach (string path in Directory.EnumerateDirectories(directory))
        {
            string hash = Path.GetFileName(path);
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
            var inputs = await ReadInstalledInputsAsync(directory, hash, token);
            if (inputs.ClusterId == clusterId) installations.Add((path, hash, inputs));
        }
        // Missing/foreign retained metadata is not permission to remove other inputs.
        if (retained.Any(hash => !installations.Any(i => i.Hash == hash)))
            throw new InvalidDataException("A retained deployment installation is missing or belongs to another cluster.");
        using var content = await ClusterContentStore.OpenAsync(directory, token);
        int removed = 0;
        foreach (var (path, hash, inputs) in installations)
        {
            if (!retained.Contains(hash) && !protectedRoots.Any(root => root == path || root.StartsWith(path + "/", StringComparison.Ordinal)))
            {
                Directory.Delete(path, true);
                removed++;
            }
            else
                foreach (var (relative, pin) in inputs.Files)
                {
                    string file = Resolve(path, relative);
                    if (ClusterContentStore.ReferenceCount(file) == 1)
                        await content.AdoptAsync(file, pin, token);
                }
        }
        return removed;
    }

    internal static async Task<ClusterDeploymentInputs> ReadInstalledInputsAsync(string directory, string hash,
        CancellationToken token)
    {
        if (hash.Length != 64 || hash != hash.ToLowerInvariant() || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid installation hash.");
        string installation = Resolve(directory, hash);
        return ReadInputs(await File.ReadAllBytesAsync(Resolve(installation, "inputs.json"), token), hash);
    }

    // Transfer only differences from an authenticated Host's pinned inventory. The Host
    // verifies every reused and received file before publishing the new installation.
    internal static async Task ExportInputsAsync(byte[] bytes, string hash, Stream output, CancellationToken token,
        Dictionary<string, DeploymentFile>? previous = null, IProgress<string>? progress = null)
    {
        var inputs = ReadInputs(bytes, hash);
        var changed = inputs.Files.Where(p => previous is null || !previous.TryGetValue(p.Key, out var old) || old != p.Value)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        progress?.Report($"Reusing {inputs.Files.Count - changed.Length:N0} unchanged files; packaging {changed.Length:N0} changed files ({changed.Sum(p => p.Value.Bytes) / 1048576d:N1} MiB)...");
        using var writer = new TarWriter(output, leaveOpen: true);
        using var metadata = new MemoryStream(bytes);
        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "inputs.json") { DataStream = metadata }, token);
        int completed = 0;
        foreach (var (path, pin) in changed)
        {
            bool package = path.StartsWith("Package/", StringComparison.Ordinal);
            string source = Resolve(package ? inputs.PackageDirectory : inputs.DependencyDirectory,
                path[(package ? "Package/".Length : "Dependencies/".Length)..]);
            using var file = File.OpenRead(source);
            if (file.Length != pin.Bytes) throw new InvalidDataException("Deployment source changed during export.");
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = file }, token);
            if (++completed % 100 == 0 || completed == changed.Length)
                progress?.Report($"Packaged {completed:N0}/{changed.Length:N0} changed files; {inputs.Files.Count - changed.Length:N0} files reused.");
        }
    }

    // Archive carries pinned files only. Absolute source paths are provenance, never extraction targets.
    internal static async Task ExportAsync(string installation, Stream output, CancellationToken token)
    {
        installation = Path.GetFullPath(installation);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(installation, "inputs.json"), token);
        if (Path.GetFileName(installation) != Hash(bytes)) throw new InvalidDataException("Installation directory hash mismatch.");
        await PrepareAsync(bytes, Hash(bytes), Path.GetDirectoryName(installation)!, token);
        var inputs = JsonSerializer.Deserialize<ClusterDeploymentInputs>(bytes, JsonOptions)!;
        using var writer = new TarWriter(output, leaveOpen: true);
        using var metadata = new MemoryStream(bytes);
        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "inputs.json") { DataStream = metadata }, token);
        foreach (var path in inputs.Files.Keys.Order(StringComparer.Ordinal))
        {
            using var file = File.OpenRead(Resolve(installation, path));
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = file }, token);
        }
    }

    internal static async Task<PreparedClusterDeployment> ImportAsync(Stream input, string expectedHash,
        string directory, CancellationToken token, string? baseInputsHash = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cluster deployments require Linux.");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid input hash.");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        RefuseLinks(directory);
        using var content = await ClusterContentStore.OpenAsync(directory, token);
        string staging = Path.Combine(directory, ".transfer-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, expectedHash);
        Directory.CreateDirectory(staging);
        File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var reader = new TarReader(input, leaveOpen: true);
            var first = await reader.GetNextEntryAsync(cancellationToken: token);
            if (first?.EntryType != TarEntryType.RegularFile || first.Name != "inputs.json"
                || first.Length is <= 0 or > 64 * 1024 * 1024 || first.DataStream is null)
                throw new InvalidDataException("Transfer must start with bounded deployment inputs.");
            using var metadata = new MemoryStream();
            await first.DataStream.CopyToAsync(metadata, token);
            byte[] bytes = metadata.ToArray();
            var inputs = ReadInputs(bytes, expectedHash);
            var previous = baseInputsHash is null ? null : await ReadInstalledInputsAsync(directory, baseInputsHash, token);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                ValidateRelative(entry.Name);
                if (entry.EntryType != TarEntryType.RegularFile || (entry.Length != 0 && entry.DataStream is null)
                    || !inputs.Files.TryGetValue(entry.Name, out var pin) || entry.Length != pin.Bytes
                    || !seen.Add(entry.Name) || !(entry.Name.StartsWith("Package/") || entry.Name.StartsWith("Dependencies/")))
                    throw new InvalidDataException("Unexpected, duplicate or invalid transfer entry.");
                string path = Path.Combine(staging, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(file, token); file.Flush(true); }
                await content.AdoptAsync(path, pin, token);
            }
            foreach (var (name, pin) in inputs.Files.Where(p => !seen.Contains(p.Key)))
            {
                token.ThrowIfCancellationRequested();
                if (previous is null || !previous.Files.TryGetValue(name, out var old) || old != pin)
                    throw new InvalidDataException("Transfer is incomplete or has mismatched reuse pins.");
                string source = Resolve(Path.Combine(directory, baseInputsHash!), name);
                string target = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await content.ReferenceAsync(source, target, pin, token);
            }
            await VerifyAsync(inputs, Path.Combine(staging, "Package"), Path.Combine(staging, "Dependencies"), token);
            await File.WriteAllBytesAsync(Path.Combine(staging, "inputs.json"), bytes, token);
            await File.WriteAllBytesAsync(Path.Combine(staging, "launch-environment.json"),
                JsonSerializer.SerializeToUtf8Bytes(EnvironmentFor(destination), JsonOptions), token);
            if (Directory.Exists(destination)) return await PrepareAsync(bytes, expectedHash, directory, token);
            Directory.Move(staging, destination);
            return new(destination, expectedHash, Path.Combine(destination, "launch-environment.json"));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
