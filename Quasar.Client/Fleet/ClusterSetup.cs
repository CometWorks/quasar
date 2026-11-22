namespace Quasar.Services;

public sealed record ClusterSetupPlacement(string HostId, int RegularNodes);
/// <summary><paramref name="HotSpares"/>: how many hot spares the cluster has (the setup page defaults to 1). Quasar only
/// configures the count; the cluster promotes a spare when a node crashes and refills it.
/// <paramref name="DefaultPlugins"/>: add the recommended server plugins (Bugfixes, Performance) to the cluster's
/// copy of the profile when it lacks them. The source profile is never changed.
/// <paramref name="InGameWorldName"/>: the world name players see; <paramref name="DisplayName"/> when empty.</summary>
public sealed record ClusterSetupRequest(string UniqueName, string DisplayName, string WorldTemplateId,
    string ConfigProfileId, string GatewayHost, int PlayerPort, ClusterSetupPlacement[] Machines, int HotSpares = 0,
    bool DefaultPlugins = true, string? InGameWorldName = null)
{
    public string WorldName => string.IsNullOrWhiteSpace(InGameWorldName) ? DisplayName : InGameWorldName.Trim();
}
public sealed record ClusterSetupStatus(ClusterSetupRequest Request, string Phase, string? Error, DateTimeOffset UpdatedAt, string? FailedPhase = null);
