using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Host;

/// <summary>
/// The World Authority's mod Storage is canonical: only its copy is kept cluster-wide, and read-only mod
/// configuration must be identical on every node (#1281). Before a node of a managed cluster starts, its mod
/// Storage folders are replaced with the World Authority's copy, so a node's own writes never reach its next start.
/// </summary>
/// <remarks>
/// A managed node starts every time from data/premade-world, so mods read their world Storage there while the
/// world loads, and from data/Saves/&lt;world&gt; after the first save; the game never copies Storage between them.
/// The World Authority's world tier is therefore its premade copy with its saved copy's files on top, and that
/// becomes both folders of the starting node, the World Authority's own included. The local and global tiers
/// (data/Storage) of a regular node become the World Authority's. A Host without the World Authority keeps
/// the prepared seed.
/// </remarks>
internal static class ModStorageSeed
{
    internal static void Apply(BundleManifest manifest, NodeSpawnSpec spec)
    {
        if (manifest.ProfileContent is null || manifest.RuntimeRoot is not { } runtime) return;
        var authority = manifest.Nodes.SingleOrDefault(node => node.Role == Admin.NodeRole.WorldAuthority);
        if (authority is null) return;
        string world = spec.Environment.TryGetValue("CLUSTER_WORLD_ID", out var id) ? id : "";
        if (world.Length == 0 || world != Path.GetFileName(world))
            throw new InvalidDataException("Node world identity is invalid.");
        string source = DataRoot(runtime, authority.SlotKey), target = DataRoot(runtime, spec.SlotKey);
        if (!Directory.Exists(source) || !Directory.Exists(target)) return;

        string canonical = Path.Combine(target, ".quasar-mod-storage");
        if (Directory.Exists(canonical)) Directory.Delete(canonical, true);
        try
        {
            CopyTree(Path.Combine(source, "premade-world", "Storage"), canonical);
            CopyTree(Path.Combine(source, "Saves", world, "Storage"), canonical);
            Replace(Path.Combine(target, "premade-world", "Storage"), canonical);
            if (Directory.Exists(Path.Combine(target, "Saves", world)))
                Replace(Path.Combine(target, "Saves", world, "Storage"), canonical);
        }
        finally { if (Directory.Exists(canonical)) Directory.Delete(canonical, true); }
        if (spec.Role != Admin.NodeRole.WorldAuthority)
            Replace(Path.Combine(target, "Storage"), Path.Combine(source, "Storage"));
    }

    private static string DataRoot(string runtime, string slotKey) => Path.Combine(runtime, "nodes",
        ExecutionBundle.Hash(System.Text.Encoding.UTF8.GetBytes(slotKey))[..24], "data");

    /// <summary>Makes <paramref name="target"/> a copy of <paramref name="source"/>; no files leave no folder.</summary>
    private static void Replace(string target, string source)
    {
        string staging = target + ".quasar-seeding", previous = target + ".quasar-previous";
        foreach (string existing in new[] { staging, previous, target })
            if (Directory.Exists(existing)) ExecutionBundle.RefuseTree(existing);
        foreach (string leftover in new[] { staging, previous })
            if (Directory.Exists(leftover)) Directory.Delete(leftover, true);
        CopyTree(source, staging);
        if (Directory.Exists(target)) Directory.Move(target, previous);
        if (Directory.Exists(staging)) Directory.Move(staging, target);
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
    }

    /// <summary>Copies every file under <paramref name="source"/>, overwriting; a folder without files is skipped.</summary>
    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (string file in ExecutionBundle.RefuseTree(source))
        {
            string copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, true);
        }
    }
}
