namespace Quasar.Plugin.Abstractions.Extensions;

public sealed record QuasarExtensionContribution(
    string TargetKey,
    Type ComponentType,
    QuasarPatchMode Mode,
    int Priority,
    string PluginId,
    string? Policy);
