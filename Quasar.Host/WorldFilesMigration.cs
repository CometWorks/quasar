using System.Security.Cryptography;
using System.Text;

namespace Quasar.Host;

/// <summary>
/// Copies the world seed's <c>Storage/</c> files that differ from the installed bundle's into the stopped, preserved
/// worlds: the Gateway's world and every node's premade and saved world. Quasar stages mod configuration such as
/// MES's as a new seed revision (#1313). Storage files the seed did not change stay as the mods left them, so an
/// update that keeps the seed changes nothing. Replaying the same bundle is safe.
/// </summary>
internal static class WorldFilesMigration
{
    internal static string[] Changed(BundleManifest candidate, BundleManifest? installed)
    {
        if (installed is null) return []; // First deployment: the nodes start from the candidate's seeds.
        var before = StorageFiles(installed);
        return StorageFiles(candidate).Where(file => !before.TryGetValue(file.Key, out var old)
                || !old.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
    }

    internal static void Apply(ExecutionBundle bundle, string manifestPath, BundleManifest? installed)
    {
        string[] changed = Changed(bundle.Manifest, installed);
        if (changed.Length == 0) return;
        string runtime = bundle.Manifest.RuntimeRoot ?? throw new InvalidDataException("World file update needs a runtime root.");
        if (!Path.IsPathFullyQualified(runtime)) throw new InvalidDataException("World file runtime root must be absolute.");
        if (!Directory.Exists(runtime)) return;
        ExecutionBundle.RefuseTree(runtime);
        string seed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
            Relative(bundle.Manifest.InitialDirectories!["world"]));
        var pins = StorageFiles(bundle.Manifest);
        var worlds = new List<string> { Path.Combine(runtime, "world") };
        foreach (var node in bundle.Manifest.Nodes)
        {
            string data = Path.Combine(runtime, "nodes", ExecutionBundle.Hash(Encoding.UTF8.GetBytes(node.SlotKey))[..24], "data");
            if (!Directory.Exists(data)) continue;
            string worldId = node.Environment.TryGetValue("CLUSTER_WORLD_ID", out var id) ? id : "";
            if (worldId.Length == 0 || worldId != Path.GetFileName(worldId))
                throw new InvalidDataException("Node world identity is invalid.");
            worlds.Add(Path.Combine(data, "premade-world"));
            worlds.Add(Path.Combine(data, "Saves", worldId));
        }
        foreach (string world in worlds.Where(w => File.Exists(Path.Combine(w, "Sandbox.sbc"))))
            foreach (string name in changed)
                Copy(Path.Combine(seed, Relative(name)), Path.Combine(world, Relative(name)), pins[name]);
    }

    private static Dictionary<string, string> StorageFiles(BundleManifest manifest)
    {
        if (manifest.InitialDirectories?.GetValueOrDefault("world") is not { } seed) return [];
        string prefix = seed + "/";
        return (manifest.ConfigFiles ?? []).Where(file => file.Path.StartsWith(prefix + "Storage/", StringComparison.Ordinal))
            .ToDictionary(file => file.Path[prefix.Length..], file => file.Sha256, StringComparer.Ordinal);
    }

    private static void Copy(string source, string destination, string sha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".quasar-world-file-tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            using (var check = File.OpenRead(temporary))
                if (!Convert.ToHexString(SHA256.HashData(check)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("World file changed during migration: " + source);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Relative(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("World file path escapes its bundle.");
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}
