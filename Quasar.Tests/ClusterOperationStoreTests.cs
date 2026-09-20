using System.Net;
using CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterOperationStoreTests
{
    [Fact]
    public async Task SlowLocalOperationDoesNotBlockOtherCommandsAndSameKeyStillExecutesOnce()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quasar-cluster-operation-" + Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var store = new ClusterOperationStore(directory);
            var first = store.ExecuteAsync("demo", "cluster.package.stage", "stage-1", "factory", "package",
                async token =>
                {
                    entered.SetResult();
                    await finish.Task.WaitAsync(token);
                    return new AdminEnvelope<string>(1, DateTimeOffset.UtcNow, "staged");
                }, timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            var replay = store.ExecuteAsync<string, string>("DEMO", "cluster.package.stage", "stage-1", "factory",
                "package", _ => throw new InvalidOperationException("must not repeat"), timeout.Token);
            var unrelated = await store.ExecuteAsync("demo", "cluster.goal.set", "goal-1", "factory", "off",
                _ => Task.FromResult(new AdminEnvelope<string>(1, DateTimeOffset.UtcNow, "off")), timeout.Token);
            Assert.Equal(ClusterOperationState.Succeeded, unrelated.State);
            Assert.False(replay.IsCompleted);
            finish.SetResult();
            Assert.Equal((await first).OperationId, (await replay).OperationId);
        }
        finally
        {
            finish.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PackageFailureIsPersistedAndReplayedAfterRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quasar-cluster-operation-" + Guid.NewGuid());
        try
        {
            var store = new ClusterOperationStore(directory);
            var failed = await store.ExecuteAsync<string, string>("demo", "cluster.package.stage", "stage-1",
                "factory", "package", _ => throw new ClusterPackageException("Invalid archive."), default);
            var replay = await new ClusterOperationStore(directory).ExecuteAsync<string, string>(
                "demo", "cluster.package.stage", "stage-1", "factory", "package",
                _ => throw new InvalidOperationException("must not repeat"), default);
            Assert.Equal(ClusterOperationState.Failed, replay.State);
            Assert.Equal("cluster_package_failed", replay.Error?.Code);
            Assert.Equal(failed.OperationId, replay.OperationId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PersistsAndReplaysIdempotentOperation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quasar-cluster-operation-" + Guid.NewGuid());
        var policy = new AdminConfigUpdate(1, []);
        int calls = 0;
        try
        {
            var store = new ClusterOperationStore(directory);
            ClusterOperation first = await store.ExecuteAsync("demo", "cluster.config.set", "request-1",
                "factory", policy, _ =>
                {
                    calls++;
                    return Task.FromResult(new AdminEnvelope<string>(AdminProtocol.Version,
                        DateTimeOffset.UtcNow, "applied"));
                }, CancellationToken.None);

            Assert.Equal(ClusterOperationState.Succeeded, first.State);
            Assert.Equal(1, calls);
            Assert.NotNull(store.Get(first.OperationId)?.Result);

            var recovered = new ClusterOperationStore(directory);
            ClusterOperation replay = await recovered.ExecuteAsync<AdminConfigUpdate, string>(
                "demo", "cluster.config.set", "request-1",
                "factory", policy, _ => throw new InvalidOperationException("must not repeat"), CancellationToken.None);
            Assert.Equal(first.OperationId, replay.OperationId);

            ClusterOperationConflictException conflict = await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
                recovered.ExecuteAsync<AdminConfigUpdate, string>(
                    "demo", "cluster.config.set", "request-1", "factory",
                    policy with { ExpectedRevision = 2 }, _ => throw new InvalidOperationException(), CancellationToken.None));
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
            ClusterOperation failed = await store.ExecuteAsync<AdminConfigUpdate, string>(
                "demo", "cluster.config.set", "request-2",
                "factory", new AdminConfigUpdate(1, []),
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
