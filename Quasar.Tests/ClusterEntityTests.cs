using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Tests;

public sealed class ClusterEntityTests
{
    [Fact]
    public async Task WholeClusterPagesEveryNodeDeduplicatesCopiesAndRoutesActionsToTheOwner()
    {
        using var fixture = new Fixture();
        fixture.Data["a"] = Enumerable.Range(1, 2001).Select(id => Entity(id, "Local")).ToList();
        fixture.Data["b"] = [Entity(1, "Unowned", "Replica"), Entity(3000, "Local")];
        fixture.Data["wa"] = [Entity(4000, "Unowned")];
        fixture.AddAgent("old", "a", 1, false);
        fixture.Registry.UpsertHello(new() { AgentId = "foreign", ClusterMode = true, ClusterId = "other", ClusterSlot = "a",
            ClusterNodeId = "node-a", ClusterEpoch = 2, HostId = "host" }, "foreign", (_, _) => throw new Exception("Wrong cluster queried."));

        var result = await fixture.Service.QueryClusterAsync(fixture.Cluster, new());
        Assert.Empty(result.Warnings);
        Assert.Equal(2003, result.Rows.Count);
        Assert.Equal(3, result.QueriedNodes);
        Assert.Null(result.TotalEntityCount); // Shared world totals must not be added together.
        Assert.Equal(2, fixture.Commands.Count(command => command.AgentId == "a"));
        Assert.DoesNotContain(fixture.Commands, command => command.AgentId == "old");
        var shared = Assert.Single(result.Rows, row => row.Entity.EntityId == 1);
        Assert.Equal(2, shared.Observations.Count);
        Assert.Equal("Entity 1", shared.Entity.DisplayName);
        Assert.Equal("a", shared.ViewingSource.Agent.AgentId);
        Assert.Equal("a", shared.DeletionSource!.Agent.AgentId);
        await fixture.Service.DeleteEntityAsync(shared);
        var deletion = Assert.Single(fixture.Commands, command => command.CommandType == ServerCommandType.DeleteEntity);
        Assert.Equal("a", deletion.AgentId);
        Assert.Equal(1, JsonSerializer.Deserialize<EntityDeleteRequest>(deletion.Payload!, JsonOptions)!.EntityId);
    }

    [Fact]
    public async Task MissingAgentsAndFailedNodesLeaveUsableResultsAndExplicitPartialCoverage()
    {
        using var fixture = new Fixture();
        fixture.Data["a"] = [Entity(1, "Local")];
        fixture.Failures.Add("b");
        fixture.Registry.MarkDisconnected("connection-wa");
        var result = await fixture.Service.QueryClusterAsync(fixture.Cluster, new());
        Assert.Single(result.Rows);
        Assert.Equal(1, result.QueriedNodes);
        Assert.Equal(3, result.ExpectedNodes);
        Assert.Contains(result.Warnings, message => message.Contains("b:") && message.Contains("incomplete"));
        Assert.Contains(result.Warnings, message => message.Contains("world-authority:") && message.Contains("missing"));
    }

    [Theory]
    [InlineData(null, "Unowned")]
    [InlineData("Local", "Local")]
    [InlineData("Frozen", "Unowned")]
    public async Task UnknownConflictingAndMovingOwnershipNeverSelectsAnArbitraryDeleteTarget(string? first, string second)
    {
        using var fixture = new Fixture();
        fixture.Data["a"] = [Entity(1, first)];
        fixture.Data["b"] = [Entity(1, second)];
        var row = Assert.Single((await fixture.Service.QueryClusterAsync(fixture.Cluster, new())).Rows);
        Assert.Null(row.DeletionSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteEntityAsync(row));
        Assert.DoesNotContain(fixture.Commands, command => command.CommandType == ServerCommandType.DeleteEntity);
    }

    [Fact]
    public async Task NodeReplacementInvalidatesOldActionsAndRefreshUsesItsNewProcess()
    {
        using var fixture = new Fixture();
        fixture.Data["a"] = [Entity(1, "Local")];
        var row = Assert.Single((await fixture.Service.QueryClusterAsync(fixture.Cluster, new())).Rows);
        fixture.Registry.MarkDisconnected("connection-a");
        fixture.AddAgent("replacement", "a", 3);
        fixture.Data["replacement"] = [Entity(1, "Local")];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteEntityAsync(row));
        var refreshed = Assert.Single((await fixture.Service.QueryClusterAsync(fixture.Cluster, new())).Rows);
        Assert.Equal("replacement", refreshed.ViewingSource.Agent.AgentId);
    }

    [Fact]
    public async Task CancelledClusterQueryDoesNotReturnASuccessfulEmptyList()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.QueryClusterAsync(fixture.Cluster, new(), token: cancel.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClusterSelectorRespectsAccessAndViewerReceivesTheEntitysActualNode(bool excluded)
    {
        using var fixture = new Fixture();
        fixture.Data["a"] = [Entity(1, "Local")];
        fixture.Data["b"] = [Entity(2, "Local")];
        var root = Path.Combine(Path.GetTempPath(), "quasar-entity-ui-" + Guid.NewGuid().ToString("N"));
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = root }).Build());
        await clusters.CreateAsync(new("cluster", "Unified cluster", "http://gateway.test", ""), default);
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("viewer");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery);
        auth.SetClaims(new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new Claim(QuasarClaimTypes.Cluster, excluded ? "other-cluster" : "cluster"));
        context.Services.AddSingleton(clusters);
        context.Services.AddSingleton(fixture.Registry);
        context.Services.AddSingleton(fixture.Service);
        using var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        context.Services.AddSingleton(servers);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(QuasarUiPluginCatalog.Create(new ConfigurationBuilder().Build(),
            new HostingEnvironment { ContentRootPath = root }, loadPluginAssemblies: false));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Entities>();
        if (excluded)
        {
            Assert.DoesNotContain("Unified cluster", page.Markup);
            Assert.Empty(fixture.Commands);
            return;
        }
        await page.WaitForAssertionAsync(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
        var targets = page.FindComponents<MudSelectItem<string>>().Select(option => option.Instance.Value!).Where(value => value.StartsWith("cluster:") || value.StartsWith("agent:")).ToArray();
        Assert.Equal(["cluster:cluster"], targets);
        Assert.Contains("Unified cluster", page.Markup);
        Assert.DoesNotContain("epoch", page.Markup);
        Assert.DoesNotContain("Delete entity", page.Markup);
        var parameters = (IReadOnlyDictionary<string, object?>)typeof(Entities)
            .GetMethod("CreateEntityExtensionParameters", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, [fixture.Data["b"][0]])!;
        Assert.Equal("b", parameters["AgentId"]);
        Assert.Equal("Unified cluster", parameters["ServerName"]);
        Assert.Equal(true, parameters["CanViewEntity"]);
        var before = fixture.Commands.Count;
        auth.SetPolicies(QuasarPolicyNames.CanView);
        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Refresh").ClickAsync();
        Assert.Equal(before, fixture.Commands.Count);
        Assert.Empty(page.FindAll("tbody tr"));
    }

    private static EntitySummary Entity(long id, string? residence, string? name = null) =>
        new() { EntityId = id, DisplayName = name ?? $"Entity {id}", TypeTag = "Grid", ClusterResidence = residence };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed class Fixture : HttpMessageHandler
    {
        public AgentRegistry Registry { get; } = new(null!, null!, null!, null!);
        public Dictionary<string, List<EntitySummary>> Data { get; } = [];
        public HashSet<string> Failures { get; } = [];
        public ConcurrentQueue<ServerCommandEnvelope> Commands { get; } = [];
        public List<Admin.NodeStatus> Nodes { get; } = [];
        public ClusterDefinition Cluster { get; } = new() { UniqueName = "cluster", GatewayUrl = "http://gateway.test" };
        public EntityService Service { get; }
        private readonly HttpClient _http;

        public Fixture()
        {
            _http = new HttpClient(this, disposeHandler: false);
            Service = new(Registry, new(_http));
            AddAgent("b", "b", 2);
            AddAgent("a", "a", 2);
            AddAgent("wa", "world-authority", 2);
        }

        public void AddAgent(string id, string slot, long epoch, bool current = true)
        {
            Data[id] = [];
            Registry.UpsertHello(new() { AgentId = id, UniqueName = "same-process-name", ClusterMode = true,
                ClusterId = "cluster", ClusterSlot = slot, ClusterNodeId = "node-" + slot, ClusterEpoch = epoch, HostId = "host" },
                "connection-" + id, (message, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var command = message.Command!;
                    Commands.Enqueue(command);
                    var filter = JsonSerializer.Deserialize<EntityListFilter>(command.Payload!, JsonOptions)!;
                    var entities = Data[id].Where(entity => filter.TypeTag == "All" || entity.TypeTag == filter.TypeTag).ToArray();
                    Registry.UpdateCommandResult(new() { AgentId = id, CommandId = command.CommandId, Success = !Failures.Contains(id), Message = "Test node response",
                        Payload = command.CommandType == ServerCommandType.ListEntities ? JsonSerializer.Serialize(new EntityListResult {
                            Entities = entities.Skip(filter.Offset).Take(filter.Limit).ToList(), TotalCount = entities.Length, TotalEntityCount = entities.Length }, JsonOptions) : string.Empty },
                        "connection-" + id);
                    return Task.CompletedTask;
                });
            if (!current) return;
            Nodes.RemoveAll(node => node.SlotKey == slot);
            Nodes.Add(new("node-" + slot, slot, epoch, slot == "world-authority" ? Admin.NodeRole.WorldAuthority : Admin.NodeRole.Regular,
                Admin.NodeState.Active, "endpoint", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 0, 0, "host", true));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var status = new Admin.ClusterStatus("cluster", "world", Admin.ClusterPhase.Serving, Admin.StartupKind.Warm, null, null,
                false, false, [], new(0, Nodes.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new("node-world-authority", 2, 1, DateTimeOffset.UtcNow.AddMinutes(1)),
                Nodes.ToArray(), [], true, Admin.AdminHealth.Healthy, [], DateTimeOffset.UtcNow);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Admin.AdminEnvelope<Admin.ClusterStatus>(1, DateTimeOffset.UtcNow, status), options: JsonOptions) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
    }
}
