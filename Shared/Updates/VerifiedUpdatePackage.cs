using System.Security.Cryptography;
using System.Text.Json;

namespace Quasar.Updates;

// Archive checksums remain the provenance check. The seal also detects damaged
// or partly extracted staging trees when another process later activates them.
internal static class VerifiedUpdatePackage
{
    private const string SealName = ".verified-files.json";
    internal static async Task SealAsync(string directory, CancellationToken token)
    {
        var hashes = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == SealName) continue;
            if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Package contains a symbolic link.");
            await using var file = File.OpenRead(path);
            hashes[Path.GetRelativePath(directory, path)] = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        }
        QuasarUpdateJournal.WriteJson(Path.Combine(directory, SealName), hashes);
    }
    internal static async Task VerifyAsync(string directory, CancellationToken token)
    {
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(directory, SealName), token))
            ?? throw new InvalidDataException("Invalid package verification seal.");
        foreach (var (name, expected) in hashes)
        {
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Package seal escapes staging directory.");
            await using var file = File.OpenRead(path);
            if (!string.Equals(expected, Convert.ToHexString(await SHA256.HashDataAsync(file, token)), StringComparison.Ordinal))
                throw new InvalidDataException($"Staged package file changed: {name}. Retry the update to download again.");
        }
        if (!hashes.ContainsKey(LauncherReplacement.ManifestName)) throw new InvalidDataException("Package has no verified launcher manifest.");
    }
}
