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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}
