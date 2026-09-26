using System.Formats.Tar;
using System.Text.Json;
using Quasar.Host.Contract.V1;

namespace Quasar.ClusterDeployment;

// World copies are immutable conversion inputs. Never extract into an existing runtime.
internal static class ClusterWorldFiles
{
    internal static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        var pins = await ClusterDeploymentFiles.InspectAsync(source, token);
        if (Directory.Exists(destination))
        {
            await VerifyAsync(destination, pins, token);
            return;
        }
        string staging = destination + ".copy-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        Private(staging);
        try
        {
            foreach (string name in pins.Keys)
            {
                token.ThrowIfCancellationRequested();
                string target = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(source, name), target);
            }
            await VerifyAsync(staging, pins, token);
            await VerifyAsync(source, pins, token);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal static async Task VerifyAsync(string root, Dictionary<string, DeploymentFile> pins, CancellationToken token)
    {
        var actual = await ClusterDeploymentFiles.InspectAsync(root, token);
        if (actual.Count != pins.Count || actual.Any(p => !pins.TryGetValue(p.Key, out var expected)
            || p.Value.Sha256 != expected.Sha256 || p.Value.Bytes != expected.Bytes))
            throw new InvalidDataException("World inventory or contents changed.");
    }

    internal static async Task<string> PackAsync(string root, string archive, CancellationToken token)
    {
        var pins = await ClusterDeploymentFiles.InspectAsync(root, token);
        if (!pins.ContainsKey("Sandbox.sbc")) throw new InvalidDataException("World has no Sandbox.sbc.");
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(pins, ClusterDeploymentFiles.JsonOptions);
        using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            using var header = new MemoryStream(metadata);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "world-files.json") { DataStream = header }, token);
            foreach (string name in pins.Keys.Order(StringComparer.Ordinal))
            {
                using var file = File.OpenRead(Path.Combine(root, name));
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = file }, token);
            }
        }
        output.Flush(true);
        await VerifyAsync(root, pins, token);
        return ClusterDeploymentFiles.Hash(metadata);
    }

    internal static async Task<string> UnpackAsync(Stream input, string hash, string directory, CancellationToken token)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid world hash.");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, hash);
        string staging = Path.Combine(directory, ".world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging); Private(staging);
        try
        {
            using var reader = new TarReader(input, leaveOpen: true);
            var header = await reader.GetNextEntryAsync(cancellationToken: token);
            if (header?.EntryType != TarEntryType.RegularFile || header.Name != "world-files.json"
                || header.Length is <= 0 or > 64 * 1024 * 1024 || header.DataStream is null)
                throw new InvalidDataException("World transfer requires a bounded inventory.");
            using var bytes = new MemoryStream();
            await header.DataStream.CopyToAsync(bytes, token);
            if (ClusterDeploymentFiles.Hash(bytes.ToArray()) != hash) throw new InvalidDataException("World manifest hash mismatch.");
            var pins = JsonSerializer.Deserialize<Dictionary<string, DeploymentFile>>(bytes.ToArray(), ClusterDeploymentFiles.JsonOptions)!;
            if (pins is null || !pins.ContainsKey("Sandbox.sbc") || pins.Count > 200_000
                || pins.Values.Any(p => p is null || p.Bytes < 0 || p.Sha256 is null || p.Sha256.Length != 64 || !p.Sha256.All(Uri.IsHexDigit))
                || pins.Values.Sum(p => (decimal)p.Bytes) > 100m * 1024 * 1024 * 1024)
                throw new InvalidDataException("Invalid world inventory.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                Relative(entry.Name);
                if (entry.EntryType != TarEntryType.RegularFile || !pins.TryGetValue(entry.Name, out var pin)
                    || entry.Length != pin.Bytes || !seen.Add(entry.Name)) throw new InvalidDataException("Unexpected world entry.");
                string file = Path.Combine(staging, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await using var output = new FileStream(file, FileMode.CreateNew);
                if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token);
            }
            if (seen.Count != pins.Count) throw new InvalidDataException("World transfer is incomplete.");
            await VerifyAsync(staging, pins, token);
            if (!Directory.Exists(destination)) Directory.Move(staging, destination);
            await VerifyAsync(destination, pins, token);
            return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal static void Relative(string name)
    {
        if (Path.IsPathRooted(name) || name.Contains('\\') || name.Contains(':')
            || name.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException("Invalid archive path.");
    }
    internal static void Private(string path)
    {
        for (string? parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Conversion paths cannot contain links.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
