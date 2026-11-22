using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Client.Clusters;

public sealed record ClusterRuntime(Admin.ClusterStatus? Status, DateTimeOffset? CapturedAt, Admin.AdminCapabilities? Capabilities,
    Admin.NodePlan[] Plan, Admin.RecoveryReadiness? Recovery, PanelNode[] Nodes, ServiceEndpoint[] Endpoints, HostLabel[] Hosts,
    bool MultipleHosts, string?[]? ClientNodes, string?[]? PartitionNodes, string ReconcileState, string? ReconcileMessage, string? ReconcileError,
    string[]? GatewayOutput, bool Stopped, string HostText, string GatewayText, string? GatewayObserved, ProcessSample? GatewayMetrics, string? Error);
public sealed record PanelNode(string Name, string? Host, Admin.NodeRole Role, bool Spare, Admin.NodePlan? Plan, PanelFleetNode? Fleet);
public sealed record PanelFleetNode(Admin.NodeStatus Registry, PanelTelemetry? Telemetry, bool Idle);
public sealed record PanelTelemetry(bool Connected, bool? ReadinessVerified, string? DeploymentFailure, NodeMetrics? Metrics);
public sealed record NodeMetrics(double SimSpeed, double? ServerCpuLoadPercent, long? MemoryWorkingSetMb);
public sealed record ProcessSample(DateTimeOffset CapturedAt, double? CpuPercent, long MemoryWorkingSetBytes);
public sealed record ServiceEndpoint(string Process, string Service, string Address, string? Bind);
public sealed record HostLabel(string Id, string Name);
public sealed record PanelNodeDetails(string AgentId, NodePlugin[] Plugins, string StatisticsJson, ClusterLog[] Logs);
public sealed record NodePlugin(string DisplayName, string? Version, bool IsLoaded);
