using System.Security.Cryptography;
using System.Text.Json;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed class ClusterDeploymentService(ClusterCatalog catalog, ClusterHostClient hosts, ClusterOperationStore operations)
{
    public Task<ClusterOperation> ActivateAsync(string clusterId, ClusterDeploymentRequest request,
        string idempotencyKey, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.deployment.activate", idempotencyKey, actor, request,
            async cancellation => new Admin.AdminEnvelope<ClusterActiveRevision>(Admin.AdminProtocol.Version,
                DateTimeOffset.UtcNow, await catalog.WithLifecycleAsync(clusterId,
                    cluster => ActivateCoreAsync(cluster, request, cancellation), cancellation)), token);

    public Task<ClusterOperation> PrepareAsync(string clusterId, ClusterPreparationRequest request, string key,
        string actor, CancellationToken token) => operations.ExecuteAsync(clusterId, "cluster.deployment.prepare", key, actor,
            request, async cancellation =>
            {
                var cluster = catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId);
                using var spec = JsonDocument.Parse(request.SpecificationJson);
                if (spec.RootElement.GetProperty("clusterId").GetString() != clusterId)
                    throw new InvalidDataException("Specification identifies a different cluster.");
                string[] required = spec.RootElement.GetProperty("hosts").EnumerateArray().Select(h => h.GetProperty("id").GetString()!)
                    .Order(StringComparer.Ordinal).ToArray();
                if (request.Hosts is null || request.Hosts.Length is < 1 or > 64
                    || !required.SequenceEqual(request.Hosts.Select(h => h.HostId).Order(StringComparer.Ordinal))
                    || required.Distinct().Count() != required.Length)
                    throw new InvalidDataException("Preparation requires the exact declared Host inventory.");
                string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.SpecificationJson))).ToLowerInvariant();
                string? revision = null;
                var activations = new List<ClusterHostActivation>();
                foreach (var host in request.Hosts)
                {
                    var target = cluster.Clone();
                    target.HostCommandUrl = host.CommandUrl;
                    target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
                    var prepared = (await hosts.PrepareDeploymentAsync(target, new(clusterId, host.InstallationDirectory,
                        request.InputsSha256, request.SpecificationJson, hash, host.WorldDirectory, host.ConfigurationDirectory), cancellation)).Data;
                    if (prepared.HostId != host.HostId || revision is not null && prepared.Revision != revision)
                        throw new InvalidDataException("Hosts prepared different deployment identities.");
                    revision = prepared.Revision;
                    activations.Add(new(host.HostId, host.CommandUrl, host.TokenEnvironmentVariable,
                        new(clusterId, cluster.ActiveDeployment?.Hosts.SingleOrDefault(h => h.HostId == host.HostId)?.Deployment.BundleManifestSha256,
                            prepared.Manifest, prepared.Sha256, request.GatewayUrl, host.ExecutorTokenEnvironmentVariable)));
                }
                await catalog.RecordPreparationAsync(cluster, request, cancellation);
                return new Admin.AdminEnvelope<ClusterDeploymentRequest>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
                    new(cluster.ActiveDeployment?.Revision, revision!, activations.ToArray()));
            }, token);

    public Task<ClusterOperation> RecoverClusterAsync(string clusterId, Guid generation, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.recover", key, actor, new { generation }, async ct =>
            await catalog.WithLifecycleAsync(clusterId, async cluster =>
            {
                if (cluster.Gateway is { Recover: true } replay && replay.StartGeneration == generation)
                    return new Admin.AdminEnvelope<HostContract.GatewaySpec>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, replay);
                if (generation == Guid.Empty || cluster.GoalState != DedicatedServerGoalState.Off || cluster.ActiveDeployment is null
                    || cluster.Gateway?.StartGeneration is null || cluster.Gateway.StartGeneration == generation
                    || cluster.Update is { Phase: not ClusterUpdatePhase.Complete }
                    || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                    throw new InvalidOperationException("Recovery requires goal Off, a new generation and a complete managed deployment.");
                ClusterDefinition Target(ClusterHostRevision host)
                {
                    var target = cluster.Clone(); target.HostCommandUrl = host.CommandUrl;
                    target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable; return target;
                }
                // First refuse recovery over any live/unknown node before touching the Gateway.
                foreach (var host in cluster.ActiveDeployment.Hosts)
                {
                    var ready = (await hosts.CheckRecoveryAsync(Target(host), host.Deployment.BundleManifestSha256, ct)).Data;
                    if (ready.HostId != host.HostId || ready.BundleManifestSha256 != host.Deployment.BundleManifestSha256)
                        throw new InvalidOperationException("Host recovery identity mismatch.");
                }
                var status = (await hosts.GetStatusAsync(cluster, ct)).Data;
                var observed = status.Gateways?.SingleOrDefault(g => g.ClusterId == clusterId);
                if (status.HostId != cluster.ActiveDeployment.Hosts.Single(h => h.Deployment.Gateway is not null).HostId
                    || !status.GatewayStopFencing || observed is null
                    || observed.Observed == HostContract.GatewayObservedState.UnmanagedConflict
                    || !ClusterReconciler.MatchesSpec(observed, cluster.Gateway))
                    throw new InvalidOperationException("Recovery requires a fenced, known Gateway process state.");
                var off = cluster.Gateway with { Goal = HostContract.GatewayGoal.Off, StopFence = observed.ProcessId is { } pid
                    && observed.LaunchedAt is { } launched ? new(pid, launched) : null };
                var stopped = (await hosts.ApplyGatewayAsync(cluster, off, ct)).Data;
                if (stopped.Observed != HostContract.GatewayObservedState.Missing || stopped.Failure is not null
                    || stopped.CompletedStopFence != off.StopFence || !ClusterReconciler.MatchesSpec(stopped, off))
                    throw new InvalidOperationException("Gateway stop has not been verified.");
                // With Gateway stopped, no fresh executor lease can spawn anything. Recheck every
                // Host under its execution gate, including any action already in flight above.
                foreach (var host in cluster.ActiveDeployment.Hosts)
                    await hosts.PreviewDeploymentAsync(Target(host), new(clusterId, host.Deployment.BundleManifestSha256,
                        host.Deployment.Attachment.BundleManifestPath!, host.Deployment.BundleManifestSha256,
                        host.Deployment.Attachment.GatewayUrl, host.Deployment.Attachment.TokenEnvironmentVariable), ct);
                await operations.FenceGatewayOperationsForRestoreAsync(clusterId, ct, recovery: true);
                var recovered = await catalog.RecordRecoveryAsync(cluster, generation, ct);
                return new Admin.AdminEnvelope<HostContract.GatewaySpec>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, recovered.Gateway!);
            }, ct), token);

    public Task<ClusterOperation> RecoverGatewayAsync(string clusterId, string idempotencyKey, string actor,
        CancellationToken token) => operations.ExecuteAsync(clusterId, "cluster.gateway.recover", idempotencyKey, actor,
            new { clusterId }, async cancellation => await catalog.WithLifecycleAsync(clusterId, async cluster =>
            {
                if (cluster.GoalState != DedicatedServerGoalState.Off || cluster.PendingDeploymentHash is not null
                    || cluster.Gateway?.StartGeneration is null || cluster.ActiveDeployment is null)
                    throw new InvalidOperationException("Gateway recovery requires a managed deployment, goal Off and no incomplete activation.");
                if (cluster.ShutdownProof?.LifecycleId == cluster.GetLifecycleId())
                    throw new InvalidOperationException("This deployment already has clean-Down proof; use a normal start.");
                var status = (await hosts.GetStatusAsync(cluster, cancellation)).Data;
                var observed = status.Gateways?.SingleOrDefault(g => g.ClusterId == clusterId);
                if (!status.GatewayStopFencing || observed?.Observed == HostContract.GatewayObservedState.UnmanagedConflict)
                    throw new InvalidOperationException("Gateway recovery requires a fenced Host without process conflicts.");
                // Explicit operator authorization to restart only the recorded Gateway generation.
                // Registry replays its WAL and retains any drain or shutdown in progress.
                var result = await hosts.ApplyGatewayAsync(cluster,
                    cluster.Gateway with { Goal = HostContract.GatewayGoal.On, StopFence = null }, cancellation);
                return new Admin.AdminEnvelope<HostContract.GatewayStatus>(Admin.AdminProtocol.Version,
                    DateTimeOffset.UtcNow, result.Data);
            }, cancellation), token);

    internal async Task<ClusterActiveRevision> ActivateCoreAsync(ClusterDefinition cluster,
        ClusterDeploymentRequest request, CancellationToken token, bool restore = false, bool dryRun = false, bool preflight = false, bool update = false)
    {
        if (preflight && !dryRun) throw new InvalidOperationException("Online preflight cannot activate a deployment.");
        if (!update && cluster.Update is { Phase: not ClusterUpdatePhase.Complete })
            throw new InvalidOperationException("A managed update owns this cluster lifecycle.");
        if (request.Hosts is null || request.Hosts.Length is < 1 or > 64
            || string.IsNullOrWhiteSpace(request.Revision)
            || request.Hosts.Any(h => h is null || h.Activation is null || string.IsNullOrWhiteSpace(h.HostId)
                || !Uri.TryCreate(h.CommandUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(h.TokenEnvironmentVariable) || h.Activation.ClusterId != cluster.UniqueName)
            || request.Hosts.Select(h => h.HostId).Distinct(StringComparer.Ordinal).Count() != request.Hosts.Length
            || request.Hosts.Select(h => h.CommandUrl.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Hosts.Length)
            throw new InvalidDataException("Deployment requires a revision and unique, valid Host endpoints and identities.");
        // Recover a lost operation-journal acknowledgement after the catalog commit.
        if (cluster.ActiveDeployment is { } current && current.Revision == request.Revision
            && current.Hosts.Length == request.Hosts.Length && request.Hosts.All(h => current.Hosts.Any(a =>
                a.HostId == h.HostId && a.CommandUrl == h.CommandUrl
                && a.TokenEnvironmentVariable == h.TokenEnvironmentVariable
                && a.Deployment.BundleManifestSha256 == h.Activation.BundleManifestSha256
                && a.Deployment.Attachment.BundleManifestPath == h.Activation.BundleManifestPath
                && a.Deployment.Attachment.GatewayUrl == h.Activation.GatewayUrl
                && a.Deployment.Attachment.TokenEnvironmentVariable == h.Activation.ExecutorTokenEnvironmentVariable)))
            return current;
        if (!restore && cluster.PendingRestoreHash is not null)
            throw new InvalidOperationException("Complete the interrupted restore before deployment activation.");
        if (!preflight && (cluster.GoalState != DedicatedServerGoalState.Off || !restore && operations.HasPendingShutdown(cluster.UniqueName)))
            throw new InvalidOperationException("Deployment activation requires goal Off and completed shutdown.");
        if (cluster.ActiveDeployment?.Revision != request.ExpectedRevision)
            throw new InvalidOperationException("Active deployment changed; refresh before activation.");
        if (!preflight && !restore && cluster.Gateway is not null && cluster.ShutdownProof?.LifecycleId != cluster.GetLifecycleId())
            throw new InvalidOperationException("Existing deployments require matching clean-Down proof before activation.");
        var hostIds = request.Hosts.Select(h => h.HostId).Order(StringComparer.Ordinal).ToArray();
        if (cluster.ActiveDeployment is { } previous
            && !previous.Hosts.Select(h => h.HostId).Order(StringComparer.Ordinal).SequenceEqual(hostIds))
            throw new InvalidOperationException("Host inventory changes require explicit migration before deployment activation.");
        string requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        if (cluster.PendingDeploymentHash is { } pending && pending != requestHash)
            throw new InvalidOperationException("Resume the interrupted deployment with its original request before activating another revision.");
        var previews = new List<(ClusterHostActivation Host, ClusterDefinition Target, HostContract.HostActiveDeployment Preview)>();
        foreach (var host in request.Hosts)
        {
            var target = cluster.Clone();
            target.HostCommandUrl = host.CommandUrl.TrimEnd('/');
            target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
            if ((await hosts.GetStatusAsync(target, token)).Data.HostId != host.HostId)
                throw new InvalidDataException("Host endpoint identifies a different host.");
            var preview = (await hosts.PreviewDeploymentAsync(target, host.Activation, token, online: preflight)).Data;
            ValidateResult(preview, host, request, hostIds);
            previews.Add((host, target, preview));
        }
        if (previews.Count(p => p.Preview.Gateway is not null) != 1)
            throw new InvalidDataException("Deployment must have exactly one Gateway host.");
        if (cluster.ActiveDeployment is { } prior && prior.Hosts.Single(h => h.Deployment.Gateway is not null).HostId
            != previews.Single(p => p.Preview.Gateway is not null).Host.HostId)
            throw new InvalidOperationException("Gateway relocation requires explicit migration before activation.");
        if (dryRun) return new ClusterActiveRevision(request.Revision, previews.Select(p =>
            new ClusterHostRevision(p.Host.HostId, p.Host.CommandUrl, p.Host.TokenEnvironmentVariable, p.Preview)).ToArray(), DateTimeOffset.UtcNow);
        await catalog.RecordPendingDeploymentAsync(cluster, requestHash, token);
        var activated = new List<ClusterHostRevision>();
        foreach (var (host, target, _) in previews)
        {
            var result = (await hosts.ActivateDeploymentAsync(target, host.Activation, token)).Data;
            ValidateResult(result, host, request, hostIds);
            activated.Add(new(host.HostId, host.CommandUrl, host.TokenEnvironmentVariable, result));
        }
        var active = new ClusterActiveRevision(request.Revision, activated.ToArray(), DateTimeOffset.UtcNow);
        await catalog.RecordActiveDeploymentAsync(cluster, active, token);
        return active;
    }
    private static void ValidateResult(HostContract.HostActiveDeployment result, ClusterHostActivation host,
        ClusterDeploymentRequest request, string[] hostIds)
    {
        if (result.ClusterId != host.Activation.ClusterId || result.Revision != request.Revision
            || result.BundleManifestSha256 != host.Activation.BundleManifestSha256
            || result.Attachment.BundleManifestPath != host.Activation.BundleManifestPath
            || result.Attachment.GatewayUrl != host.Activation.GatewayUrl.TrimEnd('/')
            || result.RequiredHosts is null || !result.RequiredHosts.Order(StringComparer.Ordinal).SequenceEqual(hostIds))
            throw new InvalidDataException("Host deployment identity or required fleet does not match activation.");
    }
}

public sealed record ClusterDeploymentRequest(string? ExpectedRevision, string Revision, ClusterHostActivation[] Hosts);
public sealed record ClusterHostActivation(string HostId, string CommandUrl, string TokenEnvironmentVariable,
    HostContract.HostDeploymentActivation Activation);

public sealed record ClusterPreparationRequest(string InputsSha256, string SpecificationJson, string GatewayUrl, ClusterPreparationHost[] Hosts);
public sealed record ClusterPreparationHost(string HostId, string CommandUrl, string TokenEnvironmentVariable,
    string ExecutorTokenEnvironmentVariable, string InstallationDirectory, string WorldDirectory, string ConfigurationDirectory);
