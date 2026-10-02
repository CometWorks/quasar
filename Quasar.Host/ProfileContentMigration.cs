using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Quasar.Host;

/// <summary>Applies a pinned profile to stopped, preserved worlds. Replaying the same bundle is safe.</summary>
internal static class ProfileContentMigration
{
    internal static void Validate(ExecutionBundle bundle, string manifestPath)
    {
        var profile = bundle.Manifest.ProfileContent;
        if (profile is null)
        {
            if (bundle.Manifest.RuntimeRoot is { } runtime
                && File.Exists(Path.Combine(runtime, ".quasar-profile-migration")))
                throw new InvalidOperationException("This older bundle cannot restore preserved profile settings and mods. Restore the pre-update cluster backup with a newly prepared deployment.");
            return;
        }
        var pins = (bundle.Manifest.ConfigFiles ?? []).ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        if (!pins.ContainsKey(profile.SettingsSeed)) throw new InvalidDataException("Profile settings seed is not pinned.");
        var source = Read(Path.Combine(root, Relative(profile.SettingsSeed)));
        var listed = source.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Mods")?
            .Elements().Where(e => e.Name.LocalName == "ModItem")
            .Select(e => e.Elements().Single(x => x.Name.LocalName == "PublishedFileId").Value).ToArray() ?? [];
        if (source.Root?.Name.LocalName != "MyObjectBuilder_Checkpoint"
            || source.Root.Elements().All(e => e.Name.LocalName != "Settings")
            || !listed.SequenceEqual(profile.ModIds) || listed.Distinct(StringComparer.Ordinal).Count() != listed.Length
            || profile.NodeModSeeds.Count != bundle.Manifest.Nodes.Length
            || bundle.Manifest.Nodes.Any(n => !profile.NodeModSeeds.ContainsKey(n.SlotKey)))
            throw new InvalidDataException("Pinned profile settings, mod list or node inventory is invalid.");
        foreach (var (slot, seed) in profile.NodeModSeeds)
        {
            Relative(seed);
            string prefix = seed + "/";
            var staged = pins.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(path => path[prefix.Length..].Split('/')[0])
                .Where(name => name != ".quasar-profile-mods").Distinct(StringComparer.Ordinal).ToArray();
            var expected = profile.ModIds.Select(id => id + ".sbm").ToArray();
            if (!staged.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))
                || profile.ModIds.Any(id => !ulong.TryParse(id, out ulong value) || value == 0)
                || slot.Length == 0)
                throw new InvalidDataException("Profile mod payload does not match its pinned world list.");
        }
    }

    internal static void Apply(ExecutionBundle bundle, string manifestPath)
    {
        var profile = bundle.Manifest.ProfileContent;
        Validate(bundle, manifestPath);
        if (profile is null) return;
        string runtime = bundle.Manifest.RuntimeRoot ?? throw new InvalidDataException("Profile update needs a runtime root.");
        if (!Path.IsPathFullyQualified(runtime)) throw new InvalidDataException("Profile runtime root must be absolute.");
        if (!Directory.Exists(runtime)) return; // First deployment uses the pinned initial seeds.
        ExecutionBundle.RefuseTree(runtime);
        string config = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var desired = Read(Path.Combine(config, Relative(profile.SettingsSeed)));
        string world = Path.Combine(runtime, "world");
        PatchDirectory(world, desired);
        var pins = (bundle.Manifest.ConfigFiles ?? []).ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        foreach (var node in bundle.Manifest.Nodes)
        {
            string slot = ExecutionBundle.Hash(Encoding.UTF8.GetBytes(node.SlotKey))[..24];
            string data = Path.Combine(runtime, "nodes", slot, "data");
            if (!Directory.Exists(data)) continue;
            if (!File.Exists(Path.Combine(data, ".quasar-seed.json")))
                throw new InvalidDataException("Existing node data lacks managed provenance: " + node.SlotKey);
            PatchDirectory(Path.Combine(data, "premade-world"), desired);
            string worldId = node.Environment.TryGetValue("CLUSTER_WORLD_ID", out var id) ? id : "";
            if (worldId.Length == 0 || worldId != Path.GetFileName(worldId))
                throw new InvalidDataException("Node world identity is invalid.");
            PatchDirectory(Path.Combine(data, "Saves", worldId), desired);
            ApplyMods(Path.Combine(data, "Mods"), config, profile.NodeModSeeds[node.SlotKey],
                profile.ModIds, pins, bundle.Manifest.Revision);
        }
        string marker = Path.Combine(runtime, ".quasar-profile-migration");
        string temporary = marker + ".tmp";
        File.WriteAllText(temporary, bundle.Manifest.Revision);
        File.Move(temporary, marker, true);
    }

    private static void PatchDirectory(string directory, XDocument desired)
    {
        if (!Directory.Exists(directory)) return;
        if (!File.Exists(Path.Combine(directory, "Sandbox.sbc")))
            throw new InvalidDataException("Existing managed world has no checkpoint: " + directory);
        foreach (string name in new[] { "Sandbox.sbc", "Sandbox_config.sbc" })
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            var checkpoint = Read(path);
            foreach (string field in new[] { "SessionName", "Settings", "Mods" })
            {
                var replacement = desired.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == field);
                if (replacement is null) continue;
                var old = checkpoint.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == field);
                if (old is null) checkpoint.Root?.Add(new XElement(replacement));
                else if (field == "Settings")
                {
                    foreach (var value in replacement.Elements())
                    {
                        var existing = old.Elements().FirstOrDefault(e => e.Name.LocalName == value.Name.LocalName);
                        if (existing is null) old.Add(new XElement(value));
                        else existing.ReplaceWith(new XElement(value));
                    }
                }
                else old.ReplaceWith(new XElement(replacement));
            }
            if (XNode.DeepEquals(checkpoint, Read(path))) continue;
            Write(path, checkpoint);
        }
    }

    private static void ApplyMods(string target, string config, string seed, string[] ids,
        Dictionary<string, string> pins, string revision)
    {
        string marker = Path.Combine(target, ".quasar-profile-mods");
        if (File.Exists(marker) && File.ReadAllText(marker) == revision) return;
        string previous = target + ".previous";
        if (!Directory.Exists(target) && Directory.Exists(previous)) Directory.Move(previous, target);
        if (Directory.Exists(target))
        {
            ExecutionBundle.RefuseTree(target);
            if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new InvalidDataException("Existing mod files have no managed provenance: " + target);
        }
        string staging = target + ".staging";
        if (Directory.Exists(staging)) { ExecutionBundle.RefuseTree(staging); Directory.Delete(staging, true); }
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string id in ids)
            {
                string prefix = seed + "/" + id + ".sbm/";
                foreach (var pin in pins.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    string relative = Relative(pin.Key[prefix.Length..]);
                    string source = Path.Combine(config, Relative(pin.Key));
                    string destination = Path.Combine(staging, id + ".sbm", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using (var input = File.OpenRead(source))
                    using (var output = new FileStream(destination, FileMode.CreateNew))
                    { input.CopyTo(output); output.Flush(true); }
                    using var check = File.OpenRead(destination);
                    if (!Convert.ToHexString(SHA256.HashData(check)).Equals(pin.Value, StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("Mod payload changed during migration.");
                }
            }
            File.WriteAllText(Path.Combine(staging, ".quasar-profile-mods"), revision);
            if (Directory.Exists(previous)) { ExecutionBundle.RefuseTree(previous); Directory.Delete(previous, true); }
            if (Directory.Exists(target)) Directory.Move(target, previous);
            Directory.Move(staging, target);
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static XDocument Read(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("World checkpoint exceeds 64 MiB.");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    private static void Write(string path, XDocument document)
    {
        string temporary = path + ".quasar-profile-tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                document.Save(output);
                output.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Relative(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Profile content path escapes its bundle.");
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}
