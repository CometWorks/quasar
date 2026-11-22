namespace Quasar.Client.Administration;

public sealed record UpdatesDashboard(string Status, bool Enabled, string CurrentVersion, string CurrentBootstrapVersion,
    string Message, DateTimeOffset? LastCheckedUtc, string SelectedWebVersion, UpdateRelease? Web, UpdateRelease[] WebReleases,
    UpdateRelease? Bootstrap, UpdateProgress? Operation, RestartStatus RestartCapability, string AppSettingsConflictPath,
    bool AppSettingsConflict, string GitHubTokenWarning, bool GitHubTokenConfigured, bool GitHubTokenSaved,
    bool GitHubTokenExpired, bool GitHubTokenExpiringSoon, string GitHubTokenVariableName, DateTimeOffset? GitHubTokenExpiresAtUtc,
    bool IncludePrerelease, bool AutoStageWebUpdates, UpdateOptionsView Options, Quasar.Services.DedicatedServerLaunchUpdateMode LaunchUpdateMode,
    Quasar.Services.ManagedRuntimeWarmupSnapshot Runtime, ClusterReleasesView Clusters);

public sealed record UpdateOptionsView(string Owner, string Repository, TimeSpan CheckInterval, bool AutoApplyUpdates,
    string PackageAssetName, string WebAssetName, string? BootstrapVersion, bool IsUnderBootstrap, string? BootstrapUpdateUnavailableReason,
    string CredentialsFile, double MagnetarCheckMinutes);
public sealed record UpdateRelease(string Version, string AssetName, bool IsCurrent, bool IsNewer, bool IsPrerelease, bool IsStaged);
public sealed record UpdateProgress(Guid Id, string Version, string Stage, string Message, DateTimeOffset UpdatedAtUtc, string PackageDirectory);
public sealed record RestartStatus(bool CanRestart, string Supervisor, string Detail, string Repair);
public sealed record UpdateCluster(string UniqueName, string DisplayName, string Version, bool UpdateAvailable);
public sealed record ClusterReleasesView(string? Version, DateTimeOffset? LastCheckedUtc, string? Error, UpdateCluster[] Rows);
public sealed record ConflictDocuments(string Current, string Incoming);
