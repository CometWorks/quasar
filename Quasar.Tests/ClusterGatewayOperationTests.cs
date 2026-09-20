using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterGatewayOperationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quasar-durable-" + Guid.NewGuid());
    private readonly ClusterDefinition _cluster = new() { UniqueName = "demo", GatewayUrl = "http://gateway.test" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task AcceptedOperationSurvivesRestartAndPollsById()
    {
        string? key = null;
        int mutations = 0;
        var client = Client(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                mutations++;
                key = request.Headers.GetValues("Idempotency-Key").Single();
                return Operation(key, AdminOperationState.Running);
            }
            Assert.EndsWith("/operations/remote-1", request.RequestUri!.AbsolutePath);
            return Operation(key!, AdminOperationState.Succeeded);
        });
        var first = await Execute(new(_directory), client);
        Assert.Equal(ClusterOperationState.Running, first.State);
        Assert.Equal("remote-1", first.GatewayOperationId);
        var completed = await Execute(new(_directory), client);
        Assert.Equal(first.OperationId, completed.OperationId);
        Assert.Equal(ClusterOperationState.Succeeded, completed.State);
        Assert.Equal(1, mutations);
        await Execute(new(_directory), Client(_ => throw new Exception("terminal replay must not call Gateway")));
    }

    [Fact]
    public async Task LostResponseReplaysPersistedKeyAndChangedBodyConflicts()
    {
        string? key = null;
        var first = await Execute(new(_directory), Client(request =>
        {
            key = request.Headers.GetValues("Idempotency-Key").Single();
            throw new HttpRequestException("response lost after acceptance");
        }));
        Assert.Equal(ClusterOperationState.Running, first.State);
        var recovered = new ClusterOperationStore(_directory);
        var done = await Execute(recovered, Client(request =>
        {
            Assert.Equal(key, request.Headers.GetValues("Idempotency-Key").Single());
            return Operation(key!, AdminOperationState.Succeeded);
        }));
        Assert.Equal(first.OperationId, done.OperationId);
        var conflict = await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
            recovered.ExecuteGatewayAsync(_cluster, "cluster.save-all", "POST", "save-all", new { changed = true },
                "request-1", "test", Client(_ => throw new Exception("must not send")), CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task RevisionConflictIsTerminal()
    {
        var result = await Execute(new(_directory), Client(_ => Response(HttpStatusCode.Conflict,
            new AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new AdminError("revision_conflict", "Stale revision.")))));
        Assert.Equal(ClusterOperationState.Failed, result.State);
        Assert.Equal("revision_conflict", result.Error?.Code);
    }

    [Fact]
    public async Task UndeliveredMutationExpiresInsteadOfBeingDeliveredAfterALongOutage()
    {
        var outage = Client(_ => throw new HttpRequestException("Gateway unreachable"));
        var pending = await Execute(new(_directory), outage);
        Assert.Equal(ClusterOperationState.Running, pending.State);
        string path = Path.Combine(_directory, pending.OperationId + ".json");
        DateTime written = File.GetLastWriteTimeUtc(path);
        await Execute(new(_directory), outage);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path)); // an unchanged retry is not rewritten

        // Age the record past the delivery lifetime.
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node["createdAt"] = DateTimeOffset.UtcNow - ClusterOperationStore.UndeliveredMutationLifetime - TimeSpan.FromSeconds(1);
        File.WriteAllText(path, node.ToJsonString());
        var expired = await Execute(new(_directory), Client(_ => throw new Exception("an expired request must not reach the Gateway")));

        Assert.Equal(ClusterOperationState.Failed, expired.State);
        Assert.Equal("gateway_delivery_expired", expired.Error!.Code);
    }

    [Fact]
    public async Task PendingMutationCanBeCancelledUntilTheGatewayAcceptsIt()
    {
        var store = new ClusterOperationStore(_directory);
        var pending = await Execute(store, Client(_ => throw new HttpRequestException("Gateway unreachable")));

        var cancelled = await store.CancelGatewayAsync("demo", pending.OperationId, "operator", default);

        Assert.Equal(ClusterOperationState.Failed, cancelled!.State);
        Assert.Equal("cancelled", cancelled.Error!.Code);
        Assert.False(store.HasPendingOperations("demo"));
        await Execute(store, Client(_ => throw new Exception("a cancelled request must not reach the Gateway")));
        Assert.Null(await store.CancelGatewayAsync("other", pending.OperationId, "operator", default));

        string? key = null;
        var accepted = await store.ExecuteGatewayAsync(_cluster, "cluster.save-all", "POST", "save-all", new SaveAllRequest(), "request-2", "test",
            Client(request => { key = request.Headers.GetValues("Idempotency-Key").Single(); return Operation(key, AdminOperationState.Running); }), default);
        Assert.Equal("operation_already_accepted", (await Assert.ThrowsAsync<ClusterOperationConflictException>(
            () => store.CancelGatewayAsync("demo", accepted.OperationId, "operator", default))).Code);
    }

    [Fact]
    public async Task FailedAutomaticRequestGetsANewAttemptKeyAfterTheRetryDelay()
    {
        var store = new ClusterOperationStore(_directory);
        const string kind = "cluster.lifecycle.shutdown", lifecycle = "lifecycle-1";
        Assert.Equal(lifecycle, store.AttemptKey("demo", kind, lifecycle, TimeSpan.FromMinutes(1)));
        var failed = await store.ExecuteGatewayAsync(_cluster, kind, "POST", "shutdown", new ShutdownRequest(), lifecycle, "reconciler",
            Client(_ => Response(HttpStatusCode.Conflict, new AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new AdminError("shutdown_failed", "players still connected")))), default);
        Assert.Equal(ClusterOperationState.Failed, failed.State);

        // Within the delay the failure is replayed; afterwards the next attempt can reach the Gateway.
        Assert.Equal(lifecycle, store.AttemptKey("demo", kind, lifecycle, TimeSpan.FromMinutes(1)));
        string retry = store.AttemptKey("demo", kind, lifecycle, TimeSpan.Zero);
        Assert.Equal(lifecycle + ":2", retry);
        string? key = null;
        var running = await store.ExecuteGatewayAsync(_cluster, kind, "POST", "shutdown", new ShutdownRequest(), retry, "reconciler",
            Client(request => { key = request.Headers.GetValues("Idempotency-Key").Single(); return Operation(key, AdminOperationState.Running); }), default);
        Assert.Equal(ClusterOperationState.Running, running.State);
        Assert.Equal(retry, store.AttemptKey("demo", kind, lifecycle, TimeSpan.Zero)); // an attempt in flight is never doubled
        Assert.Equal("lifecycle-2", store.AttemptKey("demo", kind, "lifecycle-2", TimeSpan.Zero));
    }

    private Task<ClusterOperation> Execute(ClusterOperationStore store, ClusterGatewayClient client) =>
        store.ExecuteGatewayAsync(_cluster, "cluster.save-all", "POST", "save-all", new SaveAllRequest(),
            "request-1", "test", client, CancellationToken.None);
    private static ClusterGatewayClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new HttpClient(new Handler(respond)));
    private static HttpResponseMessage Operation(string key, AdminOperationState state) => Response(HttpStatusCode.Accepted,
        new AdminEnvelope<AdminOperation>(1, DateTimeOffset.UtcNow,
            new AdminOperation("remote-1", "save-all", state, "test", key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null)));
    private static HttpResponseMessage Response<T>(HttpStatusCode code, T body)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(JsonSerializer.Serialize(body, Json)) };
        response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
