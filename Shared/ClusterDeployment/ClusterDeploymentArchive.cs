using System.Formats.Tar;
using System.Text.Json;
using Quasar.Host.Contract.V1;

namespace Quasar.ClusterDeployment;

internal static partial class ClusterDeploymentFiles
{
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
        string directory, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cluster deployments require Linux.");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid input hash.");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        RefuseLinks(directory);
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
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | (pin.Executable ? UnixFileMode.UserExecute : 0));
            }
            if (seen.Count != inputs.Files.Count) throw new InvalidDataException("Transfer is incomplete.");
            await VerifyAsync(inputs, Path.Combine(staging, "Package"), Path.Combine(staging, "Dependencies"), token);
            await File.WriteAllBytesAsync(Path.Combine(staging, "inputs.json"), bytes, token);
            await File.WriteAllBytesAsync(Path.Combine(staging, "launch-environment.json"),
                JsonSerializer.SerializeToUtf8Bytes(EnvironmentFor(destination), JsonOptions), token);
            if (!Directory.Exists(destination)) Directory.Move(staging, destination);
            return await PrepareAsync(bytes, expectedHash, directory, token);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
