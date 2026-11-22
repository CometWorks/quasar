namespace Quasar.Client.Clusters;

public sealed record ClusterDashboard(string UniqueName, string DisplayName, bool GoalOn, bool HasGateway, bool ManagedLifecycle,
    bool Deployed, string? DeployedVersion, string? LatestVersion, bool UpdateAvailable, DateTimeOffset? ReleaseChecked, string? ReleaseError,
    string? SetupPhase, string? SetupError, bool CanManage, bool CanMaintain, bool CanEdit,
    string ConfigProfileId, string? PendingProfileId, int ShutdownGracePeriodSeconds, int HotSpares, HostNodeCount[] RegularNodes,
    ClusterProfileChoice[] Profiles, string ProfileName, string WorldName, string? PreparationRevision = null);
public sealed record HostNodeCount(string HostId, int RegularNodes);
public sealed record ClusterProfileChoice(string ConfigProfileId, string Name, string[] MissingDefaults);
public sealed record SaveClusterSettings(string DisplayName, int ShutdownGracePeriodSeconds);
public sealed record ClusterDefaultOffer(string ProfileId, bool Enable);
public sealed record ClusterPluginConfig(string PluginId, string DisplayName, string ConfigType, string ConfigJson);
public sealed record ClusterPluginTarget(string PluginId, string ConfigType);
public sealed record ClusterContentView(bool Scheduled, bool Queued, DateTimeOffset? CheckedAt, ContentItem[] Items, string? InventoryError, string? CheckError);
public sealed record ContentItem(string Kind, string Id, string Name, string? PinnedVersion, string? BaselineVersion, string? LatestVersion,
    DateTimeOffset? ObservedAt, string? Error, string? DetailsUrl, bool HasUpdate);
