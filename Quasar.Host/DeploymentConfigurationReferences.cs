using System.Text.Json;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

// Only generated configurations registered by preparation are eligible for removal.
// Runtime directories and unregistered operator/recovery files are never swept.
internal static class DeploymentConfigurationReferences
{
    private static readonly object Sync = new();
    internal static void Track(string state, string cluster, string directory)
    {
        lock (Sync)
        {
            string root = Path.Combine(state, "conversions");
            directory = Path.GetFullPath(directory);
            if (!directory.StartsWith(root + "/", StringComparison.Ordinal)) return;
            string path = Path.Combine(root, ".configuration-references.json");
            var references = Read(path);
            if (!references.TryGetValue(cluster, out var paths)) references[cluster] = paths = [];
            if (paths.Add(directory)) Write(path, references);
        }
    }

    internal static void Retain(string state, string cluster, IEnumerable<string> retained, IEnumerable<string> active)
    {
        lock (Sync)
        {
            string root = Path.Combine(state, "conversions");
            var keep = retained.Concat(active).Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
            string path = Path.Combine(root, ".configuration-references.json");
            var references = Read(path);
            if (!references.TryGetValue(cluster, out var paths)) return;
            foreach (string directory in paths.Except(keep).ToArray())
            {
                if (!Path.IsPathFullyQualified(directory) || Path.GetFullPath(directory) != directory
                    || !directory.StartsWith(root + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("Configuration reference escapes managed storage.");
                if (Directory.Exists(directory))
                {
                    ClusterWorldFiles.Private(directory);
                    string manifest = Path.Combine(directory, "bundle.json");
                    var bundle = ExecutionBundle.ReadManifest(manifest, ExecutionBundle.Hash(File.ReadAllBytes(manifest)));
                    if (bundle.ClusterId != cluster || bundle.RuntimeRoot is null
                        || bundle.RuntimeRoot == directory || bundle.RuntimeRoot.StartsWith(directory + "/", StringComparison.Ordinal))
                        throw new InvalidDataException("Configuration ownership does not match its reference.");
                    Directory.Delete(directory, true);
                }
                paths.Remove(directory);
            }
            Write(path, references);
        }
    }

    private static Dictionary<string, HashSet<string>> Read(string path)
    {
        if (!File.Exists(path)) return [];
        ClusterWorldFiles.Private(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Configuration references cannot be a link.");
        return JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Configuration references are unreadable.");
    }
    private static void Write(string path, Dictionary<string, HashSet<string>> value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(value)); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
