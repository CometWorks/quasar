using System.Net;
using System.Text;
using Quasar.Bootstrap;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterCliTests
{
    [Fact]
    public async Task QueryUsesServicePrincipalTokenAndWritesJsonOnly()
    {
        string variable = "QUASAR_CLI_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-bearer");
        try
        {
            var handler = new Handler((request, _) =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-bearer", request.Headers.Authorization?.Parameter);
                Assert.Equal("/api/v1/clusters", request.RequestUri?.AbsolutePath);
                return Response(HttpStatusCode.OK, """{"protocolVersion":1,"capturedAt":"2026-01-01T00:00:00Z","data":[]}""");
            });
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = await ClusterCli.RunAsync(
                ["cluster", "list", "--url", "http://quasar.test", "--token-env", variable],
                handler, output, error);

            Assert.Equal(0, exitCode);
            Assert.Equal("", error.ToString());
            Assert.Equal("{\"protocolVersion\":1,\"capturedAt\":\"2026-01-01T00:00:00Z\",\"data\":[]}" + Environment.NewLine,
                output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task GoalWaitPollsDurableOperation()
    {
        var handler = new Handler((request, call) =>
        {
            if (call == 1)
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("factory-42", request.Headers.GetValues("Idempotency-Key").Single());
                Assert.Contains("\"goal\":\"On\"", request.Content!.ReadAsStringAsync().Result);
                var accepted = Response(HttpStatusCode.Accepted,
                    """{"protocolVersion":1,"capturedAt":"2026-01-01T00:00:00Z","data":{"operationId":"op-1","state":"Running"}}""");
                accepted.Headers.Location = new Uri("/api/v1/clusters/dev/operations/op-1", UriKind.Relative);
                return accepted;
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/clusters/dev/operations/op-1", request.RequestUri?.AbsolutePath);
            return Response(HttpStatusCode.OK,
                """{"protocolVersion":1,"capturedAt":"2026-01-01T00:00:01Z","data":{"operationId":"op-1","state":"Succeeded"}}""");
        });
        var output = new StringWriter();

        int exitCode = await ClusterCli.RunAsync(
            ["cluster", "goal", "dev", "on", "--url", "http://quasar.test",
                "--idempotency-key", "factory-42", "--wait", "--wait-timeout", "5"],
            handler, output, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("\"state\":\"Succeeded\"", output.ToString());
        Assert.DoesNotContain("\"state\":\"Running\"", output.ToString());
    }

    [Fact]
    public async Task FailedOperationHasStableExitCode()
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.Accepted,
            """{"protocolVersion":1,"capturedAt":"2026-01-01T00:00:00Z","data":{"operationId":"op-1","state":"Failed","error":{"code":"nope","message":"No."}}}"""));

        int exitCode = await ClusterCli.RunAsync(
            ["cluster", "goal", "dev", "off", "--url", "http://quasar.test",
                "--idempotency-key", "factory-43"], handler, new StringWriter(), new StringWriter());

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task ExistingOperationCanBeWaitedAfterCallerRestart()
    {
        var handler = new Handler((request, call) =>
        {
            Assert.Equal("/api/v1/clusters/dev/operations/op-1", request.RequestUri?.AbsolutePath);
            string state = call == 1 ? "Running" : "Succeeded";
            return Response(HttpStatusCode.OK,
                "{\"protocolVersion\":1,\"capturedAt\":\"2026-01-01T00:00:00Z\","
                + "\"data\":{\"operationId\":\"op-1\",\"state\":\"" + state + "\"}}");
        });

        int exitCode = await ClusterCli.RunAsync(
            ["cluster", "operation", "dev", "op-1", "--url", "http://quasar.test",
                "--wait", "--wait-timeout", "5"], handler, new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AuthorizationFailureHasStableExitCode()
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.Forbidden,
            """{"protocolVersion":1,"capturedAt":"2026-01-01T00:00:00Z","error":{"code":"scope_forbidden","message":"Denied."}}"""));

        int exitCode = await ClusterCli.RunAsync(
            ["cluster", "status", "dev", "--url", "http://quasar.test"],
            handler, new StringWriter(), new StringWriter());

        Assert.Equal(5, exitCode);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("X-Cluster-Gateway-Protocol", "1");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request, ++Calls));
    }
}
