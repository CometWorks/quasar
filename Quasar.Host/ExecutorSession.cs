using System.Diagnostics;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Host;

/// <summary>One boot-local executor session per cluster. Process identity stays in NodeActualizer's durable records.</summary>
internal sealed class ExecutorSession
{
    private readonly Guid _session = Guid.NewGuid();
    private Guid? _lease;
    private long _sequence = 1;
    private Admin.ExecutorPollRequest? _pending;

    internal async Task PollAsync(HostExecutorConfig config, HostContract.HostAttachmentSpec attachment,
        NodeActualizer actualizer,
        Func<Admin.ExecutorPollRequest, CancellationToken, Task<Admin.ExecutorPollResult>> send,
        CancellationToken token)
    {
        string? revision = attachment.BundleManifestPath is null ? null
            : ExecutionBundle.ReadManifest(attachment.BundleManifestPath, attachment.BundleManifestSha256!).Revision;
        _pending ??= new(config.HostId, config.ExecutorId, _session, _lease, _sequence, [], revision);
        long started = Stopwatch.GetTimestamp();
        Admin.ExecutorPollResult response;
        try { response = await send(_pending, token); }
        catch (InvalidOperationException error) when (error.Message == "executor_fenced")
        {
            _lease = null;
            _pending = null;
            _sequence++;
            throw;
        }
        _pending = null;
        _sequence++;
        _lease = response.Lease;
        // Use elapsed monotonic time, not agreement between host and Gateway clocks.
        TimeSpan remaining = TimeSpan.FromSeconds(Admin.ExecutorProtocol.LeaseSeconds - 5)
            - Stopwatch.GetElapsedTime(started);
        if (response.Lease == Guid.Empty || remaining <= TimeSpan.Zero
            || response.Plan.Any(slot => slot.Host != config.HostId))
            throw new InvalidOperationException("executor_plan_invalid_or_expired");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(remaining);
        NodeExecutionObservation[] observations;
        try { observations = await actualizer.ReconcileAsync(attachment, response.Plan, budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("executor_lease_expired"); }
        _pending = new(config.HostId, config.ExecutorId, _session, _lease, _sequence,
            observations.Select(item => new Admin.ExecutorNodeReport(item.SlotKey, item.AttemptKey,
                item.State, item.Node, item.Epoch, item.Failure)).ToArray(), revision);
    }
}
