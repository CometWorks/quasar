using Quasar.Services.Plugins;

namespace Quasar.Client.UiPlugins;

public sealed record UiPluginDashboard(bool SafeMode, string MarkerPath, bool MarkerExists, IReadOnlyList<string> SafeModeReasons,
    IReadOnlyList<string> LoadErrors, List<PluginDetails> LoadedPlugins, List<NavDetails> NavItems,
    List<ExtensionDetails> Extensions, List<AssemblyDetails> RazorAssemblies, List<QuasarUiPluginHubEntry> Entries,
    Dictionary<string, QuasarUiPluginInstallState> InstallStates, DateTimeOffset? LastRefreshUtc, string LastError,
    bool AutoUpdateUiPlugins, bool RestartRequired, bool CanRestart, string RestartReason, Guid? UpdateId, string? UpdateVersion,
    QuasarDotNetSdkStatus Sdk, QuasarDotNetSdkInstallAvailability Availability, bool SdkInstalling, bool Windows, UiPluginInstallJob? LastInstall);

public sealed record UiPluginInstallJob(Guid Id, string Target, QuasarDotNetSdkInstallResult? Result);

public sealed record NavDetails(string Text, string Href, string Zone, int Order, string? Policy);
public sealed record ExtensionDetails(string TargetKey, string Mode, string Component, int Priority, string PluginId, string? Policy);
public sealed record AssemblyDetails(string? Name, string Location);
public sealed record FileDetails(string? Status, long Count, DateTime? Modified);
public sealed record PluginDetails(string Id, string DisplayName, string Version, string EntryAssembly, string? EntryType,
    IReadOnlyList<string> Stylesheets, string EntryAssemblyPath, string LoadedAssemblyPath, string ShadowCopyDirectory,
    string ShadowCopyHash, string? StaticAssetsDirectory, string? SourceStaticAssetsDirectory, string PluginDirectory,
    string BuildOutput, string RequestPath, bool BuildChanged, bool AssetsChanged, Dictionary<string, FileDetails> Paths);

public sealed record EndpointAccess(string PluginId, string OperationId, IReadOnlyList<string> DefaultPolicies,
    EndpointRule? EffectiveRule, bool Overridden, string? Error);
public sealed record EndpointRule(bool Enabled, IReadOnlyList<string> RequiredPolicies);
public sealed record SdkPermissions(List<EndpointAccess> Operations, Dictionary<string, List<string>> PolicyOverrides,
    Dictionary<string, List<string>> AgentRoles);
