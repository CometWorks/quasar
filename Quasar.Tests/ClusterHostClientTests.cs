using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterHostClientTests
{
    private const string TokenVariable = "QUASAR_TEST_HOST_COMMAND_TOKEN";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StatusQueryUsesVersionedBearerContract()
    {
        string? previous = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenVariable, "test-host-token");
            var envelope = new HostEnvelope<HostStatus>(HostProtocol.Version, DateTimeOffset.UtcNow,
                new HostStatus("executor-a", "host-a", []));
            var handler = new StubHandler(_ => Response(HttpStatusCode.OK, envelope));
            var client = new ClusterHostClient(new HttpClient(handler));

            HostEnvelope<HostStatus> result = await client.GetStatusAsync(
                Definition(), CancellationToken.None);

            Assert.Equal("host-a", result.Data.HostId);
            Assert.Equal("http://host.test:28400/host/v1/status", handler.RequestUri?.ToString());
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-host-token"), handler.Authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previous);
        }
    }

    [Fact]
    public async Task QueryRejectsMismatchedProtocolHeader()
    {
        string? previous = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenVariable, "test-host-token");
            var envelope = new HostEnvelope<HostStatus>(HostProtocol.Version, DateTimeOffset.UtcNow,
                new HostStatus("executor-a", "host-a", []));
            HttpResponseMessage response = Response(HttpStatusCode.OK, envelope);
            response.Headers.Remove(HostProtocol.HeaderName);
            response.Headers.Add(HostProtocol.HeaderName, "2");
            var client = new ClusterHostClient(new HttpClient(new StubHandler(_ => response)));

            ClusterHostException exception = await Assert.ThrowsAsync<ClusterHostException>(() =>
                client.GetStatusAsync(Definition(), CancellationToken.None));

            Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
            Assert.Equal("host_protocol_mismatch", exception.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previous);
        }
    }

    [Fact]
    public async Task GatewayApplyUsesVersionedBearerContract()
    {
        string? previous = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenVariable, "test-host-token");
            var gateway = new GatewaySpec("demo", GatewayGoal.On, "/bundles/gateway.json",
                new string('a', 64), "config-7", [28000, 28016], "/runs/gateway");
            var envelope = new HostEnvelope<GatewayStatus>(HostProtocol.Version, DateTimeOffset.UtcNow,
                new GatewayStatus("demo", GatewayGoal.On, GatewayObservedState.Running,
                    gateway.BundleManifestSha256, gateway.ConfigRevision, gateway.Ports,
                    gateway.RunRoot, 42, DateTimeOffset.UtcNow, null));
            var handler = new StubHandler(_ => Response(HttpStatusCode.OK, envelope));
            var client = new ClusterHostClient(new HttpClient(handler));

            HostEnvelope<GatewayStatus> result = await client.ApplyGatewayAsync(
                Definition(), gateway, CancellationToken.None);

            Assert.Equal(GatewayObservedState.Running, result.Data.Observed);
            Assert.Equal(HttpMethod.Put, handler.Method);
            Assert.Equal("http://host.test:28400/host/v1/gateways/demo", handler.RequestUri?.ToString());
            Assert.Contains("\"goal\":\"On\"", handler.Body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previous);
        }
    }

    private static ClusterDefinition Definition() => new()
    {
        UniqueName = "demo",
        HostCommandUrl = "http://host.test:28400",
        HostCommandTokenEnvironmentVariable = TokenVariable,
    };

    private static HttpResponseMessage Response<T>(HttpStatusCode statusCode, T body)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8, "application/json"),
        };
        response.Headers.Add(HostProtocol.HeaderName, HostProtocol.Version.ToString());
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Method = request.Method;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            return Task.FromResult(respond(request));
        }
    }
}
