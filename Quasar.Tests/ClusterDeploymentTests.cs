using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CometWorks.ClusterGateway.AdminContract.V1;
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
    public async Task QueuedContentPersistsExactVersionsAndBlocksActivationWithoutChangingShutdownProof()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var deployments = Service(catalog, handler);
        await deployments.ActivateAsync("demo", Request(), "initial", "test", default);
        var cluster = catalog.GetCluster("demo")!;
        var proof = new ClusterShutdownProof(cluster.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(123, DateTimeOffset.UtcNow));
        await catalog.RecordShutdownProofAsync(cluster, proof, default);
        var snapshot = new ClusterContentSnapshot("demo", cluster.ActiveDeployment!.Revision, DateTimeOffset.UtcNow, [],
            [new("mod", "123", "Mod", null, "1700000000", "1700000010", DateTimeOffset.UtcNow, null, null),
             new("plugin", "plugin", "Plugin", new string('a', 40), null, new string('b', 40), DateTimeOffset.UtcNow, null, null)], null, null);
        await catalog.QueueContentUpdatesAsync("demo", snapshot);
        using (var reloaded = Catalog())
        {
            var queued = reloaded.GetCluster("demo")!;
            Assert.Equal(new[] { "1700000010", new string('b', 40) }, queued.QueuedContentUpdates!.Items.Select(i => i.TargetVersion));
            Assert.Equal(proof, queued.ShutdownProof);
            Assert.Equal(cluster.GetLifecycleId(), queued.GetLifecycleId());
        }
        var candidate = Request() with { ExpectedRevision = cluster.ActiveDeployment.Revision, Revision = "candidate" };
        var activationError = await Assert.ThrowsAsync<InvalidOperationException>(() => deployments.ActivateAsync("demo", candidate, "blocked", "test", default));
        Assert.Contains("queued mod/plugin", activationError.Message);
        var operations = new ClusterOperationStore(Path.Combine(root, "queue-operations"));
        using var updates = new ClusterUpdateService(catalog, deployments, null!, null!, operations, NullLogger<ClusterUpdateService>.Instance);
        var updateError = await Assert.ThrowsAsync<InvalidOperationException>(() => updates.BeginAsync("demo", new(Guid.NewGuid(), candidate), "blocked", "test", default));
        Assert.Contains("Queued mod/plugin", updateError.Message);
        Assert.Equal(2, handler.Applied.Count); // Initial activation only; queuing never contacts Hosts.
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.QueueContentUpdatesAsync("demo", snapshot with { DeploymentRevision = "stale" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.QueueContentUpdatesAsync("demo", snapshot with { CheckError = "offline" }));
        await catalog.ClearQueuedContentUpdatesAsync("demo");
        Assert.Null(catalog.GetCluster("demo")!.QueuedContentUpdates);
        Assert.Equal(proof, catalog.GetCluster("demo")!.ShutdownProof);
    }

    [Fact]
    public async Task PreparedContentClearsOnlyTheVerifiedQueueAndUnblocksTheUpdate()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var deployments = Service(catalog, handler);
        await deployments.ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var cluster = catalog.GetCluster("demo")!;
        var snapshot = new ClusterContentSnapshot("demo", cluster.ActiveDeployment!.Revision, DateTimeOffset.UtcNow, [],
            [new("plugin", "plugin", "Plugin", new string('a', 40), null, new string('b', 40), DateTimeOffset.UtcNow, null, null)], null, null);
        cluster = await catalog.QueueContentUpdatesAsync("demo", snapshot);
        var queued = cluster.QueuedContentUpdates!;
        var candidate = Request() with { ExpectedRevision = cluster.ActiveDeployment!.Revision, Revision = "candidate" };
        var preparation = new ClusterPreparationRequest(new string('a', 64), "{}", "http://gateway.test", []);
        await catalog.RecordPreparationAsync(cluster, preparation, candidate, default);
        var changed = queued with { Items = [queued.Items[0] with { TargetVersion = new string('c', 40) }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RecordSelectedReleasePreparationAsync(
            catalog.GetCluster("demo")!, candidate, default, preparedContentUpdates: changed));
        Assert.NotNull(catalog.GetCluster("demo")!.QueuedContentUpdates);
        Assert.False(catalog.GetCluster("demo")!.PreparedForSelectedRelease);
        // Queue arrays reconstructed by reload must compare by durable content.
        using var reloaded = Catalog();
        await reloaded.RecordSelectedReleasePreparationAsync(reloaded.GetCluster("demo")!, candidate, default,
            preparedContentUpdates: queued);
        cluster = reloaded.GetCluster("demo")!;
        Assert.Null(cluster.QueuedContentUpdates);
        Assert.True(cluster.HasPreparedUpdate);
        Assert.True(cluster.PreparedForSelectedRelease);
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);
        Assert.Equal("revision", cluster.ActiveDeployment!.Revision);
        Assert.Equal(2, handler.Applied.Count);
        using var persisted = Catalog();
        Assert.Null(persisted.GetCluster("demo")!.QueuedContentUpdates);
        handler.Revision = "candidate";
        using var updates = new ClusterUpdateService(persisted, Service(persisted, handler), null!, null!,
            new ClusterOperationStore(Path.Combine(root, "content-update-operations")), NullLogger<ClusterUpdateService>.Instance);
        await updates.BeginAsync("demo", new(Guid.NewGuid(), candidate), "prepared-content", "test", default);
        Assert.Equal(ClusterUpdatePhase.Stopping, persisted.GetCluster("demo")!.Update!.Phase);
    }

    [Theory]
    [InlineData("active", "candidate", true)]
    [InlineData("active", "active", false)]
    [InlineData("stale", "candidate", false)]
    public void PendingConfigurationDoesNotRequireANewerPackage(string expected, string prepared, bool pending)
    {
        var cluster = new ClusterDefinition { ActiveDeployment = new("active", [], DateTimeOffset.UtcNow, "1.1.6"),
            PreparedDeployment = new(expected, prepared, []), PreparedForSelectedRelease = false };
        Assert.Equal(pending, cluster.HasPreparedUpdate);
    }

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
    [InlineData("gateway-failed")]
    [InlineData("gateway-conflict")]
    [InlineData("gateway-running-node-running")]
    [InlineData("node-running")]
    [InlineData("host-offline")]
    [InlineData("goal-on")]
    [InlineData("deployment-pending")]
    [InlineData("detach-refused")]
    [InlineData("old-host")]
    public async Task DeleteManagedRequiresStoppedFleet(string state)
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var service = Service(catalog, handler);
        await service.ActivateAsync("demo", Request(), "initial", "test", default);
        handler.Deletion = state;
        if (state == "goal-on") await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        if (state == "deployment-pending") await catalog.RecordPendingDeploymentAsync(catalog.GetCluster("demo")!, "pending", default);
        handler.RefuseRecovery = state == "gateway-running-node-running";
        bool stopsGateway = state is "gateway-running" or "gateway-failed";
        if (state is "stopped" or "detach-refused" or "old-host" || stopsGateway)
        {
            await service.DeleteAsync("demo");
            Assert.Null(catalog.GetCluster("demo"));
            // A Gateway left over from an unclean shutdown is stopped exactly once, with the fence of its process.
            Assert.Equal(stopsGateway ? 1 : 0, handler.GatewayStops);
            Assert.Equal(stopsGateway ? new[] { "one", "two", "one", "two" } : new[] { "one", "two" }, handler.DeletePreviews);
            // Each Host stops supervising the deleted cluster; one that cannot never undoes the deletion.
            Assert.Equal(state == "old-host" ? [] : new[] { "one", "two" }, handler.Detaches);
        }
        else
        {
            var error = await Record.ExceptionAsync(() => service.DeleteAsync("demo"));
            Assert.True(error is InvalidOperationException or ClusterHostException, error?.ToString());
            Assert.NotNull(catalog.GetCluster("demo"));
            Assert.Empty(handler.Detaches);
            Assert.Equal(0, handler.GatewayStops);
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

    [Fact]
    public async Task ScheduledBackupRetriesAFailedCaptureOnlyAfterTheDelay()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential) { FailCaptures = 1 };
        await Service(catalog, handler).ActivateAsync("demo", Request(), "initial", "test", default);
        var cluster = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(cluster, new(cluster.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(123, DateTimeOffset.UtcNow)), default);
        var settings = new QuasarBackupSettings { Server = new() { Enabled = true, Frequency = BackupFrequency.Hourly } };
        var operations = new ClusterOperationStore(Path.Combine(root, "scheduled-operations"));
        ClusterBackupService Backups(TimeSpan retryDelay) => new(catalog, new ClusterHostClient(new HttpClient(handler)), operations, null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "scheduled-backups") }, null!, null!) { ScheduledRetryDelay = retryDelay };

        await Backups(TimeSpan.FromHours(1)).CaptureDueAsync(settings, default);
        var failed = Assert.Single(handler.Captures);
        Assert.Empty(Backups(TimeSpan.FromHours(1)).List("demo"));
        // Within the delay the failed attempt stands: no second capture.
        await Backups(TimeSpan.FromHours(1)).CaptureDueAsync(settings, default);
        Assert.Single(handler.Captures);
        // After it, a new attempt captures the same cut under a new operation key.
        var backups = Backups(TimeSpan.Zero);
        await backups.CaptureDueAsync(settings, default);
        var backup = Assert.Single(backups.List("demo"));
        Assert.Equal(failed.Request.SnapshotId, backup.SnapshotId);
        Assert.True(backup.Automatic);
        Assert.False(backup.Online);
        Assert.Equal(CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.Quiescent, backup.Consistency);
        Assert.All(handler.Captures, c => Assert.False(c.Request.Online));
        // Done for this interval: the next pass captures nothing.
        int captures = handler.Captures.Count;
        await backups.CaptureDueAsync(settings, default);
        Assert.Equal(captures, handler.Captures.Count);
    }

    [Fact]
    public async Task ScheduledBackupCopiesARunningClusterOnlineAfterTheRegistryCopy()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        await Service(catalog, handler).ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var gatewayHandler = new OnlineGatewayHandler(handler);
        var backups = new ClusterBackupService(catalog, new ClusterHostClient(new HttpClient(handler)),
            new ClusterOperationStore(Path.Combine(root, "online-operations")), null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "online-backups") }, null!,
            new ClusterGatewayClient(new HttpClient(gatewayHandler)));
        var settings = new QuasarBackupSettings { Server = new() { Enabled = true }, OnlineClusterMiBPerSecond = 8 };

        await backups.CaptureDueAsync(settings, default);

        var backup = Assert.Single(backups.List("demo"));
        Assert.True(backup.Online);
        Assert.True(backup.Automatic);
        Assert.Equal(CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.CrashConsistent, backup.Consistency);
        // save-all pins the saves, the registry copy comes next, the Hosts copy after it, then the copy is released.
        Assert.Equal(["POST /admin/v1/save-all", "POST /admin/v1/registry-backups", "GET /admin/v1/artifacts/registry-r1",
            "host one", "host two", "DELETE /admin/v1/artifacts/registry-r1"],
            gatewayHandler.Calls.Where(c => !c.StartsWith("GET /admin/v1/capabilities") && !c.StartsWith("GET /admin/v1/status")));
        Assert.Equal(["one", "two"], handler.Captures.Select(c => c.Host));
        Assert.All(handler.Captures, c => Assert.True(c.Request.Online && c.Request.BytesPerSecond == 8L * 1024 * 1024));
        Assert.Equal(("registry-r1", OnlineGatewayHandler.RegistrySha256),
            (handler.Captures[0].Request.RegistryArtifactId, handler.Captures[0].Request.RegistrySha256));
        Assert.Null(handler.Captures[1].Request.RegistryArtifactId);
        Assert.NotEqual(catalog.GetCluster("demo")!.GetLifecycleId(), handler.Captures[0].Request.CaptureFence);
    }

    [Fact]
    public async Task OnlineBackupRefusesAGatewayThatCannotCopyItsRegistry()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        await Service(catalog, handler).ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var gatewayHandler = new OnlineGatewayHandler(handler) { RegistryBackups = false };
        var backups = new ClusterBackupService(catalog, new ClusterHostClient(new HttpClient(handler)),
            new ClusterOperationStore(Path.Combine(root, "old-gateway-operations")), null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "old-gateway-backups") }, null!,
            new ClusterGatewayClient(new HttpClient(gatewayHandler)));
        var request = new ClusterBackupRequest(Guid.NewGuid(), AllowCrashConsistent: true, Online: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backups.CaptureAsync("demo", request, "old", "test", 1024, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backups.CaptureAsync("demo",
            request with { SnapshotId = Guid.NewGuid(), AllowCrashConsistent = false }, "implicit", "test", 1024, default));
        Assert.DoesNotContain(gatewayHandler.Calls, c => c.StartsWith("POST"));
        Assert.Empty(handler.Captures);
    }

    // #1397: one pre-update backup per managed update used to pile up forever.
    [Fact]
    public async Task UpdateBackupsKeepOnlyTheNewestFewAndNothingElseIsPruned()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        await Service(catalog, handler).ActivateAsync("demo", Request(), "initial", "test", default);
        var cluster = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(cluster, new(cluster.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(123, DateTimeOffset.UtcNow)), default);
        cluster = catalog.GetCluster("demo")!;
        string backupRoot = Path.Combine(root, "purpose-backups");
        var backups = new ClusterBackupService(catalog, new ClusterHostClient(new HttpClient(handler)),
            new ClusterOperationStore(Path.Combine(root, "purpose-operations")), null!,
            new WebServiceOptions { BackupDirectory = backupRoot }, null!, null!);
        string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        Guid Write(int hour, string? purpose, bool automatic = false)
        {
            var id = Guid.NewGuid();
            string directory = Path.Combine(backupRoot, "Clusters", Hash("demo"), id.ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "backup.json"), JsonSerializer.Serialize(new ClusterBackup(id, "demo", start.AddHours(hour),
                CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.Quiescent, cluster, [], automatic, Purpose: purpose), Json));
            return id;
        }
        Guid manual = Write(0, null), conversion = Write(1, "conversion"), scheduled = Write(2, null, automatic: true);
        Guid[] updates = [Write(3, "update"), Write(4, "update"), Write(5, "update"), Write(6, "update")];
        var taken = await backups.CaptureCoreAsync(cluster, new(Guid.NewGuid()), default, purpose: "update");
        Assert.Equal("update", taken.Purpose);
        Assert.Equal(new[] { taken.SnapshotId, updates[3], updates[2], scheduled, conversion, manual },
            backups.List("demo").Select(b => b.SnapshotId));
        // A clock step back: three older backups dated after the one just taken. It is still kept.
        Guid[] future = [Write(48, "update"), Write(49, "update"), Write(50, "update")];
        var stepped = await backups.CaptureCoreAsync(cluster, new(Guid.NewGuid()), default, purpose: "update");
        Assert.Equal(new[] { future[2], future[1], stepped.SnapshotId, scheduled, conversion, manual },
            backups.List("demo").Select(b => b.SnapshotId));
    }

    [Fact]
    public async Task RetentionKeepsTheNewestQuiescentBackupBeyondTheCount()
    {
        using var catalog = Catalog();
        await Service(catalog, new Handler(credential)).ActivateAsync("demo", Request(), "initial", "test", default);
        var cluster = catalog.GetCluster("demo")!;
        string backupRoot = Path.Combine(root, "retention-backups");
        var backups = new ClusterBackupService(catalog, null!, new ClusterOperationStore(Path.Combine(root, "retention-operations")),
            null!, new WebServiceOptions { BackupDirectory = backupRoot }, null!, null!);
        string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        Guid Write(int hour, bool quiescent, bool automatic = true)
        {
            var id = Guid.NewGuid();
            string directory = Path.Combine(backupRoot, "Clusters", Hash("demo"), id.ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "backup.json"), JsonSerializer.Serialize(new ClusterBackup(id, "demo", start.AddHours(hour),
                quiescent ? CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.Quiescent : CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.CrashConsistent,
                cluster, [], automatic, Online: !quiescent), Json));
            return id;
        }
        Guid oldClean = Write(0, true), newestClean = Write(1, true), manual = Write(2, true, automatic: false);
        Guid[] online = [Write(3, false), Write(4, false), Write(5, false)];
        backups.Prune("demo", 2);
        Assert.Equal(new[] { online[2], online[1], manual, newestClean }, backups.List("demo").Select(b => b.SnapshotId));
        Assert.DoesNotContain(oldClean, backups.List("demo").Select(b => b.SnapshotId));
    }

    // A Gateway serving the online backup steps; host captures are logged into the same call order.
    private sealed class OnlineGatewayHandler : HttpMessageHandler
    {
        internal const string RegistrySha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        internal bool RegistryBackups = true;
        internal readonly List<string> Calls = [];
        internal OnlineGatewayHandler(Handler hosts) => hosts.CaptureObserved = host => Calls.Add("host " + host);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method + " " + path);
            AdminOperation Done(string kind, Dictionary<string, string> result) => new(kind + "-1", kind, AdminOperationState.Succeeded, "quasar",
                request.Headers.GetValues("Idempotency-Key").Single(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, result, null);
            object data = path switch
            {
                "/admin/v1/capabilities" => new AdminCapabilities("gateway", AdminScope.Manage,
                    RegistryBackups ? [.. AdminProtocol.Operations, "POST /admin/v1/registry-backups"] : AdminProtocol.Operations, new Dictionary<string, string>()),
                "/admin/v1/status" => new ClusterStatus("demo", "world", ClusterPhase.Serving, StartupKind.Warm,
                    null, null, false, false, [], new(0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new(null, 0, 0, default), [], [], true,
                    AdminHealth.Healthy, [], DateTimeOffset.UtcNow, "revision", true),
                "/admin/v1/save-all" => Done("save-all", new() { ["snapshotId"] = "save-all-1" }),
                "/admin/v1/registry-backups" => Done("registry-backup", new() { ["artifactId"] = "registry-r1" }),
                "/admin/v1/artifacts/registry-r1" when request.Method == HttpMethod.Get => new ArtifactDescriptor("registry-r1",
                    "ClusterRegistrySqlite", "save-all-1", ExportConsistency.CrashConsistent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(6),
                    4096, new string('a', 64), new Dictionary<string, string> { ["registry.db"] = RegistrySha256 },
                    new(ArtifactRetrievalKind.SharedPath, "/runs/one/gateway/exports/registry-r1"), []),
                "/admin/v1/artifacts/registry-r1" => Done("artifact-release", new()),
                _ => throw new InvalidOperationException("Unexpected Gateway call " + path),
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AdminEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
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
    public async Task ActivationIsProvenStoppedUntilTheGatewayFirstStarts()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var service = Service(catalog, handler);
        await service.ActivateAsync("demo", Request(), "first", "test", default);
        var first = catalog.GetCluster("demo")!;
        Assert.True(first.HasActivationProof());
        Assert.Null(first.ActivationProof!.CleanShutdownAt);
        Assert.True(first.IsProvenStopped());
        Assert.False(first.IsProvenStopped(requireRun: true));
        var recover = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecoverGatewayAsync("demo", "recover", "test", default)
        );
        Assert.Contains("normal start", recover.Message);

        // A second activation before the first start no longer needs a clean Down that never happened.
        handler.Revision = "second";
        var second = await service.ActivateAsync(
            "demo",
            Next("revision", "second", 'a', 'b'),
            "second",
            "test",
            default
        );
        Assert.Equal(ClusterOperationState.Succeeded, second.State);
        var activated = catalog.GetCluster("demo")!;
        Assert.Equal("second", activated.ActiveDeployment!.Revision);
        Assert.True(activated.HasActivationProof());
        Assert.Null(activated.ActivationProof!.CleanShutdownAt);

        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        Assert.Null(catalog.GetCluster("demo")!.ActivationProof);
        var stopped = await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        Assert.False(stopped.IsProvenStopped());
        var clean = new ClusterShutdownProof(
            stopped.GetLifecycleId(),
            DateTimeOffset.UnixEpoch,
            new GatewayStopFence(123, DateTimeOffset.UtcNow)
        );
        await catalog.RecordShutdownProofAsync(stopped, clean, default);

        // After a clean Down the record keeps the time of that Down, so conversion may use it.
        handler.Revision = "third";
        await service.ActivateAsync(
            "demo",
            Next("second", "third", 'b', 'c'),
            "third",
            "test",
            default
        );
        var third = catalog.GetCluster("demo")!;
        Assert.Null(third.ShutdownProof);
        Assert.Equal(DateTimeOffset.UnixEpoch, third.ActivationProof!.CleanShutdownAt);
        Assert.True(third.IsProvenStopped(requireRun: true));
    }

    [Fact]
    public async Task UpdateOfADeploymentThatNeverRanActivatesWithoutAShutdown()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "update-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        var deployment = new ClusterDeploymentService(catalog, client, operations);
        await deployment.ActivateAsync("demo", Request(), "initial", "test", default);
        var update = new ClusterUpdateService(
            catalog,
            deployment,
            new ClusterGatewayClient(new HttpClient(new GatewayHandler())),
            client,
            operations,
            NullLogger<ClusterUpdateService>.Instance,
            // SE-783: it has no world to back up, so a capture would hold the update back
            new ClusterBackupService(catalog, client, operations, null!,
                new WebServiceOptions { BackupDirectory = Path.Combine(root, "never-ran-backups") }, null!, null!)
        );
        handler.Revision = "candidate";

        await update.BeginAsync(
            "demo",
            new(Guid.NewGuid(), Next("revision", "candidate", 'a', 'b')),
            "begin",
            "test",
            default
        );
        await update.AdvanceAllAsync(default);
        await update.AdvanceAllAsync(default);

        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal("candidate", cluster.ActiveDeployment!.Revision);
        Assert.Equal(ClusterUpdatePhase.Starting, cluster.Update!.Phase);
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);
        Assert.Null(cluster.ActivationProof);
        Assert.Equal(0, handler.GatewayStops);
        Assert.Null(cluster.Update.Backup);
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

    [Fact]
    public async Task UpdateDespiteADirtyShutdownFencesTheStopBacksUpAndStartsARecoveryUpdate()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "dirty-update-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        await new ClusterDeploymentService(catalog, client, operations).ActivateAsync("demo", Request(), "initial", "test", default);
        // It ran and stopped dirty: no clean shutdown proof, the Gateway still runs, Down.
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        Assert.False(catalog.GetCluster("demo")!.IsProvenStopped());
        handler.StartGeneration = catalog.GetCluster("demo")!.Gateway!.StartGeneration!.Value;
        handler.Recovery = true;
        var backups = new ClusterBackupService(catalog, client, operations, null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "dirty-update-backups") }, null!, null!);
        var gateway = new DirtyGatewayHandler(["partition:836", "node:local-node-0"]);
        var update = new ClusterUpdateService(catalog, new ClusterDeploymentService(catalog, client, operations),
            new ClusterGatewayClient(new HttpClient(gateway)), client, operations, NullLogger<ClusterUpdateService>.Instance, backups);
        handler.Revision = "candidate";
        var candidate = Next("revision", "candidate", 'a', 'b');
        string[] shown = await update.GetRecordedDirtyShutdownAsync("demo", default);
        Assert.Equal(["node:local-node-0", "partition:836"], shown);

        var plain = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "plain", "test", default));
        Assert.Contains("update it despite the recorded dirty shutdown", plain.Message);
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => update.BeginAsync("demo",
            new(Guid.NewGuid(), candidate, AcceptDirtyShutdown: ["node:local-node-0"]), "stale", "test", default));
        Assert.Contains("It now lists: node:local-node-0, partition:836.", stale.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => update.BeginAsync("demo",
            new(Guid.NewGuid(), Rollback: true, AcceptDirtyShutdown: shown), "rollback", "test", default));
        // The candidate's release must accept a recovery start with a new revision; without a known package it is refused.
        var release = await Assert.ThrowsAsync<InvalidOperationException>(() => update.BeginAsync("demo",
            new(Guid.NewGuid(), candidate, AcceptDirtyShutdown: shown), "release", "test", default));
        Assert.Contains("prepared from the selected cluster release", release.Message);
        Assert.Null(catalog.GetCluster("demo")!.Update);

        update.RequireRecoveryUpdateAsync = (_, _, _) => Task.CompletedTask;
        var id = Guid.NewGuid();
        await update.BeginAsync("demo", new(id, candidate, AcceptDirtyShutdown: ["partition:836", "node:local-node-0"]), "begin", "test", default);
        Assert.Equal(shown, catalog.GetCluster("demo")!.Update!.DirtyShutdown);

        // Stopping: the fenced stop is checkpointed, so a retry after a later failure does not stop again.
        handler.Revision = "revision";
        await update.AdvanceAllAsync(default);
        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Stopping, cluster.Update!.Phase);
        Assert.True(cluster.Update.FencedStop);
        Assert.Equal(1, handler.GatewayStops);
        Assert.NotNull(cluster.Update.LastError);
        handler.Revision = "candidate";
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Activating, cluster.Update!.Phase);
        Assert.Equal(1, handler.GatewayStops);
        Assert.Equal(id, cluster.Update.Backup);
        Assert.Equal(CometWorks.ClusterGateway.AdminContract.V1.ExportConsistency.CrashConsistent,
            Assert.Single(backups.List("demo")).Consistency);

        // Activating: the new deployment's first start is a recovery start that may change the revision.
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Starting, cluster.Update!.Phase);
        Assert.Equal("candidate", cluster.ActiveDeployment!.Revision);
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);
        Assert.True(cluster.Gateway!.Recover && cluster.Gateway.RecoveryUpdate);

        // An ordinary start later clears both flags.
        await update.AbandonAsync("demo", "abandon", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        Assert.False(catalog.GetCluster("demo")!.Gateway!.Recover || catalog.GetCluster("demo")!.Gateway!.RecoveryUpdate);
    }

    // A stopped Gateway, Down, whose last shutdown's CleanShutdown snapshot lists dirty state.
    private sealed class DirtyGatewayHandler(string[] dirty) : HttpMessageHandler
    {
        private readonly DateTimeOffset shutdown = DateTimeOffset.UtcNow;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object data = request.RequestUri!.AbsolutePath.EndsWith("/snapshots")
                ? new[] { new CometWorks.ClusterGateway.AdminContract.V1.SnapshotSummary("s1",
                    CometWorks.ClusterGateway.AdminContract.V1.AdminSnapshotKind.CleanShutdown, shutdown.UtcTicks, 3, true, 0, dirty) }
                : new CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus("demo", "world",
                    CometWorks.ClusterGateway.AdminContract.V1.ClusterPhase.Down, CometWorks.ClusterGateway.AdminContract.V1.StartupKind.Warm,
                    shutdown, shutdown, false, false, [], new(0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new(null, 0, 0, default), [], [], false,
                    CometWorks.ClusterGateway.AdminContract.V1.AdminHealth.Healthy, [], DateTimeOffset.UtcNow, "revision", false);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new CometWorks.ClusterGateway.AdminContract.V1.AdminEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task FailedUpdateKeepsThePreviousVersionOrNamesItsBackup()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "timeout-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        await new ClusterDeploymentService(catalog, client, operations).ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var backups = new ClusterBackupService(catalog, client, operations, null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "update-backups") }, null!, null!);
        var update = new ClusterUpdateService(catalog, new ClusterDeploymentService(catalog, client, operations),
            new ClusterGatewayClient(new HttpClient(new GatewayHandler())), client, operations, NullLogger<ClusterUpdateService>.Instance, backups);
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest("revision", "candidate", Request().Hosts.Select(h => h with {
            Activation = h.Activation with { ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                BundleManifestSha256 = new string('b', 64) } }).ToArray());
        async Task Backdate(TimeSpan age)
        {
            var current = catalog.GetCluster("demo")!;
            var past = DateTimeOffset.UtcNow - age - TimeSpan.FromSeconds(1);
            await catalog.RecordUpdateAsync(current, current.Update! with { StartedAt = past, PhaseStartedAt = past }, default);
        }
        async Task ProveCleanShutdown(int pid)
        {
            var stopped = catalog.GetCluster("demo")!;
            await catalog.RecordShutdownProofAsync(stopped, new(stopped.GetLifecycleId(), DateTimeOffset.UtcNow,
                new GatewayStopFence(pid, DateTimeOffset.UtcNow)), default);
        }

        // Stopping never finishes while the old Gateway still runs: the previous version carries on.
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin-1", "test", default);
        Assert.Equal(DedicatedServerGoalState.Off, catalog.GetCluster("demo")!.GoalState);
        await Backdate(ClusterUpdateService.StoppingTimeout);
        handler.Recovery = true; // Host reports the Gateway running
        await update.AdvanceAllAsync(default);
        handler.Recovery = false;
        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.StartsWith("Update failed after 60 minutes in Stopping: still waiting for a verified clean shutdown (if it ended dirty, abandon this update", cluster.Update.LastError);
        Assert.EndsWith("stays on its current deployment and its goal is On again.", cluster.Update.LastError);
        Assert.Equal("revision", cluster.ActiveDeployment!.Revision);
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);

        // The same, but the Gateway is gone without clean proof: it must not start unverified.
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin-2", "test", default);
        await Backdate(ClusterUpdateService.StoppingTimeout);
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.EndsWith("stays stopped: its Gateway has no clean shutdown proof; recover it in Maintenance → Recovery.", cluster.Update.LastError);
        Assert.Equal(DedicatedServerGoalState.Off, cluster.GoalState);

        // Activating: every Host refuses before anything was applied; a cluster stopped before the update stays stopped.
        await ProveCleanShutdown(121);
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin-3", "test", default);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
        handler.Deletion = "host-offline";
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
        Assert.Null(catalog.GetCluster("demo")!.PendingDeploymentHash);
        await Backdate(ClusterUpdateService.ActivatingTimeout);
        await update.AdvanceAllAsync(default);
        handler.Deletion = null;
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.StartsWith("Update failed after 30 minutes in Activating: Cannot reach the Host executor", cluster.Update.LastError);
        Assert.EndsWith("stays stopped: it was stopped before the update.", cluster.Update.LastError);
        Assert.Equal("revision", cluster.ActiveDeployment!.Revision);
        Assert.Equal(DedicatedServerGoalState.Off, cluster.GoalState);

        // Activating, half applied for a while: resuming still works (the revert after its timeout is tested separately).
        await ProveCleanShutdown(122);
        var id = Guid.NewGuid();
        await update.BeginAsync("demo", new(id, candidate), "begin-4", "test", default);
        await update.AdvanceAllAsync(default);
        Assert.Equal(id, catalog.GetCluster("demo")!.Update!.Backup);
        Assert.Single(backups.List("demo"), b => b.SnapshotId == id);
        handler.FailSecond = true;
        await update.AdvanceAllAsync(default);
        Assert.NotNull(catalog.GetCluster("demo")!.PendingDeploymentHash);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
        handler.FailSecond = false;
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Starting, catalog.GetCluster("demo")!.Update!.Phase);

        // Starting: the new deployment never serves, so the update reverts from its pre-update backup.
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Starting, catalog.GetCluster("demo")!.Update!.Phase);
        await Backdate(ClusterUpdateService.StartingTimeout);
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Reverting, cluster.Update!.Phase);
        Assert.StartsWith(
            "Update failed after 60 minutes in Starting: still waiting for the Gateway",
            cluster.Update.Failure
        );
        Assert.EndsWith(
            $"Reverting to the previous version and world from backup {id:D}.",
            cluster.Update.LastError
        );
        Assert.Equal(DedicatedServerGoalState.Off, cluster.GoalState);

        // A revert that cannot even stop the failed deployment gives up and names the backup.
        await Backdate(ClusterUpdateService.RevertingTimeout);
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.StartsWith("Update failed after 60 minutes in Starting", cluster.Update.LastError);
        Assert.Contains("The revert did not finish within 3 hours", cluster.Update.LastError);
        Assert.EndsWith($"restore backup {id:D} in Maintenance.", cluster.Update.LastError);
        Assert.Equal("candidate", cluster.ActiveDeployment!.Revision);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On); // the lifecycle tools are unlocked again
    }

    [Fact]
    public async Task FailedStartRevertsFromThePreUpdateBackupAndStartsThePreviousVersion()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "revert-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        var deployments = new ClusterDeploymentService(catalog, client, operations);
        await deployments.ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var backups = new ClusterBackupService(
            catalog,
            client,
            operations,
            null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "revert-backups") },
            null!,
            null!
        );
        var gateway = new GatewayHandler();
        var update = new ClusterUpdateService(
            catalog,
            deployments,
            new ClusterGatewayClient(new HttpClient(gateway)),
            client,
            operations,
            NullLogger<ClusterUpdateService>.Instance,
            backups
        );
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest(
            "revision",
            "candidate",
            Request()
                .Hosts.Select(h =>
                    h with
                    {
                        Activation = h.Activation with
                        {
                            ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                            BundleManifestSha256 = new string('b', 64),
                        },
                    }
                )
                .ToArray()
        );
        // The restore path itself (fresh preparation, snapshot restore) is the operator's; here it re-activates
        // a restored revision so the update's own steps can be checked end to end.
        int prepared = 0;
        update.PrepareRevertAsync = (cluster, workflow, token) =>
        {
            prepared++;
            return Task.FromResult(
                new ClusterRestoreRequest(
                    workflow.Backup!.Value,
                    workflow.RevertRestoreId!.Value,
                    new(
                        "candidate",
                        "restored",
                        candidate
                            .Hosts.Select(h =>
                                h with
                                {
                                    Activation = h.Activation with
                                    {
                                        ExpectedBundleManifestSha256 =
                                            h.Activation.BundleManifestSha256,
                                        BundleManifestSha256 = new string('c', 64),
                                    },
                                }
                            )
                            .ToArray()
                    )
                )
            );
        };
        update.ApplyRevertAsync = async (cluster, workflow, token) =>
        {
            handler.Recovery = false;
            handler.GatewayStopped = true;
            handler.Revision = "restored";
            await deployments.ActivateCoreAsync(
                cluster,
                workflow.Revert!.Deployment,
                token,
                restore: true,
                update: true
            );
        };

        var id = Guid.NewGuid();
        await update.BeginAsync("demo", new(id, candidate), "begin", "test", default);
        var stopped = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(
            stopped,
            new(
                stopped.GetLifecycleId(),
                DateTimeOffset.UtcNow,
                new GatewayStopFence(123, DateTimeOffset.UtcNow)
            ),
            default
        );
        await update.AdvanceAllAsync(default);
        await update.AdvanceAllAsync(default);
        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Starting, cluster.Update!.Phase);
        Assert.Equal(id, cluster.Update.Backup);
        async Task Backdate(TimeSpan age)
        {
            var current = catalog.GetCluster("demo")!;
            await catalog.RecordUpdateAsync(
                current,
                current.Update! with
                {
                    PhaseStartedAt = DateTimeOffset.UtcNow - age - TimeSpan.FromSeconds(1),
                },
                default
            );
        }
        await Backdate(ClusterUpdateService.StartingTimeout);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Reverting, catalog.GetCluster("demo")!.Update!.Phase);

        // First the reconciler gets its chance to stop the failed deployment cleanly.
        await update.AdvanceAllAsync(default);
        Assert.Equal(0, prepared);
        Assert.Equal(0, handler.GatewayStops);

        // Then a fenced stop: the Gateway runs the generation the update started, and every node is down.
        await Backdate(ClusterUpdateService.RevertStopGrace);
        handler.Recovery = true;
        handler.StartGeneration = catalog.GetCluster("demo")!.Gateway!.StartGeneration!.Value;
        await update.AdvanceAllAsync(default);
        Assert.Equal(1, handler.GatewayStops);
        Assert.Equal(1, prepared);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Reverting, cluster.Update!.Phase);
        Assert.Equal("restored", cluster.ActiveDeployment!.Revision);
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);
        Assert.EndsWith(
            $"Reverted to the previous version and world from backup {id:D}. Starting it again.",
            cluster.Update.LastError
        );

        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Reverting, catalog.GetCluster("demo")!.Update!.Phase);
        gateway.Revision = "restored";
        gateway.Ready = true;
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.Equal(
            $"{cluster.Update.Failure} Reverted to the previous version and world from backup {id:D}.",
            cluster.Update.LastError
        );
        Assert.Equal(1, prepared);
    }

    [Fact]
    public async Task HalfAppliedActivationRevertsFromThePreUpdateBackup()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "half-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        var deployments = new ClusterDeploymentService(catalog, client, operations);
        await deployments.ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var backups = new ClusterBackupService(
            catalog,
            client,
            operations,
            null!,
            new WebServiceOptions { BackupDirectory = Path.Combine(root, "half-backups") },
            null!,
            null!
        );
        var gateway = new GatewayHandler();
        var update = new ClusterUpdateService(
            catalog,
            deployments,
            new ClusterGatewayClient(new HttpClient(gateway)),
            client,
            operations,
            NullLogger<ClusterUpdateService>.Instance,
            backups
        );
        string previous = Request().Hosts[0].Activation.BundleManifestSha256;
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest(
            "revision",
            "candidate",
            Request()
                .Hosts.Select(h =>
                    h with
                    {
                        Activation = h.Activation with
                        {
                            ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                            BundleManifestSha256 = new string('b', 64),
                        },
                    }
                )
                .ToArray()
        );
        // As a fresh preparation would: every Host is expected to run the recorded (previous) deployment.
        update.PrepareRevertAsync = (cluster, workflow, token) =>
            Task.FromResult(
                new ClusterRestoreRequest(
                    workflow.Backup!.Value,
                    workflow.RevertRestoreId!.Value,
                    new(
                        "revision",
                        "restored",
                        Request()
                            .Hosts.Select(h =>
                                h with
                                {
                                    Activation = h.Activation with
                                    {
                                        ExpectedBundleManifestSha256 = previous,
                                        BundleManifestSha256 = new string('c', 64),
                                    },
                                }
                            )
                            .ToArray()
                    )
                )
            );
        update.ApplyRevertAsync = async (cluster, workflow, token) =>
        {
            handler.GatewayStopped = true;
            handler.Revision = "restored";
            await deployments.ActivateCoreAsync(
                cluster,
                workflow.Revert!.Deployment,
                token,
                restore: true,
                update: true,
                supersede: true
            );
        };

        var id = Guid.NewGuid();
        await update.BeginAsync("demo", new(id, candidate), "begin", "test", default);
        var stopped = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(
            stopped,
            new(
                stopped.GetLifecycleId(),
                DateTimeOffset.UtcNow,
                new GatewayStopFence(123, DateTimeOffset.UtcNow)
            ),
            default
        );
        await update.AdvanceAllAsync(default);
        handler.FailSecond = true; // Host one switches to the new bundle, Host two never does
        await update.AdvanceAllAsync(default);
        var cluster = catalog.GetCluster("demo")!;
        Assert.NotNull(cluster.PendingDeploymentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            update.AbandonAsync("demo", "abandon", "operator", default)
        );

        var current = catalog.GetCluster("demo")!;
        await catalog.RecordUpdateAsync(
            current,
            current.Update! with
            {
                PhaseStartedAt =
                    DateTimeOffset.UtcNow
                    - ClusterUpdateService.ActivatingTimeout
                    - TimeSpan.FromSeconds(1),
            },
            default
        );
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Reverting, cluster.Update!.Phase);
        Assert.StartsWith("Update failed after 30 minutes in Activating", cluster.Update.Failure);

        // No process ran, so the clean proof still holds and no stop is needed. Each Host is restored from the bundle it runs.
        handler.FailSecond = false;
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(0, handler.GatewayStops);
        var expected = cluster.Update!.Revert!.Deployment.Hosts.ToDictionary(
            h => h.HostId,
            h => h.Activation.ExpectedBundleManifestSha256
        );
        Assert.Equal(new string('b', 64), expected["one"]);
        Assert.Equal(previous, expected["two"]);
        Assert.Null(cluster.PendingDeploymentHash);
        Assert.Equal("restored", cluster.ActiveDeployment!.Revision);
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);

        gateway.Revision = "restored";
        gateway.Ready = true;
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.EndsWith(
            $"Reverted to the previous version and world from backup {id:D}.",
            cluster.Update.LastError
        );
    }

    [Fact]
    public async Task NodesFailingAtStartupSayWhyAndEndTheUpdateEarly()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "failing-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        await new ClusterDeploymentService(catalog, client, operations).ActivateAsync("demo", Request(), "initial", "test", default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var gateway = new GatewayHandler();
        var update = new ClusterUpdateService(catalog, new ClusterDeploymentService(catalog, client, operations),
            new ClusterGatewayClient(new HttpClient(gateway)), client, operations, NullLogger<ClusterUpdateService>.Instance);
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest("revision", "candidate", Request().Hosts.Select(h => h with {
            Activation = h.Activation with { ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                BundleManifestSha256 = new string('b', 64) } }).ToArray());
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin", "test", default);
        var stopped = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(stopped, new(stopped.GetLifecycleId(), DateTimeOffset.UtcNow,
            new GatewayStopFence(124, DateTimeOffset.UtcNow)), default);
        await update.AdvanceAllAsync(default);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Starting, catalog.GetCluster("demo")!.Update!.Phase);

        // Nothing failed yet: a slow start is only waited for.
        NodePlan Slot(string key, NodeObservation observed, string? failure, bool alert = false) => new(key, "one", NodeRole.Regular,
            NodeGoal.Wanted, observed, null, IncumbentAction.None, null, 0, null, null, failure, null, 1, alert, false);
        gateway.Plan = [Slot("a", NodeObservation.Spawning, null), Slot("b", NodeObservation.Spawning, null)];
        await update.AdvanceAllAsync(default);
        Assert.Null(catalog.GetCluster("demo")!.Update!.LastError);

        // One slot alerts, the other runs: it is named, but the cluster may still come up, so the long timeout holds.
        gateway.Plan = [Slot("a", NodeObservation.Failed, "ready_timeout", alert: true), Slot("b", NodeObservation.Ready, null)];
        await update.AdvanceAllAsync(default);
        Assert.Contains("a: ready_timeout.", catalog.GetCluster("demo")!.Update!.LastError);
        async Task Backdate(TimeSpan age)
        {
            var current = catalog.GetCluster("demo")!;
            await catalog.RecordUpdateAsync(current, current.Update! with { PhaseStartedAt = DateTimeOffset.UtcNow - age - TimeSpan.FromSeconds(1) }, default);
        }
        await Backdate(ClusterUpdateService.NodesFailingTimeout);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Starting, catalog.GetCluster("demo")!.Update!.Phase);

        // Every node failed the same way: the Registry halts spawns. The operator reads why, and how to get out.
        await Backdate(TimeSpan.Zero);
        gateway.SpawnHalted = true;
        gateway.Plan = [Slot("a", NodeObservation.Failed, "process_exited"), Slot("b", NodeObservation.Failed, "process_exited")];
        handler.NodeFailures = [new("demo", "a", "process_exited", "System.InvalidOperationException: boom at Preloader.Fail()", "/run/a/config/info.log"),
            new("demo", "b", "process_exited", "System.InvalidOperationException: boom at Preloader.Fail()", "/run/b/config/info.log"),
            new("other", "a", "process_exited", "not this cluster", null)];
        await update.AdvanceAllAsync(default);
        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Starting, cluster.Update!.Phase);
        Assert.StartsWith("Nodes do not start. a, b: process_exited (System.InvalidOperationException: boom at Preloader.Fail()). Node log: /run/a/config/info.log.",
            cluster.Update.LastError);
        string refused = cluster.BusyMessage("changing profiles");
        Assert.StartsWith("Finish the current setup, update or activation before changing profiles.", refused);
        Assert.Contains("boom", refused);
        Assert.Contains("cluster update-abandon", refused);
        DateTimeOffset recorded = cluster.Update.UpdatedAt;
        await update.AdvanceAllAsync(default);
        Assert.Equal(recorded, catalog.GetCluster("demo")!.Update!.UpdatedAt);

        // The retry failed too. Without a backup there is nothing to revert to, so the cluster keeps the new deployment.
        await Backdate(ClusterUpdateService.NodesFailingTimeout);
        await update.AdvanceAllAsync(default);
        cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.StartsWith("Update failed after 12 minutes in Starting: Nodes do not start. a, b: process_exited (System.InvalidOperationException: boom",
            cluster.Update.LastError);
        Assert.EndsWith("The cluster keeps the new deployment. Start a rollback update if it cannot serve.", cluster.Update.LastError);
    }

    [Fact]
    public void NodeFailureCauseIsTheInnermostException()
    {
        Exception thrown;
        try { try { throw new InvalidOperationException("boom"); } catch (Exception inner) { throw new TypeInitializationException("Preloader", inner); } }
        catch (Exception outer) { thrown = outer; }
        string summary = Quasar.Diagnostics.DiagnosticCollector.Summarize(thrown.ToString());
        Assert.StartsWith("System.InvalidOperationException: boom at ", summary);
        Assert.Contains(nameof(NodeFailureCauseIsTheInnermostException), summary);
        Assert.Equal("System.Exception: plain", Quasar.Diagnostics.DiagnosticCollector.Summarize("System.Exception: plain"));
        Assert.EndsWith("…", Quasar.Diagnostics.DiagnosticCollector.Summarize(new string('x', 900)));
    }

    [Fact]
    public async Task EmergencyShutdownEndsAnUpdateOnlyWhileItStopsOrStarts()
    {
        using var catalog = Catalog();
        var handler = new Handler(credential);
        var operations = new ClusterOperationStore(Path.Combine(root, "emergency-operations"));
        var client = new ClusterHostClient(new HttpClient(handler));
        await new ClusterDeploymentService(catalog, client, operations).ActivateAsync(
            "demo",
            Request(),
            "initial",
            "test",
            default
        );
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        var gatewayHandler = new GatewayHandler();
        var gateway = new ClusterGatewayClient(new HttpClient(gatewayHandler));
        var update = new ClusterUpdateService(
            catalog,
            new ClusterDeploymentService(catalog, client, operations),
            gateway,
            client,
            operations,
            NullLogger<ClusterUpdateService>.Instance
        );
        // Own store: a pending emergency shutdown would rightly hold back the second update's activation.
        var commands = new ClusterCommandService(
            catalog,
            new ClusterOperationStore(Path.Combine(root, "emergency-commands")),
            gateway,
            client
        );
        handler.Revision = "candidate";
        var candidate = new ClusterDeploymentRequest(
            "revision",
            "candidate",
            Request()
                .Hosts.Select(h =>
                    h with
                    {
                        Activation = h.Activation with
                        {
                            ExpectedBundleManifestSha256 = h.Activation.BundleManifestSha256,
                            BundleManifestSha256 = new string('b', 64),
                        },
                    }
                )
                .ToArray()
        );
        ClusterAdminCommand Shutdown(bool emergency) =>
            ClusterAdminCommand.Create(
                "shutdown",
                new CometWorks.ClusterGateway.AdminContract.V1.ShutdownRequest(Emergency: emergency)
            );

        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin-1", "test", default);
        var refused = await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
            commands.SubmitAsync("demo", Shutdown(false), "graceful", "operator")
        );
        Assert.Equal("deployment_pending", refused.Code);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
            commands.SubmitAsync("demo", ClusterAdminCommand.Create("save-all"), "save", "operator")
        );
        Assert.Empty(gatewayHandler.Posts);

        var operation = await commands.SubmitAsync("demo", Shutdown(true), "emergency", "operator");
        Assert.NotEqual(ClusterOperationState.Failed, operation.State); // Running until the Gateway reports Down
        var cluster = catalog.GetCluster("demo")!;
        Assert.Equal(ClusterUpdatePhase.Complete, cluster.Update!.Phase);
        Assert.Equal(
            "Abandoned by operator for an emergency shutdown while Stopping.",
            cluster.Update.LastError
        );
        Assert.Equal("revision", cluster.ActiveDeployment!.Revision);
        var post = Assert.Single(gatewayHandler.Posts);
        Assert.EndsWith("/shutdown", post.Path);
        Assert.Contains("\"emergency\":true", post.Body);

        // While Hosts switch deployments the Gateway is already down; nothing may interrupt that.
        var stopped = catalog.GetCluster("demo")!;
        await catalog.RecordShutdownProofAsync(
            stopped,
            new(
                stopped.GetLifecycleId(),
                DateTimeOffset.UtcNow,
                new GatewayStopFence(121, DateTimeOffset.UtcNow)
            ),
            default
        );
        await update.BeginAsync("demo", new(Guid.NewGuid(), candidate), "begin-2", "test", default);
        await update.AdvanceAllAsync(default);
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
            commands.SubmitAsync("demo", Shutdown(true), "emergency-2", "operator")
        );
        Assert.Equal(ClusterUpdatePhase.Activating, catalog.GetCluster("demo")!.Update!.Phase);
    }

    private sealed class GatewayHandler : HttpMessageHandler
    {
        internal bool Ready;
        internal string Revision = "candidate";
        internal bool SpawnHalted;
        internal NodePlan[] Plan = [];
        internal readonly List<(string Path, string Body)> Posts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
                Posts.Add((request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult() ?? ""));
            if (request.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal))
            {
                var planned = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                    new AdminEnvelope<NodePlan[]>(1, DateTimeOffset.UtcNow, Plan)) };
                planned.Headers.Add("X-Cluster-Gateway-Protocol", "1");
                return Task.FromResult(planned);
            }
            var status = new CometWorks.ClusterGateway.AdminContract.V1.ClusterStatus("demo", "world",
                CometWorks.ClusterGateway.AdminContract.V1.ClusterPhase.Serving, CometWorks.ClusterGateway.AdminContract.V1.StartupKind.Warm,
                null, null, false, SpawnHalted, [], new(0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new(null, 0, 0, default), [], [], true,
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
    private ClusterDeploymentRequest Next(string expected, string revision, char from, char to) => new(expected, revision,
        Request().Hosts.Select(h => h with { Activation = h.Activation with {
            ExpectedBundleManifestSha256 = new string(from, 64), BundleManifestSha256 = new string(to, 64) } }).ToArray());
    private ClusterHostActivation Host(string id) => new(id, "http://" + id, credential,
        new("demo", null, "/" + id + "/bundle.json", new string('a', 64), "http://gateway.test", credential));

    private sealed class Handler(string credential) : HttpMessageHandler
    {
        internal bool FailSecond;
        internal bool Recovery, RefuseRecovery, RefusePreview;
        internal int GatewayStops;
        private readonly DateTimeOffset launched = DateTimeOffset.UtcNow;
        internal Guid StartGeneration = Guid.NewGuid();
        internal bool GatewayStopped; // GET reports the Gateway Off and Missing, as after a fresh activation
        internal string Revision = "revision";
        internal string? Deletion;
        internal HostNodeFailure[]? NodeFailures;
        internal readonly List<string> DeletePreviews = [];
        internal readonly List<string> Detaches = [];
        private readonly Dictionary<string, string> active = [];
        internal readonly List<string> Applied = [];
        internal int FailCaptures;
        internal Action<string>? CaptureObserved;
        internal readonly List<(string Host, HostSnapshotRequest Request)> Captures = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string host = request.RequestUri!.Host;
            object data;
            if (Deletion == "host-offline") throw new HttpRequestException("Host unreachable");
            if (request.RequestUri.AbsolutePath.Contains("/snapshots/"))
            {
                byte[] archive = [1, 2, 3];
                if (request.Method == HttpMethod.Get) // the archive download is raw bytes, not an envelope
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
                if (request.Method == HttpMethod.Post)
                {
                    var capture = JsonSerializer.Deserialize<HostSnapshotRequest>(await request.Content!.ReadAsStringAsync(token), Json)!;
                    Captures.Add((host, capture));
                    CaptureObserved?.Invoke(host);
                    if (FailCaptures > 0 && FailCaptures-- > 0) throw new HttpRequestException("disk full");
                    Assert.Equal(capture.Online, request.RequestUri.Query == "?online=true");
                    data = new HostSnapshot("demo", host, capture.SnapshotId, "revision", capture.ExpectedBundleManifestSha256,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)), archive.Length);
                }
                else data = new { };
            }
            else if (request.Method == HttpMethod.Delete && request.RequestUri.AbsolutePath == HostProtocol.ClusterRoute("demo"))
            {
                Assert.Equal("?sha256=" + active[host], request.RequestUri.Query);
                Detaches.Add(host);
                if (Deletion == "detach-refused" && host == "one")
                    throw new HttpRequestException("Every node and the Gateway must be verifiably stopped");
                data = new HostClusterDetach("demo", ["attachment"], "/state/detached/demo");
            }
            else if (Deletion is not null && request.Method == HttpMethod.Get && !request.RequestUri.AbsolutePath.Contains("/recovery-readiness/"))
            {
                GatewayObservedState? left = GatewayStops > 0 ? null : Deletion switch
                {
                    "gateway-running" or "gateway-running-node-running" => GatewayObservedState.Running,
                    "gateway-failed" => GatewayObservedState.Failed,
                    "gateway-conflict" => GatewayObservedState.UnmanagedConflict,
                    _ => null,
                };
                data = new HostStatus(host, host, [], host == "one" ? [new("demo", left is null ? GatewayGoal.Off : GatewayGoal.On,
                    left ?? GatewayObservedState.Missing, active[host], Revision, [29416], "/runs/one/gateway",
                    left is null ? null : 123, left is null ? null : launched, null, StartGeneration: StartGeneration)] : [], true,
                    ClusterDetach: Deletion != "old-host");
            }
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
                    active[host], Revision, [29416], "/runs/one/gateway", 123, launched, null, StartGeneration: StartGeneration)], true);
            else if (request.Method == HttpMethod.Get) data = new HostStatus(host, host, active.TryGetValue(host, out var hash)
                ? [new("demo", "http://gateway.test", true, hash, "/runs/" + host)] : [],
                GatewayStopped && host == "one" ? [new("demo", GatewayGoal.Off, GatewayObservedState.Missing, active[host], Revision,
                    [29416], "/runs/one/gateway", null, null, null, StartGeneration: StartGeneration)] : null, GatewayStopped, NodeFailures: NodeFailures);
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
                        Revision, [29416], "/runs/one/gateway", StartGeneration: StartGeneration) : null, ["one", "two"]);
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
