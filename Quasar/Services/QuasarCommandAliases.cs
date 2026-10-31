using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quasar.ClusterDeployment;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>Profile validation and transport for Magnetar's dispatcher-owned command aliases.</summary>
internal static class QuasarCommandAliases
{
    internal const string ConfigType = "Quasar.Agent.CommandAliasConfiguration";
    internal const string FileName = "CommandAliases.json";

    internal static void Validate(IReadOnlyList<QuasarCommandAlias>? aliases)
    {
        if (aliases is null) return;
        if (aliases.Count > 64) throw new ArgumentException("A profile supports at most 64 command aliases.");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            if (alias is null || !ValidPath(alias.Source) || !ValidPath(alias.Target))
                throw new ArgumentException("Alias paths must start with ! and contain command words made of letters, digits, underscores or hyphens (maximum 512 characters).");
            string source = Normalize(alias.Source), target = Normalize(alias.Target);
            if (!sources.Add(source) || source.Split(' ')[0].Equals("!help", StringComparison.OrdinalIgnoreCase)
                || source.Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Aliases must be unique, cannot alias themselves or replace !help.");
        }
    }

    private static bool ValidPath(string? path) => path is { Length: <= 512 }
        && Regex.IsMatch(path, @"\A![a-zA-Z0-9_-]+(?:[ \t]+[a-zA-Z0-9_-]+)*\z");
    private static string Normalize(string path) => string.Join(' ', path.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    internal static Dictionary<string, string> Values(QuasarConfigProfile profile)
    {
        Validate(profile.CommandAliases);
        return (profile.CommandAliases ?? []).ToDictionary(a => Normalize(a.Source), a => Normalize(a.Target), StringComparer.OrdinalIgnoreCase);
    }

    internal static string Serialize(QuasarConfigProfile profile) => JsonSerializer.Serialize(new { aliases = Values(profile) });

    internal static void RequireRuntime(QuasarConfigProfile profile, string? executable)
    {
        Validate(profile.CommandAliases);
        if (profile.CommandAliases is not { Count: > 0 }) return;
        string root = Path.GetDirectoryName(executable ?? "") ?? "";
        string launcher = Path.GetFileNameWithoutExtension(executable ?? "").Equals("MagnetarLegacy", StringComparison.OrdinalIgnoreCase)
            ? "MagnetarLegacy" : "MagnetarInterim";
        string[] candidates = [Path.Combine(root, "Libraries", launcher, "PluginSdk.dll"),
            Path.Combine(root, "PluginSdk.dll"), Path.Combine(root, "Bin/PluginSdk.dll")];
        var sdk = string.IsNullOrEmpty(root) ? null : candidates.FirstOrDefault(File.Exists);
        if (sdk is null || PluginSdkContract.Read(sdk) is not { Major: 1, Minor: >= 1 })
            throw new InvalidOperationException("Command aliases require Magnetar with PluginSdk contract 1.1 or newer in 1.x. Update Magnetar before starting or staging this profile.");
    }

    internal static void ApplyToCanonical(JsonObject canonical, QuasarConfigProfile profile)
    {
        var aliases = Values(profile);
        foreach (var plugin in canonical.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var configs = plugin["configurations"] is JsonArray multiple ? multiple.OfType<JsonObject>() : [plugin];
            foreach (var config in configs)
            {
                if (config["configType"]?.GetValue<string>() != ConfigType) continue;
                config["configuration"]!["values"]!["aliases"] = JsonSerializer.SerializeToNode(aliases);
                return;
            }
        }
        if (aliases.Count != 0)
            throw new InvalidOperationException("The prepared Quasar.Agent has no command alias configuration. Update the Agent and stage plugins again before using aliases.");
    }
}
