using System.Net;
using CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterOperationStoreTests
{
    [Fact]
    public async Task PersistsAndReplaysIdempotentOperation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quasar-cluster-operation-" + Guid.NewGuid());
        var policy = new ClusterPolicy("revision-1", 1, ["host-a"], "host-a");
        int calls = 0;
        try
        {
            var store = new ClusterOperationStore(directory);
            ClusterOperation first = await store.ExecuteAsync("demo", "cluster.config.set", "request-1",
                "factory", policy, _ =>
                {
                    calls++;
                    return Task.FromResult(new AdminEnvelope<ClusterPolicyApplied>(AdminProtocol.Version,
                        DateTimeOffset.UtcNow, new ClusterPolicyApplied(policy.Revision, true, [])));
                }, CancellationToken.None);

            Assert.Equal(ClusterOperationState.Succeeded, first.State);
            Assert.Equal(1, calls);
            Assert.NotNull(store.Get(first.OperationId)?.Result);

            var recovered = new ClusterOperationStore(directory);
            ClusterOperation replay = await recovered.ExecuteAsync<ClusterPolicy, ClusterPolicyApplied>(
                "demo", "cluster.config.set", "request-1",
                "factory", policy, _ => throw new InvalidOperationException("must not repeat"), CancellationToken.None);
            Assert.Equal(first.OperationId, replay.OperationId);

            ClusterOperationConflictException conflict = await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
                recovered.ExecuteAsync<ClusterPolicy, ClusterPolicyApplied>(
                    "demo", "cluster.config.set", "request-1", "factory",
                    policy with { NodeTargetCount = 2 }, _ => throw new InvalidOperationException(), CancellationToken.None));
            Assert.Equal("idempotency_key_conflict", conflict.Code);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecordsGatewayFailureForPolling()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quasar-cluster-operation-" + Guid.NewGuid());
        try
        {
            var store = new ClusterOperationStore(directory);
            ClusterOperation failed = await store.ExecuteAsync<ClusterPolicy, ClusterPolicyApplied>(
                "demo", "cluster.config.set", "request-2",
                "factory", new ClusterPolicy("bad", 1, ["host-a"], "host-a"),
                _ => throw new ClusterGatewayException(
                    HttpStatusCode.Conflict, "registry_conflict", "Rejected."), CancellationToken.None);

            Assert.Equal(ClusterOperationState.Failed, failed.State);
            Assert.Equal("registry_conflict", failed.Error?.Code);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
