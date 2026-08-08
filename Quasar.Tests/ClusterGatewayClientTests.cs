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

    [Fact]
    public async Task PolicyMutationUsesVersionedGatewayPut()
    {
        var applied = new ClusterPolicyApplied("revision-2", true, []);
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK,
            new AdminEnvelope<ClusterPolicyApplied>(AdminProtocol.Version, DateTimeOffset.UtcNow, applied)));
        var client = new ClusterGatewayClient(new HttpClient(handler));
        var policy = new ClusterPolicy("revision-2", 2, ["host-a", "host-b"], "host-a");

        AdminEnvelope<ClusterPolicyApplied> result = await client.SetPolicyAsync(
            Definition(), policy, CancellationToken.None);

        Assert.True(result.Data.Changed);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("http://gateway.test/admin/v1/config", handler.RequestUri?.ToString());
        Assert.Contains("\"nodeTargetCount\":2", handler.RequestBody);
    }

    [Fact]
    public async Task ShutdownUsesStableIdempotentLifecycleRoute()
    {
        Guid requestId = Guid.NewGuid();
        var result = new GatewayLifecycleResult(requestId, GatewayLifecycleAction.GracefulShutdown,
            GatewayOperationDisposition.Accepted, ClusterPhase.Down, DateTimeOffset.UtcNow);
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK,
            new AdminEnvelope<GatewayLifecycleResult>(AdminProtocol.Version, DateTimeOffset.UtcNow, result)));
        var client = new ClusterGatewayClient(new HttpClient(handler));

        await client.ShutdownAsync(Definition(), new ShutdownRequest(requestId,
            ShutdownMode.Graceful, 0, 10, 10), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://gateway.test/admin/v1/shutdown", handler.RequestUri?.ToString());
        Assert.Contains(requestId.ToString(), handler.RequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GatewayRestartUsesStableLifecycleRoute()
    {
        Guid requestId = Guid.NewGuid();
        var result = new GatewayLifecycleResult(requestId, GatewayLifecycleAction.Restart,
            GatewayOperationDisposition.Accepted, ClusterPhase.Serving, DateTimeOffset.UtcNow);
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK,
            new AdminEnvelope<GatewayLifecycleResult>(AdminProtocol.Version, DateTimeOffset.UtcNow, result)));
        var client = new ClusterGatewayClient(new HttpClient(handler));

        await client.RestartGatewayAsync(Definition(), new GatewayRestartRequest(requestId), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://gateway.test/admin/v1/gateway/restart", handler.RequestUri?.ToString());
    }

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
