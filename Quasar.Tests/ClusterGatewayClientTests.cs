using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterGatewayClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task HealthQueryAcceptsMatchingVersionedEnvelope()
    {
        var envelope = new AdminEnvelope<GatewayHealth>(AdminProtocol.Version, DateTimeOffset.UtcNow,
            new GatewayHealth("cluster-gateway", true, "direct-transport-v1"));
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, envelope));
        var client = new ClusterGatewayClient(new HttpClient(handler));

        AdminEnvelope<GatewayHealth> result = await client.GetHealthAsync(Definition(), CancellationToken.None);

        Assert.Equal("cluster-gateway", result.Data.Service);
        Assert.Equal("http://gateway.test/admin/v1/health", handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task QueryRejectsMismatchedProtocolHeader()
    {
        var envelope = new AdminEnvelope<GatewayHealth>(AdminProtocol.Version, DateTimeOffset.UtcNow,
            new GatewayHealth("cluster-gateway", true, "direct-transport-v1"));
        HttpResponseMessage response = Response(HttpStatusCode.OK, envelope);
        response.Headers.Remove("X-Cluster-Gateway-Protocol");
        response.Headers.Add("X-Cluster-Gateway-Protocol", "2");
        var client = new ClusterGatewayClient(new HttpClient(new StubHandler(_ => response)));

        ClusterGatewayException exception = await Assert.ThrowsAsync<ClusterGatewayException>(() =>
            client.GetHealthAsync(Definition(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("protocol_mismatch", exception.Code);
    }

    [Fact]
    public async Task QueryPreservesGatewayErrorCode()
    {
        var envelope = new AdminErrorEnvelope(AdminProtocol.Version, DateTimeOffset.UtcNow,
            new AdminError("cluster_not_ready", "Cluster registry is not ready."));
        var client = new ClusterGatewayClient(new HttpClient(new StubHandler(_ =>
            Response(HttpStatusCode.ServiceUnavailable, envelope))));

        ClusterGatewayException exception = await Assert.ThrowsAsync<ClusterGatewayException>(() =>
            client.GetHealthAsync(Definition(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("cluster_not_ready", exception.Code);
    }

    [Fact]
    public async Task RecoveryQueryPreservesAuthoritativeCoverage()
    {
        var readiness = new RecoveryReadiness(
            RecoveryReadinessState.AtRisk,
            new RecoveryPoint("online-1", RecoveryConsistency.CutConsistent, 42),
            10,
            12,
            5,
            new ArtifactCoverage(2, 2, 1, 1),
            new ArtifactCoverage(1, 1, 2, 2),
            new ArtifactCoverage(1, 1, 1, 1),
            new RegistryDurability(true, DateTimeOffset.UtcNow, 3, 4096),
            [],
            ["partitionSingleCopy"]);
        var envelope = new AdminEnvelope<RecoveryReadiness>(AdminProtocol.Version, DateTimeOffset.UtcNow, readiness);
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, envelope));
        var client = new ClusterGatewayClient(new HttpClient(handler));

        AdminEnvelope<RecoveryReadiness> result = await client.GetRecoveryReadinessAsync(
            Definition(), CancellationToken.None);

        Assert.Equal(RecoveryReadinessState.AtRisk, result.Data.State);
        Assert.Equal(2, result.Data.PartitionSaves.Covered);
        Assert.Equal("http://gateway.test/admin/v1/recovery-readiness", handler.RequestUri?.ToString());
    }

    [Theory]
    [InlineData("config", "PUT")]
    [InlineData("shutdown", "POST")]
    [InlineData("gateway/restart", "POST")]
    public async Task MutationsSendStableKeyAndReadRunningOperation(string route, string method)
    {
        const string json = """{"protocolVersion":1,"capturedAt":"2026-09-01T00:00:00Z","data":{"operationId":"op-1","kind":"test","state":"Running","actor":"test","idempotencyKey":"request-1","createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-01T00:00:00Z","target":null,"result":null,"error":null}}""";
        var handler = new StubHandler(request =>
        {
            Assert.Equal("request-1", request.Headers.GetValues("Idempotency-Key").Single());
            var response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(json) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return response;
        });
        var client = new ClusterGatewayClient(new HttpClient(handler));
        var result = route switch
        {
            "config" => await client.SetPolicyAsync(Definition(), new AdminConfigUpdate(2, []), "request-1", CancellationToken.None),
            "shutdown" => await client.ShutdownAsync(Definition(), new ShutdownRequest(GraceSeconds: 0), "request-1", CancellationToken.None),
            _ => await client.RestartGatewayAsync(Definition(), "request-1", CancellationToken.None),
        };
        Assert.Equal(AdminOperationState.Running, result.Data.State);
        Assert.Equal(method, handler.Method?.Method);
        Assert.Equal("http://gateway.test/admin/v1/" + route, handler.RequestUri?.ToString());
        if (route == "config") Assert.Contains("\"expectedRevision\":2", handler.RequestBody);
    }

    [Fact]
    public async Task CurrentStatusPreservesHealthIndependentOfLifecyclePhase()
    {
        const string json = """{"protocolVersion":1,"capturedAt":"2026-09-01T00:00:00Z","data":{"clusterId":"demo","worldId":"world","phase":"Serving","startup":"Warm","shutdownStarted":null,"lastCleanShutdown":null,"executorSilent":true,"globalSpawnHalted":false,"degradedReasons":[],"counts":{"connectedClients":3,"nodes":2,"partitions":4,"saves":4,"handovers":0,"incompleteHandovers":0,"snapshots":1,"voxelBases":0,"voxelJournals":0,"pendingDeletes":0,"walRecords":3,"walBytes":123},"worldAuthority":{"node":"wa-1","epoch":300,"generation":2,"leaseExpires":"2026-09-01T00:01:00Z"},"nodes":[],"executors":[],"acceptingPlayers":false,"health":"Warning","reasonCodes":["executorSilence"],"observedAt":"2026-09-01T00:00:00Z"}}""";
        var client = WireClient(json);
        var result = await client.GetStatusAsync(Definition(), CancellationToken.None);
        Assert.Equal(ClusterPhase.Serving, result.Data.Phase);
        Assert.Equal(AdminHealth.Warning, result.Data.Health);
        Assert.False(result.Data.AcceptingPlayers);
        Assert.Equal(["executorSilence"], result.Data.ReasonCodes);
        Assert.Equal(300, result.Data.WorldAuthority.Epoch);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task MissingRequiredCurrentFieldsAreRejected(string data)
    {
        var client = WireClient("{\"protocolVersion\":1,\"capturedAt\":\"2026-09-01T00:00:00Z\",\"data\":" + data + "}");
        var error = await Assert.ThrowsAsync<ClusterGatewayException>(() => client.GetStatusAsync(Definition(), CancellationToken.None));
        Assert.Equal("protocol_mismatch", error.Code);
    }

    private static ClusterGatewayClient WireClient(string json) => new(new HttpClient(new StubHandler(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
        return response;
    })));

    private static ClusterDefinition Definition() => new()
    {
        UniqueName = "demo",
        GatewayUrl = "http://gateway.test",
    };

    private static HttpResponseMessage Response<T>(HttpStatusCode statusCode, T body)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-Cluster-Gateway-Protocol", AdminProtocol.Version.ToString());
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            RequestBody = request.Content == null
                ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
