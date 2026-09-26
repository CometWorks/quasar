namespace CometWorks.ClusterGateway.AdminContract.V1;

/// <summary>Separate machine contract. Credentials have Executor scope and their name is the host ID.</summary>
public static class ExecutorProtocol
{
    public const string HeartbeatRoute = "/executor/v1/heartbeat";
    public const int MaxReports = 256;
    public const int LeaseSeconds = 60;
}

public sealed record ExecutorPollRequest(string Host, string Executor, Guid Session,
    Guid? Lease, long Sequence, ExecutorNodeReport[] Reports, string? DeploymentRevision = null);

public sealed record ExecutorNodeReport(string SlotKey, string AttemptKey,
    NodeObservation State, string? Node, long Epoch, string? Failure);

public sealed record ExecutorPollResult(Guid Lease, DateTimeOffset ExpiresAt, NodePlan[] Plan);
