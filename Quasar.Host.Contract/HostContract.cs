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

public sealed record HostStatus(
    string ExecutorId,
    string HostId,
    HostAttachmentStatus[] Attachments,
    GatewayStatus[]? Gateways = null);

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
    string RunRoot);

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
    string? Failure);
