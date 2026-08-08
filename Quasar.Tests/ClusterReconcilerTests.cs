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
            return HostResponse(new HostStatus("executor", "host", [], [hostState]));
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
                hostState = GatewayStatus(GatewayGoal.Off, GatewayObservedState.Missing);
                return HostResponse(hostState);
            }
            order.Add("host-status");
            return HostResponse(new HostStatus("executor", "host", [], [hostState]));
        });
        Admin.ClusterPhase phase = Admin.ClusterPhase.Bootstrapping;
        var gateway = new ContractHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                order.Add("gateway-shutdown");
                phase = Admin.ClusterPhase.Down;
                var result = new Admin.GatewayLifecycleResult(Guid.NewGuid(),
                    Admin.GatewayLifecycleAction.GracefulShutdown,
                    Admin.GatewayOperationDisposition.Accepted, phase, DateTimeOffset.UtcNow);
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

    [Fact]
    public async Task PersistedOffStateDoesNotRestartGateway()
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
                : HostResponse(new HostStatus("executor", "host", [], [hostState]));
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
        Assert.Equal(ClusterReconcileState.Converged, reconciler.GetStatus("demo").State);
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
                : HostResponse(new HostStatus("executor", "host", [], [hostState]));
        });
        var gateway = new ContractHandler((_, _) => throw new HttpRequestException("starting"));
        var reconciler = CreateReconciler(catalog, gateway, host);

        await reconciler.ReconcileAllAsync(CancellationToken.None);

        ClusterReconcileStatus status = reconciler.GetStatus("demo");
        Assert.Equal(ClusterReconcileState.Converging, status.State);
        Assert.Equal("gateway_api_starting", status.ErrorCode);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ClusterCatalog CreateCatalog(DedicatedServerGoalState goal)
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
            Gateway = Spec(),
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        File.WriteAllText(Path.Combine(directory, "cluster.json"), JsonSerializer.Serialize(cluster, JsonOptions));
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = _directory }).Build();
        return new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, configuration);
    }

    private static ClusterReconciler CreateReconciler(
        ClusterCatalog catalog, HttpMessageHandler gateway, HttpMessageHandler host) => new(
            catalog,
            new ClusterGatewayClient(new HttpClient(gateway)),
            new ClusterHostClient(new HttpClient(host)),
            NullLogger<ClusterReconciler>.Instance);

    private static GatewaySpec Spec() => new("demo", GatewayGoal.On, "/bundle/manifest.json",
        new string('a', 64), "r1", [28000, 28016], "/runs/demo");

    private static GatewayStatus GatewayStatus(GatewayGoal goal, GatewayObservedState observed) => new(
        "demo", goal, observed, new string('a', 64), "r1", [28000, 28016], "/runs/demo",
        observed == GatewayObservedState.Running ? 42 : null,
        observed == GatewayObservedState.Running ? DateTimeOffset.UnixEpoch : null, null);

    private static Admin.ClusterStatus Status(Admin.ClusterPhase phase) => new(
        "demo", "world", phase, Admin.StartupKind.Recovery, null,
        phase == Admin.ClusterPhase.Down ? DateTimeOffset.UnixEpoch : null,
        false, false, [], new Admin.ClusterCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        new Admin.WorldAuthorityStatus(null, 0, 0, DateTimeOffset.UnixEpoch), [], []);

    private static HttpResponseMessage HostResponse<T>(T value) => Response(
        new HostEnvelope<T>(HostProtocol.Version, DateTimeOffset.UtcNow, value),
        HostProtocol.HeaderName, HostProtocol.Version);

    private static HttpResponseMessage GatewayResponse<T>(T value) => Response(
        new Admin.AdminEnvelope<T>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, value),
        "X-Cluster-Gateway-Protocol", Admin.AdminProtocol.Version);

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
}
