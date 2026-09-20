using System.Xml;
using System.Xml.Linq;

namespace Quasar.ClusterDeployment;

internal static class ClusterPluginBundles
{
    public static void Validate(string root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(folder);
            string assembly = Path.Combine(folder, name + ".dll");
            string metadata = Path.Combine(folder, name + ".xml");
            if (!File.Exists(assembly) || new FileInfo(assembly).Length == 0)
                throw new InvalidDataException($"Missing compiled plugin: {assembly}");
            using var reader = XmlReader.Create(metadata, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var data = XDocument.Load(reader).Root;
            string? id = data?.Element("Id")?.Value;
            string? commit = data?.Element("Commit")?.Value;
            if (data?.Name != "PluginData"
                || data.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value != "GitHubPlugin"
                || string.IsNullOrWhiteSpace(id) || !ids.Add(id)
                || id is "direct-transport" or "cluster-node" or "cluster-wa"
                || commit?.Length != 40 || !commit.All(Uri.IsHexDigit))
                throw new InvalidDataException("Common plugins require unique non-infrastructure IDs and exact source commits.");
            if (data.Element("Runtimes")?.Value != "CoreCLR"
                || (data.Element("Platforms") is { } platform && platform.Value != "Linux"))
                throw new InvalidDataException("Common plugin bundles must support the selected CoreCLR/Linux runtime.");
            foreach (var dependency in data.Element("DependencyIds")?.Elements("Id") ?? [])
                dependencies.Add(dependency.Value);
            foreach (var asset in data.Elements("Asset"))
            {
                string? path = asset.Attribute("Path")?.Value;
                if (asset.Attribute("Url") is not null || string.IsNullOrWhiteSpace(path)
                    || Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Contains("..")
                    || (!File.Exists(Path.Combine(folder, path)) && !Directory.Exists(Path.Combine(folder, path))))
                    throw new InvalidDataException("Plugin assets must already exist inside their local bundle.");
            }
            if (id == "linux-compat")
                foreach (string native in new[] { "libHavok.so", "libRecastDetour.so", "libVRageNative.so",
                             "libsteam_api.so", "libEOSSDK-Linux-Shipping.so" })
                    if (!File.Exists(Path.Combine(folder, native)) || new FileInfo(Path.Combine(folder, native)).Length == 0)
                        throw new InvalidDataException($"Linux compatibility bundle is missing {native}.");
        }
        if (!ids.Contains("dotnet-compat") || !ids.Contains("linux-compat") || Directory.EnumerateFiles(root).Any())
            throw new InvalidDataException("CommonPlugins must contain named dotnet-compat and linux-compat bundles.");
        if (dependencies.Any(id => !ids.Contains(id) && id != "direct-transport"))
            throw new InvalidDataException("A common plugin dependency is absent from the pinned deployment.");
    }
}
