using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Tests;

public sealed class ClusterReconcilerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"quasar-reconciler-{Guid.NewGuid():N}");
    private readonly string _tokenVariable = "QUASAR_RECONCILER_TEST_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ClusterBusyWithALongOperationIsSkippedInsteadOfStallingTheLoop()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.On);
        int hostCalls = 0;
        var host = new ContractHandler((_, _) => { hostCalls++; return HostResponse(Host([GatewayStatus(GatewayGoal.On, GatewayObservedState.Running)])); });
        var gateway = new ContractHandler((_, _) => GatewayResponse(Status(Admin.ClusterPhase.Serving)));
        var reconciler = CreateReconciler(catalog, gateway, host);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A backup, restore or activation holds the lifecycle gate for the whole transfer.
        Task busy = catalog.WithLifecycleAsync("demo", async _ => { entered.SetResult(); await release.Task; return true; }, default);
        await entered.Task;

        await reconciler.ReconcileAllAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, hostCalls);

        release.SetResult(); await busy;
        await reconciler.ReconcileAllAsync(CancellationToken.None);
        Assert.True(hostCalls > 0);
    }

    [Fact]
    public async Task OnStartsGatewayOnceThenOnlyObserves()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.On);
        GatewayStatus hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
        int applies = 0;
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                applies++;
                hostState = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running);
                return HostResponse(hostState);
            }
            return HostResponse(Host( [hostState]));
        });
        var gateway = new ContractHandler((_, _) => GatewayResponse(Status(Admin.ClusterPhase.Serving)));
        var reconciler = CreateReconciler(catalog, gateway, host);

        await reconciler.ReconcileAllAsync(CancellationToken.None);
        await reconciler.ReconcileAllAsync(CancellationToken.None);

        Assert.Equal(1, applies);
        Assert.Equal(ClusterReconcileState.Converged, reconciler.GetStatus("demo").State);
    }

    [Fact]
    public async Task OffGracefullyShutsGatewayBeforeStoppingProcess()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        var order = new List<string>();
        GatewayStatus hostState = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running);
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                order.Add("host-off");
                var requestSpec = JsonSerializer.Deserialize<GatewaySpec>(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), JsonOptions)!;
                Assert.Equal(new GatewayStopFence(42, DateTimeOffset.UnixEpoch), requestSpec.StopFence);
                hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
                return HostResponse(hostState);
            }
            order.Add("host-status");
            return HostResponse(Host( [hostState]));
        });
        Admin.ClusterPhase phase = Admin.ClusterPhase.Bootstrapping;
        var gateway = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                order.Add("gateway-shutdown");
                phase = Admin.ClusterPhase.Down;
                var result = new Admin.AdminOperation("shutdown-1", "shutdown", Admin.AdminOperationState.Succeeded,
                    "test", request.Headers.GetValues("Idempotency-Key").Single(), DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, null, null, null);
                return GatewayResponse(result);
            }
            order.Add("gateway-status");
            return GatewayResponse(Status(phase));
        });
        var reconciler = CreateReconciler(catalog, gateway, host);

        await reconciler.ReconcileAllAsync(CancellationToken.None);

        Assert.Equal(["host-status", "gateway-status", "gateway-shutdown", "gateway-status", "host-off"], order);
        Assert.Equal(ClusterReconcileState.Converged, reconciler.GetStatus("demo").State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedShutdownWithEmptyNodesRequiresRecoveryWithoutRepeatingMutation(bool retainEmptyNode)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        int shutdowns = 0;
        var host = new ContractHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return HostResponse(Host([GatewayStatus(GatewayGoal.On, GatewayObservedState.Running)]));
        });
        var emptyNode = new Admin.NodeStatus("world-authority", "wa", 2, Admin.NodeRole.WorldAuthority,
            Admin.NodeState.Empty, "127.0.0.1:28100", null, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 0, 0, "host", false);
        var gateway = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                shutdowns++;
                return GatewayError(HttpStatusCode.Conflict, "shutdown_failed",
                    "Graceful shutdown is waiting for Empty nodes and final saves; dirty: global");
            }
            return GatewayResponse(Status(Admin.ClusterPhase.Draining) with
                { Nodes = retainEmptyNode ? [emptyNode] : [] });
        });
        var reconciler = CreateReconciler(catalog, gateway, host);

        await reconciler.ReconcileAllAsync(default);
        await reconciler.ReconcileAllAsync(default);
        await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);

        Assert.Equal(1, shutdowns);
        Assert.Equal(ClusterReconcileState.ConfigurationRequired, reconciler.GetStatus("demo").State);
        Assert.Equal("shutdown_recovery_required", reconciler.GetStatus("demo").ErrorCode);
        Assert.Contains("dirty: global", reconciler.GetStatus("demo").Message);
        Assert.Null(catalog.GetCluster("demo")!.ShutdownProof);
    }

    [Fact]
    public async Task PersistedOffWithoutProofRequiresRecoveryAndDoesNotRestartGateway()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        int gatewayCalls = 0, hostApplies = 0;
        GatewayStatus hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put) hostApplies++;
            return request.Method == HttpMethod.Put
                ? HostResponse(hostState)
                : HostResponse(Host( [hostState]));
        });
        var gateway = new ContractHandler((_, _) =>
        {
            gatewayCalls++;
            return GatewayResponse(Status(Admin.ClusterPhase.Down));
        });
        var reconciler = CreateReconciler(catalog, gateway, host);

        await reconciler.ReconcileAllAsync(CancellationToken.None);

        Assert.Equal(0, gatewayCalls);
        Assert.Equal(0, hostApplies);
        Assert.Equal(ClusterReconcileState.ConfigurationRequired, reconciler.GetStatus("demo").State);
        Assert.Equal("shutdown_unverified", reconciler.GetStatus("demo").ErrorCode);
        Assert.Null(reconciler.GetStatus("demo").ClusterPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanFencedStopCompletesShutdownJournalEvenWhenRemoteAcknowledgementWasLost(bool lostHostReply)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using var catalog = CreateCatalog(DedicatedServerGoalState.Off);
        GatewayStatus state = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running);
        bool down = false;
        var gateway = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                down = true;
                return GatewayResponse(new Admin.AdminOperation("shutdown-1", "shutdown", Admin.AdminOperationState.Running,
                    "test", request.Headers.GetValues("Idempotency-Key").Single(), DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, null, null, null));
            }
            return GatewayResponse(Status(down ? Admin.ClusterPhase.Down : Admin.ClusterPhase.Serving));
        });
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                state = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
                if (lostHostReply) throw new HttpRequestException("stop reply lost");
                return HostResponse(state);
            }
            return HostResponse(Host([state]));
        });
        ClusterOperationStore Store() => new(Path.Combine(_directory, "operations"));
        await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
        Assert.True(Store().HasPendingShutdown("demo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CompleteShutdownAsync(catalog.GetCluster("demo")!, default));
        await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
        if (lostHostReply)
        {
            Assert.True(Store().HasPendingShutdown("demo"));
            state = state with { CompletedStopFence = new(43, DateTimeOffset.UnixEpoch) };
            await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
            Assert.True(Store().HasPendingShutdown("demo"));
            state = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
            await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
        }
        Assert.False(Store().HasPendingShutdown("demo"));
        var record = Directory.EnumerateFiles(Path.Combine(_directory, "operations"), "*.json").Single();
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(record));
        Assert.Equal("clean-shutdown-proof", saved.RootElement.GetProperty("result").GetProperty("confirmation").GetString());
    }

    [Fact]
    public async Task OnWaitsWhileNewGatewayAdminApiStarts()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.On);
        GatewayStatus hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
                hostState = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running);
            return request.Method == HttpMethod.Put
                ? HostResponse(hostState)
                : HostResponse(Host( [hostState]));
        });
        var gateway = new ContractHandler((_, _) => throw new HttpRequestException("starting"));
        var reconciler = CreateReconciler(catalog, gateway, host);

        await reconciler.ReconcileAllAsync(CancellationToken.None);

        ClusterReconcileStatus status = reconciler.GetStatus("demo");
        Assert.Equal(ClusterReconcileState.Converging, status.State);
        Assert.Equal("gateway_api_starting", status.ErrorCode);
    }

    [Fact]
    public async Task CleanProofSurvivesLostHostResponseAndWorkerRestart()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        GatewayStatus hostState = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running);
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.NotNull(catalog.GetCluster("demo")!.ShutdownProof);
                hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
                throw new HttpRequestException("response lost after stop");
            }
            return HostResponse(Host( [hostState]));
        });
        var gateway = new ContractHandler((_, _) => GatewayResponse(Status(Admin.ClusterPhase.Down)));
        await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);

        using ClusterCatalog recovered = OpenCatalog();
        var unavailable = new ContractHandler((_, _) => throw new InvalidOperationException("Gateway must stay stopped"));
        var reconciler = CreateReconciler(recovered, unavailable, host);
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal(ClusterReconcileState.Converged, reconciler.GetStatus("demo").State);
        Assert.Equal(Admin.ClusterPhase.Down, reconciler.GetStatus("demo").ClusterPhase);

        hostState = hostState with { CompletedStopFence = new(43, DateTimeOffset.UnixEpoch) };
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal("shutdown_unverified", reconciler.GetStatus("demo").ErrorCode);
        hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);

        // Repeating Off is idempotent; changing configuration invalidates the proof.
        string identity = recovered.GetCluster("demo")!.GetLifecycleId();
        await recovered.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        Assert.Equal(identity, recovered.GetCluster("demo")!.GetLifecycleId());
        await recovered.SetGatewayAsync("demo", Spec() with { ConfigRevision = "r2" });
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal("shutdown_unverified", reconciler.GetStatus("demo").ErrorCode);
        Assert.Null(recovered.GetCluster("demo")!.ShutdownProof);
    }

    [Fact]
    public async Task ShutdownJournalReplaysLostResponseThenPollsSameOperationAfterRestart()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        GatewayStatus hostState = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running);
        int stops = 0, posts = 0, polls = 0;
        string? key = null;
        bool down = false;
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                stops++;
                hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
                return HostResponse(hostState);
            }
            return HostResponse(Host( [hostState]));
        });
        var gateway = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                string attemptKey = request.Headers.GetValues("Idempotency-Key").Single();
                if (posts++ == 0)
                {
                    key = attemptKey;
                    throw new HttpRequestException("lost shutdown response");
                }
                Assert.Equal(key, attemptKey);
                return GatewayResponse(Operation(Admin.AdminOperationState.Running));
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/operations/shutdown-1"))
            {
                polls++;
                down = true;
                return GatewayResponse(Operation(Admin.AdminOperationState.Succeeded));
            }
            return GatewayResponse(Status(down ? Admin.ClusterPhase.Down : Admin.ClusterPhase.Serving));
        });
        await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
        Assert.Equal(0, stops);
        await CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
        Assert.Equal(0, stops);
        var recovered = CreateReconciler(catalog, gateway, host);
        await recovered.ReconcileAllAsync(default);
        Assert.Equal(2, posts);
        Assert.Equal(1, polls);
        Assert.Equal(1, stops);
        Assert.Equal(ClusterReconcileState.Converged, recovered.GetStatus("demo").State);

        Admin.AdminOperation Operation(Admin.AdminOperationState state) => new(
            "shutdown-1", "shutdown", state, "test", key!, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null, null, null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GatewayObservedState.Missing)]
    [InlineData(GatewayObservedState.Failed)]
    [InlineData(GatewayObservedState.UnmanagedConflict)]
    public async Task OffNeverLaunchesGatewayForRecovery(GatewayObservedState? observed)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        var host = new ContractHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return HostResponse(Host( observed is { } state
                ? [GatewayStatus(GatewayGoal.On, state)] : []));
        });
        var gateway = new ContractHandler((_, _) => throw new InvalidOperationException("must not contact Gateway"));
        var reconciler = CreateReconciler(catalog, gateway, host);
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal(ClusterReconcileState.ConfigurationRequired, reconciler.GetStatus("demo").State);
    }

    [Fact]
    public async Task OnWaitsForPendingShutdownRecoveredFromJournal()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        var store = new ClusterOperationStore(Path.Combine(_directory, "operations"));
        var gateway = new ContractHandler((request, _) => GatewayResponse(new Admin.AdminOperation(
            "shutdown-1", "shutdown", Admin.AdminOperationState.Running, "test",
            request.Headers.GetValues("Idempotency-Key").Single(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null, null, null)));
        await store.ExecuteGatewayAsync(catalog.GetCluster("demo")!, "cluster.lifecycle.shutdown", "POST",
            "shutdown", new Admin.ShutdownRequest(), "stop", "test", new(new HttpClient(gateway)), default);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);

        var forbidden = new ContractHandler((_, _) => throw new InvalidOperationException("must wait for shutdown"));
        var reconciler = CreateReconciler(catalog, forbidden, forbidden);
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal("shutdown_pending", reconciler.GetStatus("demo").ErrorCode);
    }

    [Fact]
    public async Task ShutdownRefusesWrongClusterBeforeMutation()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        var host = new ContractHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return HostResponse(Host(
                [GatewayStatus(GatewayGoal.On, GatewayObservedState.Running)]));
        });
        var gateway = new ContractHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return GatewayResponse(Status(Admin.ClusterPhase.Serving) with { ClusterId = "different" });
        });
        var reconciler = CreateReconciler(catalog, gateway, host);
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal("cluster_identity_mismatch", reconciler.GetStatus("demo").ErrorCode);
        Assert.Null(catalog.GetCluster("demo")!.ShutdownProof);
    }

    [Fact]
    public async Task ShutdownRefusesHostWithoutStopFencing()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        var host = new ContractHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return HostResponse(new HostStatus("old-executor", "host", [],
                [GatewayStatus(GatewayGoal.On, GatewayObservedState.Running)]));
        });
        var gateway = new ContractHandler((_, _) => throw new InvalidOperationException("must not begin shutdown"));
        var reconciler = CreateReconciler(catalog, gateway, host);
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal("gateway_stop_fencing_unavailable", reconciler.GetStatus("demo").ErrorCode);
    }

    [Fact]
    public async Task GoalChangeWaitsForInFlightShutdownAndClearsItsProof()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new AsyncHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return HostResponse(GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing));
            }
            return HostResponse(Host(
                [GatewayStatus(GatewayGoal.On, GatewayObservedState.Running)]));
        });
        var gateway = new ContractHandler((_, _) => GatewayResponse(Status(Admin.ClusterPhase.Down)));
        Task reconcile = CreateReconciler(catalog, gateway, host).ReconcileAllAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task goal = catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);
        try { Assert.False(goal.IsCompleted); }
        finally { release.SetResult(); }
        await Task.WhenAll(reconcile, goal);
        Assert.Equal(DedicatedServerGoalState.On, catalog.GetCluster("demo")!.GoalState);
        Assert.Null(catalog.GetCluster("demo")!.ShutdownProof);
    }

    [Fact]
    public async Task ObservationNeedsNoHostAndDoesNotApplyDefaultOffGoal()
    {
        using ClusterCatalog catalog = CreateCatalog(DedicatedServerGoalState.Off, observeOnly: true);
        var host = new ContractHandler((_, _) => throw new InvalidOperationException("must not contact host"));
        var gateway = new ContractHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return GatewayResponse(Status(Admin.ClusterPhase.Serving));
        });
        var reconciler = CreateReconciler(catalog, gateway, host);
        await reconciler.ReconcileAllAsync(CancellationToken.None);
        Assert.Equal(ClusterReconcileState.Observing, reconciler.GetStatus("demo").State);
        Assert.Equal(Admin.ClusterPhase.Serving, reconciler.GetStatus("demo").ClusterPhase);
    }

    [Theory]
    [InlineData(Admin.ClusterPhase.Serving, 0)]
    [InlineData(Admin.ClusterPhase.Draining, 0)]
    [InlineData(Admin.ClusterPhase.Down, 1)]
    public async Task NewGenerationPreservesLiveOrDrainingGatewayAndFencesCleanDown(Admin.ClusterPhase phase, int expectedStops)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "test-token");
        using var catalog = CreateCatalog(DedicatedServerGoalState.On);
        Guid oldGeneration = Guid.NewGuid(), desired = Guid.NewGuid();
        await catalog.SetGatewayAsync("demo", Spec() with { StartGeneration = desired });
        var current = GatewayStatus(GatewayGoal.On, GatewayObservedState.Running) with { StartGeneration = oldGeneration };
        int stops = 0;
        var host = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                var spec = JsonSerializer.Deserialize<GatewaySpec>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), JsonOptions)!;
                if (spec.Goal == GatewayGoal.On)
                {
                    Assert.Equal(1, stops);
                    Assert.Equal(desired, spec.StartGeneration);
                    return HostResponse(current with { StartGeneration = desired });
                }
                Assert.Equal(GatewayGoal.Off, spec.Goal);
                Assert.Equal(oldGeneration, spec.StartGeneration);
                Assert.Equal(new GatewayStopFence(42, DateTimeOffset.UnixEpoch), spec.StopFence);
                stops++;
                return HostResponse(current with { Goal = GatewayGoal.Off, Observed = GatewayObservedState.Missing,
                    ProcessId = null, LaunchedAt = null, CompletedStopFence = spec.StopFence });
            }
            return HostResponse(Host([current]));
        });
        var gateway = new ContractHandler((_, _) => GatewayResponse(Status(phase)));
        var reconciler = CreateReconciler(catalog, gateway, host);
        await reconciler.ReconcileAllAsync(default);
        Assert.Equal(expectedStops, stops);
        Assert.Equal(phase == Admin.ClusterPhase.Serving ? oldGeneration : desired, catalog.GetCluster("demo")!.Gateway!.StartGeneration);
        Assert.Equal(ClusterReconcileState.Converging, reconciler.GetStatus("demo").State);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ClusterCatalog CreateCatalog(DedicatedServerGoalState goal, bool observeOnly = false)
    {
        string directory = Path.Combine(_directory, "demo");
        Directory.CreateDirectory(directory);
        var cluster = new ClusterDefinition
        {
            UniqueName = "demo",
            DisplayName = "Demo",
            GatewayUrl = "http://gateway.test",
            HostCommandUrl = "http://host.test",
            HostCommandTokenEnvironmentVariable = _tokenVariable,
            GoalState = goal,
            ShutdownGracePeriodSeconds = 0,
            Gateway = observeOnly ? null : Spec(),
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        File.WriteAllText(Path.Combine(directory, "cluster.json"), JsonSerializer.Serialize(cluster, JsonOptions));
        return OpenCatalog();
    }

    private ClusterCatalog OpenCatalog()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = _directory }).Build();
        return new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, configuration);
    }

    private ClusterReconciler CreateReconciler(
        ClusterCatalog catalog, HttpMessageHandler gateway, HttpMessageHandler host) => new(
            catalog,
            new ClusterGatewayClient(new HttpClient(gateway)),
            new ClusterHostClient(new HttpClient(host)),
            new ClusterOperationStore(Path.Combine(_directory, "operations")),
            NullLogger<ClusterReconciler>.Instance);

    private static HostStatus Host(GatewayStatus[] gateways) => new("executor", "host", [], gateways, GatewayStopFencing: true);

    private static GatewaySpec Spec() => new("demo", GatewayGoal.On, "/bundle/manifest.json",
        new string('a', 64), "r1", [28000, 28016], "/runs/demo");

    private static GatewayStatus GatewayStatus(GatewayGoal goal, GatewayObservedState observed) => new(
        "demo", goal, observed, new string('a', 64), "r1", [28000, 28016], "/runs/demo",
        observed == GatewayObservedState.Running ? 42 : null,
        observed == GatewayObservedState.Running ? DateTimeOffset.UnixEpoch : null, null,
        goal == GatewayGoal.Off && observed == GatewayObservedState.Missing ? new(42, DateTimeOffset.UnixEpoch) : null);

    private static Admin.ClusterStatus Status(Admin.ClusterPhase phase) => new(
        "demo", "world", phase, Admin.StartupKind.Recovery, null,
        phase == Admin.ClusterPhase.Down ? DateTimeOffset.UnixEpoch : null,
        false, false, [], new Admin.ClusterCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        new Admin.WorldAuthorityStatus(null, 0, 0, DateTimeOffset.UnixEpoch), [], [], false, Admin.AdminHealth.Healthy, [], DateTimeOffset.UtcNow);

    private static HttpResponseMessage HostResponse<T>(T value) => Response(
        new HostEnvelope<T>(HostProtocol.Version, DateTimeOffset.UtcNow, value),
        HostProtocol.HeaderName, HostProtocol.Version);

    private static HttpResponseMessage GatewayResponse<T>(T value) => Response(
        new Admin.AdminEnvelope<T>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, value),
        "X-Cluster-Gateway-Protocol", Admin.AdminProtocol.Version);

    private static HttpResponseMessage GatewayError(HttpStatusCode status, string code, string message)
    {
        var response = Response(new Admin.AdminErrorEnvelope(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
            new Admin.AdminError(code, message)), "X-Cluster-Gateway-Protocol", Admin.AdminProtocol.Version);
        response.StatusCode = status;
        return response;
    }

    private static HttpResponseMessage Response<T>(T value, string header, int version)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json"),
        };
        response.Headers.Add(header, version.ToString());
        return response;
    }

    private sealed class ContractHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request, cancellationToken));
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            response(request, token);
    }
}
