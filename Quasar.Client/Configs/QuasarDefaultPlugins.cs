using Quasar.Models;

namespace Quasar.Services;

/// <summary>
/// Server plugins Quasar enables by default, with their default settings and the latest hub commit:
/// Bugfixes and Performance, both on MagnetarHub. Guided cluster setup adds them to the chosen profile.
/// A profile records each default it was offered in <see cref="QuasarConfigProfile.OfferedDefaultPlugins"/>,
/// whether the operator took it or not, so a removed or declined default never comes back.
/// </summary>
public static class QuasarDefaultPlugins
{
    public const string BugfixesPluginId = "bugfixes";
    public const string PerformancePluginId = "454B0F55-C727-4DAB-B1A9-5AE6050A0932";

    private static readonly (string Id, string Name)[] Defaults =
        [(BugfixesPluginId, "Bugfixes"), (PerformancePluginId, "Performance")];

    public static IReadOnlyList<string> PluginIds { get; } = Defaults.Select(plugin => plugin.Id).ToArray();

    /// <summary>The defaults this profile neither selects nor was offered before.</summary>
    public static IReadOnlyList<QuasarPluginSelection> Pending(QuasarConfigProfile profile) =>
        Defaults
            .Where(plugin => !profile.Plugins.Any(selected => Same(selected.PluginId, plugin.Id))
                && !(profile.OfferedDefaultPlugins ?? []).Any(offered => Same(offered, plugin.Id)))
            .Select(plugin => new QuasarPluginSelection { PluginId = plugin.Id, DisplayName = plugin.Name })
            .ToList();

    /// <summary>
    /// Selects the pending defaults when <paramref name="enable"/> is true, and records every default as
    /// offered either way. Returns the plugins it selected. The profile is changed in place, not saved.
    /// </summary>
    public static IReadOnlyList<QuasarPluginSelection> Apply(QuasarConfigProfile profile, bool enable)
    {
        var added = enable ? Pending(profile) : [];
        profile.Plugins.AddRange(added);
        profile.OfferedDefaultPlugins = (profile.OfferedDefaultPlugins ?? [])
            .Concat(PluginIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return added;
    }

    private static bool Same(string? left, string right) => string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);
}
