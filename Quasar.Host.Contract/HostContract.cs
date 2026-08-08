namespace Quasar.Host.Contract.V1;

public static class HostProtocol
{
    public const int Version = 1;
    public const string HeaderName = "X-Quasar-Host-Protocol";
    public const string RoutePrefix = "/host/v1";
    public const string StatusRoute = RoutePrefix + "/status";

    public static string AttachmentRoute(string clusterId) =>
        $"{RoutePrefix}/attachments/{Uri.EscapeDataString(clusterId)}";
}

public sealed record HostEnvelope<T>(int ProtocolVersion, DateTimeOffset CapturedAt, T Data);

public sealed record HostErrorEnvelope(int ProtocolVersion, DateTimeOffset CapturedAt, HostError Error);

public sealed record HostError(string Code, string Message);

public sealed record HostStatus(
    string ExecutorId,
    string HostId,
    HostAttachmentStatus[] Attachments);

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
