using System.Text.Json;

namespace Quasar.ClusterDeployment;

// World/mod inputs can be shared by clusters. Durable owners also cover transfers
// not yet committed to a deployment; reconciliation releases abandoned transfers.
internal static class ClusterInputOwners
{
    private static readonly object Sync = new();

    internal static void Track(string directory, string hash, string cluster)
    {
        Validate(hash);
        if (string.IsNullOrWhiteSpace(cluster)) throw new InvalidDataException("Input owner is required.");
        lock (Sync)
        {
            string path = OwnerPath(directory, hash);
            var owners = Read(path);
            if (owners.Add(cluster)) Write(path, owners);
        }
    }

    // Caller owns the exclusive storage lease, including all transfer requests.
    internal static async Task RetainAsync(string directory, string cluster, IReadOnlySet<string> retained, CancellationToken token)
    {
        foreach (string hash in retained)
        {
            Validate(hash);
            if (!Directory.Exists(Path.Combine(directory, hash))) throw new InvalidDataException("Retained world/mod input is missing.");
        }
        if (!Directory.Exists(directory)) return;
        using var content = await ClusterContentStore.OpenAsync(directory, token);
        lock (Sync)
        {
            foreach (string hash in retained) Track(directory, hash, cluster);
            string ownersDirectory = Path.Combine(directory, ".owners");
            if (!Directory.Exists(ownersDirectory)) return;
            ClusterWorldFiles.Private(ownersDirectory);
            foreach (string path in Directory.EnumerateFiles(ownersDirectory, "*.json"))
            {
                string hash = Path.GetFileNameWithoutExtension(path);
                Validate(hash);
                var owners = Read(path);
                if (!retained.Contains(hash) && owners.Remove(cluster)) Write(path, owners);
                if (owners.Count != 0) continue;
                string input = Path.Combine(directory, hash);
                if (Directory.Exists(input)) { ClusterWorldFiles.Private(input); Directory.Delete(input, true); }
                File.Delete(path); // Empty owner receipt survives a crash during deletion.
            }
        }
    }

    private static string OwnerPath(string directory, string hash) => Path.Combine(directory, ".owners", hash + ".json");
    private static HashSet<string> Read(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Input owner receipt is a link.");
        return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Invalid input ownership receipt.");
    }
    private static void Write(string path, HashSet<string> owners)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ClusterWorldFiles.Private(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(owners)); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
    private static void Validate(string hash)
    {
        if (hash.Length != 64 || hash != hash.ToLowerInvariant() || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid input reference hash.");
    }
}
