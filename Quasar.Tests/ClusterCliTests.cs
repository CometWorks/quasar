extern alias Bootstrap;

using System.Net;
using System.Text;
using Bootstrap::Quasar.Bootstrap;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterCliTests
{
    [Fact]
    public void LauncherReadsCamelCaseWorkerManifest()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"workerId":"worker","processId":123,"baseUrl":"http://127.0.0.1:8080"}""");
            var manifest = Bootstrap::Quasar.Bootstrap.Program.ReadManifest(path);
            Assert.NotNull(manifest);
            Assert.Equal("http://127.0.0.1:8080", manifest.BaseUrl);
            Assert.Equal(123, manifest.ProcessId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LauncherOnlyRetiresTheWorkerNamedByDiscovery()
    {
        var expected = new Magnetar.Protocol.Discovery.WebServiceDiscoveryManifest
            { ProcessId = 123, WorkerId = "original" };
        Assert.True(Bootstrap::Quasar.Bootstrap.Program.IsSameWorker(expected,
            new() { ProcessId = 123, WorkerId = "original" }));
        Assert.False(Bootstrap::Quasar.Bootstrap.Program.IsSameWorker(expected,
            new() { ProcessId = 123, WorkerId = "replacement" }));
        Assert.False(Bootstrap::Quasar.Bootstrap.Program.IsSameWorker(expected,
            new() { ProcessId = 456, WorkerId = "original" }));
    }

    [Theory]
    [InlineData("conversion-plugins", "/api/v1/clusters/demo/convert/plugins")]
    [InlineData("fleet", "/api/v1/clusters/demo/fleet")]
    [InlineData("package-release", "/api/v1/clusters/demo/package-release")]
    [InlineData("package-selection", "/api/v1/clusters/demo/package-selection")]
    [InlineData("dependency-candidate", "/api/v1/clusters/demo/dependency-candidate")]
    [InlineData("dependencies", "/api/v1/clusters/demo/dependencies")]
    [InlineData("deployment-inputs", "/api/v1/clusters/demo/deployment-inputs")]
    [InlineData("deployment", "/api/v1/clusters/demo/deployment")]
    [InlineData("events", "/api/v1/clusters/demo/events?cursor=42&limit=10")]
    [InlineData("chat-history", "/api/v1/clusters/demo/chat/history?cursor=42&limit=10")]
    public async Task ExpandedQueriesPreserveCursor(string command, string expected)
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(expected, request.RequestUri!.PathAndQuery);
            return Response(HttpStatusCode.OK, """{"protocolVersion":1,"data":[]}""");
        });
        int result = await ClusterCli.RunAsync(["cluster", command, "demo", "--url", "http://quasar.test",
            "--cursor", "42", "--limit", "10"], handler, new StringWriter(), new StringWriter());
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 8)]
    public async Task UpdateWaitRequiresMatchingWorkflow(bool matches, int expected)
    {
        var id = Guid.NewGuid();
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { id, rollback = true }));
            var handler = new Handler((_, call) => call == 1
                ? Response(HttpStatusCode.OK, """{"protocolVersion":1,"data":{"operationId":"op","state":"Succeeded"}}""")
                : Response(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1,
                    data = new { id = matches ? id : Guid.NewGuid(), phase = "Complete" } })));
            int code = await ClusterCli.RunAsync(["cluster", "update", "dev", path, "--url", "http://quasar.test", "--wait", "--idempotency-key", "update-test"],
                handler, new StringWriter(), new StringWriter());
            Assert.Equal(expected, code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PackageSelectionCarriesRevisionAndIdempotencyKey()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/v1/clusters/demo/package-selection", request.RequestUri!.AbsolutePath);
            Assert.Equal("select-1", request.Headers.GetValues("Idempotency-Key").Single());
            using var json = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("1.0.3", json.RootElement.GetProperty("version").GetString());
            Assert.Equal(new string('a', 64), json.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(7, json.RootElement.GetProperty("expectedRevision").GetInt64());
            return Response(HttpStatusCode.OK, """{"protocolVersion":1,"data":{"state":"Succeeded"}}""");
        });
        Assert.Equal(0, await ClusterCli.RunAsync(["cluster", "package-select", "demo", "1.0.3", new string('a', 64),
            "7", "--url", "http://quasar.test", "--idempotency-key", "select-1"], handler, new StringWriter(), new StringWriter()));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775807")]
    [InlineData("abc")]
    public async Task InvalidPackageRevisionDoesNotSendRequest(string revision)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("must not send"));
        Assert.Equal(2, await ClusterCli.RunAsync(["cluster", "package-select", "demo", "1.0.3", new string('a', 64),
            revision, "--url", "http://quasar.test", "--idempotency-key", "select-1"], handler, new StringWriter(), new StringWriter()));
    }

    [Fact]
    public async Task PackageStagePinsVersionAndChecksum()
    {
        string checksum = new('a', 64);
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/v1/clusters/demo/package", request.RequestUri!.AbsolutePath);
            Assert.Equal("stage-1", request.Headers.GetValues("Idempotency-Key").Single());
            var json = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("1.0.3", json.RootElement.GetProperty("version").GetString());
            Assert.Equal(checksum, json.RootElement.GetProperty("sha256").GetString());
            return Response(HttpStatusCode.OK, """{"protocolVersion":1,"data":{"state":"Succeeded"}}""");
        });
        Assert.Equal(0, await ClusterCli.RunAsync(["cluster", "package-stage", "demo", "1.0.3", checksum,
            "--url", "http://quasar.test", "--idempotency-key", "stage-1"], handler, new StringWriter(), new StringWriter()));
    }

    [Theory]
    [InlineData("command", "commands", "POST")]
    [InlineData("recover", "recover", "POST")]
    [InlineData("dependencies-stage", "dependencies", "PUT")]
    [InlineData("convert-to-cluster", "convert/from-server", "POST")]
    [InlineData("convert-to-server", "convert/to-server", "POST")]
    public async Task JsonCommandUsesSharedMutationRoute(string command, string route, string method)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"action":"save-all"}""");
            var handler = new Handler((request, _) =>
            {
                Assert.Equal(new HttpMethod(method), request.Method);
                Assert.Equal("/api/v1/clusters/demo/" + route, request.RequestUri!.AbsolutePath);
                Assert.Equal("save-42", request.Headers.GetValues("Idempotency-Key").Single());
                Assert.Contains("save-all", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Response(HttpStatusCode.Accepted, """{"protocolVersion":1,"data":{"operationId":"op-a","state":"Running"}}""");
            });
            Assert.Equal(0, await ClusterCli.RunAsync(["cluster", command, "demo", path, "--url", "http://quasar.test",
                "--idempotency-key", "save-42"], handler, new StringWriter(), new StringWriter()));
        }
        finally { File.Delete(path); }
    }

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
