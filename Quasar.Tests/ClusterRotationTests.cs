using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterRotationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-rotation-" + Guid.NewGuid());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task RotationPublicationUsesDurableManageCommandAndPreservesIncarnation()
    {
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());
        await catalog.CreateAsync(new("demo", "Demo", "http://gateway.test", ""), default);
        var publication = new RotationPublication(7, "cluster-manager", DateTimeOffset.UtcNow.AddMinutes(2), true,
            [new("node-4", 42, DateTimeOffset.UtcNow.AddMinutes(10))]);
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/admin/v1/rotation", request.RequestUri!.AbsolutePath);
            Assert.NotEmpty(request.Headers.GetValues("Idempotency-Key"));
            var sent = JsonSerializer.Deserialize<RotationPublication>(await request.Content!.ReadAsStringAsync(), Json)!;
            Assert.Equal(publication.Nodes[0], sent.Nodes.Single());
            Assert.Equal(7, sent.ExpectedRevision);
            return Response(new AdminOperation("rotation-1", "rotation", AdminOperationState.Succeeded, "test", request.Headers.GetValues("Idempotency-Key").Single(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "rotation", null, null));
        }));
        var service = new ClusterCommandService(catalog, new ClusterOperationStore(Path.Combine(root, "operations")), new(http), null!);
        var result = await service.SubmitAsync("demo", ClusterAdminCommand.Create("rotation-set", publication), "request-1", "test");
        Assert.Equal(ClusterOperationState.Succeeded, result.State);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => service.SubmitAsync("demo",
            ClusterAdminCommand.Create("rotation-set", publication with { Nodes = [new("node-4", 0, DateTimeOffset.UtcNow)] }), "bad-epoch", "test"));
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => service.SubmitAsync("demo",
            ClusterAdminCommand.Create("rotation-set", publication with { ValidUntil = DateTimeOffset.UtcNow.AddHours(1) }), "bad-lease", "test"));
    }

    [Fact]
    public async Task TypedReadsPreservePublicationAndDiagnosticLease()
    {
        var cluster = new ClusterDefinition { GatewayUrl = "http://gateway.test", UniqueName = "demo" };
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/rotation")
            ? Response(new RotationStatus(3, "manager", DateTimeOffset.UtcNow.AddMinutes(1), true, [new("node-1", 9, DateTimeOffset.UtcNow)]))
            : Response(new DiagnosticsSnapshot { Current = new NodeDiagnostics { Name = "node-1", Epoch = 9, RotationExpiresAt = 12345, Rotation = RotationState.Assigned } }))));
        var client = new ClusterGatewayClient(http);
        Assert.Equal(9, (await client.GetRotationAsync(cluster, default)).Data.Nodes.Single().Epoch);
        var report = (await client.GetClientDiagnosticsAsync(cluster, "node-1", default)).Data;
        Assert.Equal(12345, report.Current.RotationExpiresAt);
        Assert.Equal(RotationState.Assigned, report.Current.Rotation);
    }

    private static HttpResponseMessage Response<T>(T value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new AdminEnvelope<T>(1, DateTimeOffset.UtcNow, value), Json)) };
        response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
