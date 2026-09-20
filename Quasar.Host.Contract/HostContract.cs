using System.Text.Json.Serialization;

namespace Quasar.Host.Contract.V1;

public static class HostProtocol
{
    public const int Version = 1;
    public const string HeaderName = "X-Quasar-Host-Protocol";
    public const string RoutePrefix = "/host/v1";
    public const string StatusRoute = RoutePrefix + "/status";

    public static string AttachmentRoute(string clusterId) =>
        $"{RoutePrefix}/attachments/{Uri.EscapeDataString(clusterId)}";

    public static string GatewayRoute(string clusterId) =>
        $"{RoutePrefix}/gateways/{Uri.EscapeDataString(clusterId)}";
}

public sealed record HostEnvelope<T>(int ProtocolVersion, DateTimeOffset CapturedAt, T Data);

public sealed record HostErrorEnvelope(int ProtocolVersion, DateTimeOffset CapturedAt, HostError Error);

public sealed record HostError(string Code, string Message);

// Preparation only: never a spawn specification or permission to activate a cluster.
public sealed record ClusterDeploymentInputs(string ClusterId, long PackageSelectionRevision,
    string PackageVersion, string PackageSha256, string PackageCommit, string DependencyManifestSha256,
    string PackageDirectory, string DependencyDirectory, Dictionary<string, DeploymentFile> Files);

public sealed record DeploymentFile(string Sha256, long Bytes, bool Executable);
public sealed record PreparedClusterDeployment(string Directory, string InputsSha256, string EnvironmentFile);

public sealed record HostStatus(
    string ExecutorId,
    string HostId,
    HostAttachmentStatus[] Attachments,
    GatewayStatus[]? Gateways = null,
    bool GatewayStopFencing = false);

public sealed record HostAttachmentStatus(
    string ClusterId,
    string GatewayUrl,
    bool ActualizationConfigured,
    string? BundleManifestSha256,
    string? RunRoot);

public sealed record HostAttachmentSpec(
    string ClusterId,
    string GatewayUrl,
    string TokenEnvironmentVariable,
    string? BundleManifestPath = null,
    string? BundleManifestSha256 = null,
    string? RunRoot = null);

[JsonConverter(typeof(JsonStringEnumConverter<GatewayGoal>))]
public enum GatewayGoal { Off, On }

[JsonConverter(typeof(JsonStringEnumConverter<GatewayObservedState>))]
public enum GatewayObservedState { Missing, Running, Failed, UnmanagedConflict }

public sealed record GatewaySpec(
    string ClusterId,
    GatewayGoal Goal,
    string BundleManifestPath,
    string BundleManifestSha256,
    string ConfigRevision,
    int[] Ports,
    string RunRoot,
    GatewayStopFence? StopFence = null, Guid? StartGeneration = null, bool Recover = false);

public sealed record GatewayStopFence(int ProcessId, DateTimeOffset LaunchedAt);

public sealed record GatewayStatus(
    string ClusterId,
    GatewayGoal Goal,
    GatewayObservedState Observed,
    string BundleManifestSha256,
    string ConfigRevision,
    int[] Ports,
    string RunRoot,
    int? ProcessId,
    DateTimeOffset? LaunchedAt,
    string? Failure,
    GatewayStopFence? CompletedStopFence = null, Guid? StartGeneration = null);

public sealed record HostDeploymentActivation(string ClusterId, string? ExpectedBundleManifestSha256,
    string BundleManifestPath, string BundleManifestSha256, string GatewayUrl, string ExecutorTokenEnvironmentVariable);
public sealed record HostActiveDeployment(string ClusterId, string Revision, string BundleManifestSha256,
    HostAttachmentSpec Attachment, GatewaySpec? Gateway, string[]? RequiredHosts = null);

public sealed record HostDeploymentPreparation(string ClusterId, string InstallationDirectory,
    string InputsSha256, string SpecificationJson, string SpecificationSha256, string WorldDirectory,
    string ConfigurationDirectory);
public sealed record HostPreparedConfiguration(string HostId, string Revision, string Manifest, string Sha256);

public sealed record HostSnapshotRequest(string ClusterId, Guid SnapshotId, string ExpectedBundleManifestSha256, string CaptureFence);
public sealed record HostSnapshot(string ClusterId, string HostId, Guid SnapshotId, string Revision,
    string BundleManifestSha256, string ArchiveSha256, long ArchiveBytes);
public sealed record HostSnapshotRestore(string ClusterId, Guid RestoreId, Guid SnapshotId, string ArchiveSha256,
    string CandidateManifestPath, string CandidateManifestSha256, string CandidateExecutorTokenEnvironmentVariable);

public sealed record HostRecoveryReadiness(string HostId, string BundleManifestSha256);

public sealed record HostConversionPaths(string HostId, string Directory, string ConfigurationDirectory, string RuntimeDirectory);
