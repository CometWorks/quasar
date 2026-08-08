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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            return Task.FromResult(respond(request));
        }
    }
}
