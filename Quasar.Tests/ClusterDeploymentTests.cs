using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterDeploymentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cluster-activation-" + Guid.NewGuid());
    private readonly string credential = "ACTIVATION_TEST_" + Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PreparedCandidateSurvivesCatalogReload()
    {
        using var catalog = Catalog();
        var preparation = new ClusterPreparationRequest(new string('a', 64), "{\"clusterId\":\"demo\"}",
            "http://127.0.0.1:28016", []);
        var candidate = Request();
        await catalog.RecordPreparationAsync(catalog.GetCluster("demo")!, preparation, candidate, default);
        await catalog.RecordSelectedReleasePreparationAsync(catalog.GetCluster("demo")!, candidate, default);
        using var reloaded = Catalog();
        Assert.Equal(candidate.Revision, reloaded.GetCluster("demo")!.PreparedDeployment?.Revision);
        Assert.True(reloaded.GetCluster("demo")!.PreparedForSelectedRelease);
        await reloaded.RecordPreparationAsync(reloaded.GetCluster("demo")!, preparation, candidate, default);
        Assert.False(reloaded.GetCluster("demo")!.PreparedForSelectedRelease);
    }

    [Fact]
    public async Task DeleteObservationArchivesDefinitionAndPreservesData()
    {
        using var catalog = Catalog();
        string data = Path.Combine(root, "clusters", "demo", "world.dat");
        await File.WriteAllTextAsync(data, "world data");
        bool notified = false;
        catalog.Changed += () => notified = true;
        await Service(catalog, new Handler(credential)).DeleteAsync("demo");
        Assert.Null(catalog.GetCluster("demo"));
        Assert.True(notified);
        Assert.Equal("world data", await File.ReadAllTextAsync(data));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "clusters", "demo", "History"), "*-deleted.json"));
        using var reloaded = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        Assert.Null(reloaded.GetCluster("demo"));
    }

    [Fact]
    public async Task ForgetRequiresExactConfirmationAndPreventsIdentityReuse()
    {
        using var catalog = Catalog();
        var service = Service(catalog, new Handler(credential));
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ForgetAsync("demo", "DEMO"));
        var definition = catalog.GetCluster("demo")!;
        await service.ForgetAsync("demo", "demo");
        Assert.Null(catalog.GetCluster("demo"));
        foreach (string id in new[] { "demo", "DEMO" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CreateAsync(new(id, definition.DisplayName,
                definition.GatewayUrl, definition.GatewayAdminTokenEnvironmentVariable), default));
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("gateway-running")]
    [InlineData("node-running")]
    [InlineData("host-offline")]
    [InlineData("goal-on")]
    [InlineData("deployment-pending")]
    public async Task DeleteManagedRequiresStoppedFleet(string state)
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var service = Service(catalog, handler);
        await service.ActivateAsync("demo", Request(), "initial", "test", default);
        handler.Deletion = state;
        if (state == "goal-on") await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        if (state == "deployment-pending") await catalog.RecordPendingDeploymentAsync(catalog.GetCluster("demo")!, "pending", default);
        if (state == "stopped")
        {
            await service.DeleteAsync("demo");
            Assert.Null(catalog.GetCluster("demo"));
            Assert.Equal(new[] { "one", "two" }, handler.DeletePreviews);
        }
        else
        {
            var error = await Record.ExceptionAsync(() => service.DeleteAsync("demo"));
            Assert.True(error is InvalidOperationException or ClusterHostException, error?.ToString());
            Assert.NotNull(catalog.GetCluster("demo"));
        }
        Assert.Equal(2, handler.Applied.Count); // Deletion never activates or erases Host data.
    }

    [Fact]
    public async Task DeleteRefusesPendingOperations()
    {
        using var catalog = Catalog();
        var operations = new ClusterOperationStore(Path.Combine(root, "operations"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = operations.ExecuteAsync("demo", "test.operation", "pending", "test", new { }, async token =>
        {
            entered.SetResult();
            await finish.Task;
            return new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<bool>(1, DateTimeOffset.UtcNow, true);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var service = new ClusterDeploymentService(catalog, new ClusterHostClient(new HttpClient(new Handler(credential))), operations);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("demo"));
            Assert.NotNull(catalog.GetCluster("demo"));
        }
        finally { finish.TrySetResult(); await pending; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRequiresStoppedFleetThenCommitsReplayableGeneration(bool nodeAppearedDuringStop)
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var service = Service(catalog, handler);
        await service.ActivateAsync("demo", Request(), "initial", "test", default);
        var before = catalog.GetCluster("demo")!;
        var generation = Guid.NewGuid();
        handler.Recovery = true;
        handler.RefuseRecovery = !nodeAppearedDuringStop;
        handler.RefusePreview = nodeAppearedDuringStop;
        var failed = await service.RecoverClusterAsync("demo", generation, "recover", "test", default);
        Assert.Equal(ClusterOperationState.Failed, failed.State);
        Assert.Equal(before.Gateway!.StartGeneration, catalog.GetCluster("demo")!.Gateway!.StartGeneration);
        Assert.Equal(nodeAppearedDuringStop ? 1 : 0, handler.GatewayStops);
        handler.RefuseRecovery = handler.RefusePreview = false;
        var done = await service.RecoverClusterAsync("demo", generation, "recover-retry", "test", default);
        Assert.Equal(ClusterOperationState.Succeeded, done.State);
        var recovered = catalog.GetCluster("demo")!;
        Assert.Equal(generation, recovered.Gateway!.StartGeneration);
        Assert.True(recovered.Gateway.Recover);
        Assert.Equal(DedicatedServerGoalState.On, recovered.GoalState);
        Assert.Null(recovered.ShutdownProof);
        Assert.Equal(before.ActiveDeployment, recovered.ActiveDeployment);
        int stops = handler.GatewayStops;
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        await service.RecoverClusterAsync("demo", generation, "lost-ack", "test", default);
        Assert.Equal(stops, handler.GatewayStops);
        Assert.Equal(DedicatedServerGoalState.Off, catalog.GetCluster("demo")!.GoalState);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        Assert.False(catalog.GetCluster("demo")!.Gateway!.Recover);
    }

    [Fact]
    public async Task CommittedBackupRetriesHostCleanupButRejectsAnotherLifecycle()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        await Service(catalog, handler).ActivateAsync("demo", Request(), "initial", "test", default);
        var cluster = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(cluster, new(cluster.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(123, DateTimeOffset.UtcNow)), default);
        cluster = catalog.GetCluster("demo")!;
        Guid id = Guid.NewGuid();
        string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        string backupRoot = Path.Combine(root, "backups");
        string directory = Path.Combine(backupRoot, "Clusters", Hash(System.Text.Encoding.UTF8.GetBytes("demo")), id.ToString("N"));
        Directory.CreateDirectory(directory);
        byte[] archive = [1, 2, 3];
        var snapshots = cluster.ActiveDeployment!.Hosts.Select(h => new HostSnapshot("demo", h.HostId, id, "revision",
            h.Deployment.BundleManifestSha256, Hash(archive), archive.Length)).ToArray();
        foreach (var host in snapshots)
            await File.WriteAllBytesAsync(Path.Combine(directory, Hash(System.Text.Encoding.UTF8.GetBytes(host.HostId)) + ".tar"), archive);
        var backup = new ClusterBackup(id, "demo", DateTimeOffset.UtcNow,
            CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.Quiescent, cluster, snapshots, false);
        await File.WriteAllTextAsync(Path.Combine(directory, "backup.json"), JsonSerializer.Serialize(backup, Json));
        var cleanup = new SnapshotCleanupHandler();
        var service = new ClusterBackupService(catalog, new ClusterHostClient(new HttpClient(cleanup)),
            new ClusterOperationStore(Path.Combine(root, "backup-operations")), null!, new WebServiceOptions { BackupDirectory = backupRoot }, null!, null!);
        // One torn manifest must not hide the cluster's other backups from listing, scheduling and retention.
        string torn = Path.Combine(Path.GetDirectoryName(directory)!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(torn);
        await File.WriteAllBytesAsync(Path.Combine(torn, "backup.json"), []);
        Assert.Equal(id, Assert.Single(service.List("demo")).SnapshotId);
        Assert.Equal(ClusterOperationState.Succeeded, (await service.CaptureAsync("demo", new(id), "retry", "test", default)).State);
        Assert.Equal(2, cleanup.Releases);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync("demo",
            new(id, AllowCrashConsistent: true), "different-cut", "test", default));
        Assert.Equal(2, cleanup.Releases);
    }

    private sealed class SnapshotCleanupHandler : HttpMessageHandler
    {
        internal int Releases;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Releases++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new HostEnvelope<object>(1, DateTimeOffset.UtcNow, new { })) };
            response.Headers.Add(HostProtocol.HeaderName, "1");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task PreflightRejectsMissingFleetBeforeAnyMutation()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var service = Service(catalog, handler);
        var request = Request() with { Hosts = [Request().Hosts[0]] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ActivateAsync("demo", request, "missing", "test", default));
        Assert.Empty(handler.Applied);
        Assert.Null(catalog.GetCluster("demo")!.PendingDeploymentHash);
    }

    [Fact]
    public async Task PartialActivationBlocksStartAndCanResumeAfterRestart()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential) { FailSecond = true };
        var service = Service(catalog, handler);
        var failed = await service.ActivateAsync("demo", Request(), "first", "test", default);
        Assert.Equal(ClusterOperationState.Failed, failed.State);
        Assert.Equal(["one"], handler.Applied);
        Assert.Null(catalog.GetCluster("demo")!.ActiveDeployment);
        Assert.NotNull(catalog.GetCluster("demo")!.PendingDeploymentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On));
        using var recovered = Catalog();
        var resumed = Service(recovered, handler);
        handler.FailSecond = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.ActivateAsync("demo",
            Request() with { Revision = "different" }, "different", "test", default));
        var result = await resumed.ActivateAsync("demo", Request(), "resume", "test", default);
        Assert.Equal(ClusterOperationState.Succeeded, result.State);
        Assert.Null(recovered.GetCluster("demo")!.PendingDeploymentHash);
        Assert.Equal("revision", recovered.GetCluster("demo")!.ActiveDeployment!.Revision);
        int writes = handler.Applied.Count;
        await resumed.ActivateAsync("demo", Request(), "lost-catalog-ack", "test", default);
        Assert.Equal(writes, handler.Applied.Count);
        await recovered.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
    }

    [Fact]
    public async Task UpdateCheckpointsSurviveRestartAndOnlyCompleteForExactReadyRevision()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "update-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        var deployment = new ClusterDeploymentService(catalog, client, operations);
        await deployment.ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var gateway = new GatewayHandler();
        ClusterUpdateService UpdateService(ClusterCatalog c) => new(c, new ClusterDeploymentService(c, client, operations),
            new ClusterGatewayClient(new HttpClient(gateway)), client, operations, NullLogger<ClusterUpdateService>.Instance);
        var update = UpdateService(catalog);
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest("revision", "candidate", Request().Hosts.Select(h => h with {
            Activation = h.Activation with { ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                BundleManifestSha256 = new string('b', 64) } }).ToArray());
        var request = new ClusterUpdateRequest(Guid.NewGuid(), candidate);
        handler.Revision = "wrong-preview";
        await Assert.ThrowsAsync<InvalidDataException>(() => update.BeginAsync("demo", request, "bad-preview", "test", default));
        Assert.Equal(DedicatedServerGoalState.On, catalog.GetCluster("demo")!.GoalState);
        Assert.Null(catalog.GetCluster("demo")!.Update);
        handler.Revision = "candidate";
        await update.BeginAsync("demo", request, "begin", "test", default);
        Assert.Equal(DedicatedServerGoalState.Off, catalog.GetCluster("demo")!.GoalState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On));
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Stopping, catalog.GetCluster("demo")!.Update!.Phase);
        // A file-watcher reload reconstructs arrays inside the workflow. CAS compares
        // durable content, not those arrays' object identities.
        var reloaded = JsonSerializer.Deserialize<ClusterDefinition>(JsonSerializer.Serialize(catalog.GetCluster("demo"), Json), Json)!;
        await catalog.RecordUpdateAsync(reloaded, reloaded.Update!, default);
        var changed = reloaded.Clone();
        changed.Update = changed.Update! with { Deployment = candidate with { Revision = "different" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RecordUpdateAsync(changed, changed.Update, default));
        var stopped = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(stopped, new(stopped.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(123, DateTimeOffset.UtcNow)), default);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
        handler.FailSecond = true;
        await update.AdvanceAllAsync(default);
        Assert.NotNull(catalog.GetCluster("demo")!.PendingDeploymentHash);
        using var recovered = Catalog();
        update = UpdateService(recovered);
        handler.FailSecond = false;
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Starting, recovered.GetCluster("demo")!.Update!.Phase);
        Assert.Equal("revision", recovered.GetCluster("demo")!.PreviousDeployment!.Revision);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Starting, recovered.GetCluster("demo")!.Update!.Phase);
        gateway.Ready = true;
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Complete, recovered.GetCluster("demo")!.Update!.Phase);
        Assert.Equal("candidate", recovered.GetCluster("demo")!.ActiveDeployment!.Revision);
        handler.Revision = "revision";
        await update.BeginAsync("demo", new(Guid.NewGuid(), Rollback: true), "rollback", "test", default);
        stopped = recovered.GetCluster("demo")!;
        await recovered.RecordShutdownProofAsync(stopped, new(stopped.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(456, DateTimeOffset.UtcNow)), default);
        await update.AdvanceAllAsync(default);
        await update.AdvanceAllAsync(default);
        Assert.Equal("revision", recovered.GetCluster("demo")!.ActiveDeployment!.Revision);
        Assert.Equal("candidate", recovered.GetCluster("demo")!.PreviousDeployment!.Revision);
        gateway.Revision = "revision";
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Complete, recovered.GetCluster("demo")!.Update!.Phase);
    }

    [Fact]
    public async Task StuckUpdateReportsItsDeadlineAndCanBeAbandonedUnlessHostsAreHalfActivated()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "abandon-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        await new ClusterDeploymentService(catalog, client, operations).ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var update = new ClusterUpdateService(catalog, new ClusterDeploymentService(catalog, client, operations),
            new ClusterGatewayClient(new HttpClient(new GatewayHandler())), client, operations, NullLogger<ClusterUpdateService>.Instance);
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest("revision", "candidate", Request().Hosts.Select(h => h with {
            Activation = h.Activation with { ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                BundleManifestSha256 = new string('b', 64) } }).ToArray());
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin", "test", default);

        // The clean shutdown never arrives: past the deadline the reason is recorded once.
        await update.AdvanceAllAsync(default);
        Assert.Null(catalog.GetCluster("demo")!.Update!.LastError);
        var waiting = catalog.GetCluster("demo")!;
        await catalog.RecordUpdateAsync(waiting, waiting.Update! with { StartedAt = DateTimeOffset.UtcNow - ClusterUpdateService.StoppingDeadline - TimeSpan.FromSeconds(1) }, default);
        await update.AdvanceAllAsync(default);
        Assert.Contains("abandon the update", catalog.GetCluster("demo")!.Update!.LastError);
        Assert.Equal(ClusterUpdatePhase.Stopping, catalog.GetCluster("demo")!.Update!.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On));

        var abandoned = await update.AbandonAsync("demo", "abandon", "operator", default);

        Assert.Equal(ClusterOperationState.Succeeded, abandoned.State);
        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.StartsWith("Abandoned by operator while Stopping", cluster.Update.LastError);
        Assert.Equal("revision", cluster.ActiveDeployment!.Revision);
        Assert.Equal(DedicatedServerGoalState.Off, cluster.GoalState);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On); // the lifecycle tools are unlocked again
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Complete, catalog.GetCluster("demo")!.Update!.Phase);

        // A half-activated fleet must be settled by the deployment's own resume or recovery.
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        await Assert.ThrowsAsync<InvalidOperationException>(() => update.BeginAsync("demo",
            new(Guid.NewGuid(), candidate), "missing-clean-shutdown", "test", default));
        var clean = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(clean, new(clean.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(122, DateTimeOffset.UtcNow)), default);
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin-2", "test", default);
        var stopped = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(stopped, new(stopped.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(123, DateTimeOffset.UtcNow)), default);
        await update.AdvanceAllAsync(default);
        handler.FailSecond = true;
        await update.AdvanceAllAsync(default);
        Assert.NotNull(catalog.GetCluster("demo")!.PendingDeploymentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => update.AbandonAsync("demo", "abandon-2", "operator", default));
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
    }

    private sealed class GatewayHandler : HttpMessageHandler
    {
        internal bool Ready;
        internal string Revision = "candidate";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var status = new CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus("demo", "world",
                CometWorks.ClusterGateway.AdminContract.V1.ClusterPhase.Serving, CometWorks.ClusterGateway.AdminContract.V1.StartupKind.Warm,
                null, null, false, false, [], new(0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new(null, 0, 0, default), [], [], true,
                CometWorks.ClusterGateway.AdminContract.V1.AdminHealth.Healthy, [], DateTimeOffset.UtcNow,
                Ready ? Revision : "revision", Ready);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<object>(1, DateTimeOffset.UtcNow, status)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }

    private ClusterCatalog Catalog()
    {
        Environment.SetEnvironmentVariable(credential, "token");
        var path = Path.Combine(root, "clusters", "demo", "cluster.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, """{"uniqueName":"demo","gatewayUrl":"http://gateway.test"}""");
        return new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
    }

    private ClusterDeploymentService Service(ClusterCatalog catalog, Handler handler) => new(catalog,
        new ClusterHostClient(new HttpClient(handler)), new ClusterOperationStore(Path.Combine(root, "operations")));

    private ClusterDeploymentRequest Request() => new(null, "revision", [Host("one"), Host("two")]);
    private ClusterHostActivation Host(string id) => new(id, "http://" + id, credential,
        new("demo", null, "/" + id + "/bundle.json", new string('a', 64), "http://gateway.test", credential));

    private sealed class Handler(string credential) : HttpMessageHandler
    {
        internal bool FailSecond;
        internal bool Recovery, RefuseRecovery, RefusePreview;
        internal int GatewayStops;
        private readonly DateTimeOffset launched = DateTimeOffset.UtcNow;
        private readonly Guid startGeneration = Guid.NewGuid();
        internal string Revision = "revision";
        internal string? Deletion;
        internal readonly List<string> DeletePreviews = [];
        private readonly Dictionary<string, string> active = [];
        internal readonly List<string> Applied = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string host = request.RequestUri!.Host;
            object data;
            if (Deletion == "host-offline") throw new HttpRequestException("Host unreachable");
            if (Deletion is not null && request.Method == HttpMethod.Get)
                data = new HostStatus(host, host, [], host == "one" ? [new("demo", GatewayGoal.Off,
                    Deletion == "gateway-running" ? GatewayObservedState.Running : GatewayObservedState.Missing,
                    active[host], Revision, [29416], "/runs/one/gateway", null, null, null)] : [], true);
            else if (request.RequestUri.AbsolutePath.Contains("/recovery-readiness/"))
            {
                if (host == "two" && RefuseRecovery) throw new HttpRequestException("nodes still running");
                data = new HostRecoveryReadiness(host, active[host]);
            }
            else if (request.RequestUri.AbsolutePath.Contains("/gateways/"))
            {
                var spec = JsonSerializer.Deserialize<GatewaySpec>(await request.Content!.ReadAsStringAsync(token), Json)!;
                Assert.Equal(GatewayGoal.Off, spec.Goal);
                Assert.Equal(new GatewayStopFence(123, launched), spec.StopFence);
                GatewayStops++;
                data = new GatewayStatus("demo", GatewayGoal.Off, GatewayObservedState.Missing,
                    spec.BundleManifestSha256, spec.ConfigRevision, spec.Ports, spec.RunRoot, null, null, null,
                    spec.StopFence, spec.StartGeneration);
            }
            else if (request.Method == HttpMethod.Get && Recovery)
                data = new HostStatus(host, host, [], [new("demo", GatewayGoal.On, GatewayObservedState.Running,
                    active[host], Revision, [29416], "/runs/one/gateway", 123, launched, null, StartGeneration: startGeneration)], true);
            else if (request.Method == HttpMethod.Get) data = new HostStatus(host, host, active.TryGetValue(host, out var hash)
                ? [new("demo", "http://gateway.test", true, hash, "/runs/" + host)] : []);
            else
            {
                if (Deletion is not null && request.Method == HttpMethod.Post)
                {
                    DeletePreviews.Add(host);
                    if (Deletion == "node-running" && host == "two") throw new HttpRequestException("Node still running");
                }
                if (Recovery && RefusePreview && request.Method == HttpMethod.Post)
                    throw new HttpRequestException("node appeared during Gateway stop");
                if (request.Method == HttpMethod.Put)
                {
                    if (host == "two" && FailSecond) throw new HttpRequestException("lost connection");
                    Applied.Add(host);
                }
                var activation = JsonSerializer.Deserialize<HostDeploymentActivation>(await request.Content!.ReadAsStringAsync(token), Json)!;
                if (request.Method == HttpMethod.Put) active[host] = activation.BundleManifestSha256;
                data = new HostActiveDeployment("demo", Revision, activation.BundleManifestSha256,
                    new("demo", "http://gateway.test", credential, activation.BundleManifestPath, activation.BundleManifestSha256, "/runs/" + host),
                    host == "one" ? new("demo", GatewayGoal.Off, activation.BundleManifestPath, activation.BundleManifestSha256,
                        Revision, [29416], "/runs/one/gateway", StartGeneration: startGeneration) : null, ["one", "two"]);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new HostEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add(HostProtocol.HeaderName, "1");
            return response;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(credential, null);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
