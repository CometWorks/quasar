using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

public sealed class ClusterCommandService
{
    private readonly ClusterCatalog _catalog;
    private readonly ClusterOperationStore _operations;

    public ClusterCommandService(ClusterCatalog catalog, ClusterOperationStore operations)
    {
        _catalog = catalog;
        _operations = operations;
    }

    public Task<ClusterOperation> SetGoalAsync(string uniqueName, ClusterGoalRequest request,
        string idempotencyKey, string actor, CancellationToken cancellationToken = default) =>
        _operations.ExecuteAsync(uniqueName, "cluster.goal.set", idempotencyKey, actor, request,
            async token =>
            {
                ClusterDefinition updated = await _catalog.SetGoalStateAsync(uniqueName, request.Goal, token);
                return new Admin.AdminEnvelope<ClusterGoalResult>(Admin.AdminProtocol.Version,
                    DateTimeOffset.UtcNow,
                    new ClusterGoalResult(updated.UniqueName, updated.GoalState, updated.UpdatedAtUtc));
            }, cancellationToken);
}

public sealed record ClusterGoalRequest(DedicatedServerGoalState Goal);
public sealed record ClusterGoalResult(string ClusterId, DedicatedServerGoalState Goal, DateTimeOffset UpdatedAt);
