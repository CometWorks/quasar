using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using Quasar.Components.Dashboard;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterOfflineTests
{
    [Theory]
    [InlineData(QuasarRoles.Editor)]
    [InlineData(QuasarRoles.Viewer)]
    public async Task AdvancedCommandUiCannotBypassOfflineAdministratorGuard(string role)
    {
        using var services = new ServiceCollection().AddLogging().AddAuthorization(Program.ConfigureAuthorizationPolicies).BuildServiceProvider();
        var permissions = new QuasarPermissionService(new RoleState(role), services.GetRequiredService<IAuthorizationService>(), new QuasarRoleMapper(new(), null!));
        var panel = new ClusterAdminPanel();
        typeof(ClusterAdminPanel).GetProperty("Permissions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, permissions);
        // Every mutable collaborator is absent: denied events must return before reaching any.
        await panel.SubmitAsync(ClusterAdminCommand.Create("trigger", new ClusterMaintenanceScope("node-1", 7), "profile"));
        await panel.SubmitAsync(ClusterAdminCommand.Create("offline-cache-clear", new OfflineCacheClearRequest("key", 0)));
    }

    private sealed class RoleState(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "fixture"))));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(168.01)]
    public async Task PredictionRejectsNonfiniteAndOutOfRangeBeforeTransport(double hours)
    {
        using var fixture = new OfflineGatewayFixture();
        var error = await Assert.ThrowsAsync<ClusterGatewayException>(() => fixture.Gateway.GetOfflinePredictionAsync(fixture.Cluster, "holder-1", 17, hours, default));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(168)]
    public async Task PredictionUsesOnlyAuthenticatedGatewayQueriesAndExactHolder(double hours)
    {
        using var fixture = new OfflineGatewayFixture();
        var prediction = (await fixture.Gateway.GetOfflinePredictionAsync(fixture.Cluster, "holder-1", 17, hours, default)).Data;
        Assert.Equal("17:2:3", prediction.SaveId);
        Assert.All(fixture.Requests, r =>
        {
            Assert.Equal("GET", r.Method);
            Assert.Equal("gateway.test", r.Uri.Host);
            Assert.Equal("Bearer fixture-offline-admin", r.Authorization);
            Assert.DoesNotContain("claim", r.Uri.AbsolutePath);
            Assert.DoesNotContain("save", r.Uri.AbsolutePath);
            Assert.DoesNotContain("wake", r.Uri.AbsolutePath);
        });
        var sent = fixture.Requests.Last();
        Assert.Contains("node=holder-1&partition=17&hours=" + hours, sent.Uri.Query);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Empty(fixture.SavedOperations);
    }

    [Fact]
    public async Task MissingCapabilityAndBadTargetFailClosedWithoutNodeFallback()
    {
        using var fixture = new OfflineGatewayFixture { Supported = false };
        Assert.Equal("offline_unsupported", (await Assert.ThrowsAsync<ClusterGatewayException>(() =>
            fixture.Gateway.GetOfflineCacheAsync(fixture.Cluster, default))).Code);
        Assert.All(fixture.Requests, r => Assert.EndsWith("/capabilities", r.Uri.AbsolutePath));
        Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<ClusterGatewayException>(() =>
            fixture.Gateway.GetOfflineDetailsAsync(fixture.Cluster, "", 0, default))).Code);
    }

    [Fact]
    public async Task ProfileAndPerKeyClearRetainExactRoutingEpochErrorsAndDurableReplay()
    {
        using var fixture = new OfflineGatewayFixture();
        var profile = await fixture.Commands.SubmitAsync("demo", ClusterAdminCommand.Create("trigger", new ClusterMaintenanceScope("node-1", 7), "profile"), "profile-1", "admin");
        Assert.Equal(ClusterOperationState.Succeeded, profile.State);
        var sent = Assert.Single(fixture.Requests.Where(r => r.Method == "POST"));
        Assert.Equal("/admin/v1/triggers/profile", sent.Uri.AbsolutePath);
        Assert.Contains("node=node-1&partition=7", sent.Uri.Query);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
        var clear = ClusterAdminCommand.Create("offline-cache-clear", new OfflineCacheClearRequest("rate/key?x=1", 4));
        var first = await fixture.Commands.SubmitAsync("demo", clear, "clear-1", "admin");
        await fixture.Commands.SubmitAsync("demo", clear, "clear-1", "admin");
        Assert.Equal(ClusterOperationState.Succeeded, first.State);
        var request = Assert.Single(fixture.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/cache/clear")));
        Assert.Equal("rate/key?x=1", JsonDocument.Parse(request.Body).RootElement.GetProperty("key").GetString());
        Assert.Equal(4, JsonDocument.Parse(request.Body).RootElement.GetProperty("expectedEpoch").GetInt32());
        fixture.MutationError = "epoch_mismatch";
        var stale = await fixture.Commands.SubmitAsync("demo", clear, "clear-2", "admin");
        Assert.Equal("epoch_mismatch", stale.Error?.Code);
        Assert.Equal(ClusterOperationState.Failed, stale.State);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => fixture.Commands.SubmitAsync("demo",
            ClusterAdminCommand.Create("trigger", new ClusterMaintenanceScope(), "profile"), "bad", "admin"));
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => fixture.Commands.SubmitAsync("demo",
            new("offline-cache-clear", JsonSerializer.SerializeToElement(new { key = "missing-epoch" })), "bad-epoch", "admin"));
    }

    [Fact]
    public async Task QueryOnlyGatewayCredentialCannotProfileOrClear()
    {
        using var fixture = new OfflineGatewayFixture { Scope = AdminScope.Query };
        var error = await Assert.ThrowsAsync<ClusterGatewayException>(() => fixture.Commands.SubmitAsync("demo",
            ClusterAdminCommand.Create("offline-cache-clear", new OfflineCacheClearRequest("key", 0)), "clear", "admin"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.All(fixture.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Theory]
    [InlineData(QuasarRoles.Admin, true)]
    [InlineData(QuasarRoles.Editor, false)]
    [InlineData(QuasarRoles.Viewer, false)]
    public async Task PanelShowsReadinessPresenceAndRestrictsAllDeliberateActions(string role, bool admin)
    {
        using var fixture = new OfflineGatewayFixture();
        await using var context = Context(fixture, role);
        var panel = context.Render<ClusterPartitionsPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        Assert.Contains("profiling", panel.Markup);
        Assert.Contains("Approximate", panel.Markup);
        Assert.Contains("unknown mod logic", panel.Markup);
        Assert.Contains("81", panel.Markup);
        Assert.Contains("remote-use conflict", panel.Markup);
        Assert.Equal(admin, panel.FindAll("button").Any(b => b.TextContent.Contains("Clear this key")));
        await panel.Find("button[aria-label='Inspect partition 17']").ClickAsync(new());
        Assert.Equal(admin, panel.FindAll("button").Any(b => b.TextContent.Contains("Dry run")));
        Assert.Contains("Exact Final 17:2:3", panel.Markup);
        if (admin)
        {
            await panel.FindAll("button").Single(b => b.TextContent.Contains("Dry run")).ClickAsync(new());
            Assert.Contains("Prediction only", panel.Markup);
            Assert.Contains("chips exhausted", panel.Markup);
        }
        Assert.All(fixture.Requests, r => Assert.Equal("GET", r.Method));
        if (Environment.GetEnvironmentVariable("QUASAR_OFFLINE_RENDER_DIR") is { Length: > 0 } path)
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "partitions-" + role + ".html"), panel.Markup);
            await File.WriteAllTextAsync(Path.Combine(path, "palette.css"), ":root{" + QuasarTheme.BuildCss(BrandingSettings.Normalize(null).DarkPalette, true) + "}");
        }
    }

    [Fact]
    public async Task MissingNodeDiagnosticsAndRemoteUseRefusalStayVisibleWithoutMutationByBrowsing()
    {
        using var fixture = new OfflineGatewayFixture { QueryError = "offline_unsupported" };
        await using var context = Context(fixture, QuasarRoles.Admin);
        var panel = context.Render<ClusterPartitionsPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        await panel.Find("button[aria-label='Inspect partition 7']").ClickAsync(new());
        Assert.Contains("offline_unsupported", panel.Markup);
        Assert.All(fixture.Requests, r => Assert.Equal("GET", r.Method));
        fixture.QueryError = null;
        fixture.MutationError = "remote-use";
        await panel.FindAll("button").Single(b => b.TextContent == "Profile").ClickAsync(new());
        Assert.Contains("remote-use", panel.Markup);
        Assert.Single(fixture.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task OldBackendAndEmptyStateKeepOrdinaryManagementUsable()
    {
        using var fixture = new OfflineGatewayFixture { Supported = false };
        await using var context = Context(fixture, QuasarRoles.Admin);
        var panel = context.Render<ClusterPartitionsPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        Assert.Contains("unsupported by this cluster package", panel.Markup);
        Assert.Contains("node-1", panel.Markup);
        Assert.DoesNotContain(fixture.Requests, r => r.Uri.AbsolutePath.Contains("/offline/"));
        fixture.Empty = true;
        await panel.FindAll("button").Single(b => b.TextContent.Contains("Refresh partitions")).ClickAsync(new());
        Assert.Contains("No partition rows reported", panel.Markup);
    }

    [Fact]
    public async Task ChangingSelectionCancelsAndDiscardsLateDetailOrPredictionResponse()
    {
        using var fixture = new OfflineGatewayFixture();
        await using var context = Context(fixture, QuasarRoles.Admin);
        var panel = context.Render<ClusterPartitionsPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Delay = async request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/details") && request.Uri.Query.Contains("partition=17"))
            { entered.TrySetResult(); await release.Task; }
        };
        var old = panel.Find("button[aria-label='Inspect partition 17']").ClickAsync(new());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await panel.Find("button[aria-label='Inspect partition 7']").ClickAsync(new());
        release.SetResult();
        await old;
        Assert.Contains("Current owner: node-1", panel.Markup);
        Assert.DoesNotContain("Exact Final 17:2:3", panel.Markup);
        Assert.DoesNotContain("Group 17", panel.Find("section[aria-label='Selected partition']").TextContent);
        fixture.Delay = null;
        await panel.Find("button[aria-label='Inspect partition 17']").ClickAsync(new());
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Delay = async request =>
        { if (request.Uri.AbsolutePath.EndsWith("/prediction")) { entered.TrySetResult(); await release.Task; } };
        var prediction = panel.FindAll("button").Single(b => b.TextContent.Contains("Dry run")).ClickAsync(new());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await panel.Find("button[aria-label='Inspect partition 7']").ClickAsync(new());
        release.SetResult(); await prediction;
        Assert.DoesNotContain("Prediction only", panel.Markup);
    }

    [Fact]
    public async Task CacheClearShowsEpochConflictAndRefreshesDisplayedEpoch()
    {
        using var fixture = new OfflineGatewayFixture { MutationError = "epoch_mismatch" };
        await using var context = Context(fixture, QuasarRoles.Admin);
        var panel = context.Render<ClusterPartitionsPanel>(p => p.Add(c => c.Cluster, fixture.Cluster));
        fixture.CacheEpoch = 5;
        await panel.FindAll("button").Single(b => b.TextContent.Contains("Clear this key")).ClickAsync(new());
        Assert.Contains("epoch_mismatch", panel.Markup);
        Assert.NotEmpty(panel.FindAll("button[aria-label='Clear rate key rate/key?x=1 at epoch 5']"));
        var sent = Assert.Single(fixture.Requests.Where(r => r.Method == "POST"));
        Assert.Contains("\"expectedEpoch\":4", sent.Body);
    }

    private static BunitContext Context(OfflineGatewayFixture fixture, string role)
    {
        var context = new BunitContext();
        var auth = context.AddAuthorization(); auth.SetAuthorized("operator"); auth.SetRoles(role);
        auth.SetPolicies(role == QuasarRoles.Admin ? [QuasarPolicyNames.ClusterMaintenance] : []);
        context.Services.AddSingleton(fixture.Gateway);
        context.Services.AddSingleton(fixture.Commands);
        context.Services.AddSingleton(fixture.Store);
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddSingleton<QuasarPermissionService>();
        return context;
    }
}

internal sealed class OfflineGatewayFixture : HttpMessageHandler
{
    internal sealed record Request(string Method, Uri Uri, string Body, string? Authorization, string? IdempotencyKey);
    private readonly List<Request> _requests = [];
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("QUASAR_OFFLINE_TEST_DIR") ?? Path.GetTempPath(), "offline-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string _credential = "QUASAR_TEST_OFFLINE_" + Guid.NewGuid().ToString("N");
    private readonly HttpClient _http;
    internal ClusterDefinition Cluster { get; }
    internal ClusterCatalog Catalog { get; }
    internal ClusterGatewayClient Gateway { get; }
    internal ClusterCommandService Commands { get; }
    internal ClusterOperationStore Store { get; }
    internal bool Supported = true, Empty;
    internal AdminScope Scope = AdminScope.Manage;
    internal int CacheEpoch = 4;
    internal string? MutationError;
    internal string? QueryError;
    internal Func<Request, Task>? Delay;
    internal string[] SavedOperations => Directory.GetFiles(Path.Combine(_root, "operations"), "*.json");
    internal Request[] Requests { get { lock (_requests) return _requests.ToArray(); } }
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    internal OfflineGatewayFixture()
    {
        Environment.SetEnvironmentVariable(_credential, "fixture-offline-admin");
        Directory.CreateDirectory(Path.Combine(_root, "catalog", "demo"));
        Cluster = new() { UniqueName = "demo", GatewayUrl = "http://gateway.test", GatewayAdminTokenEnvironmentVariable = _credential };
        File.WriteAllText(Path.Combine(_root, "catalog", "demo", "cluster.json"), JsonSerializer.Serialize(Cluster, Json));
        Catalog = new(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(_root, "catalog") }).Build());
        _http = new(this, disposeHandler: false); Gateway = new(_http); Store = new(Path.Combine(_root, "operations"));
        Commands = new(Catalog, Store, Gateway, new(_http));
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    {
        var request = new Request(message.Method.Method, message.RequestUri!, message.Content is null ? "" : await message.Content.ReadAsStringAsync(token),
            message.Headers.Authorization?.ToString(), message.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null);
        lock (_requests) _requests.Add(request);
        if (Delay is not null) await Delay(request);
        if (QueryError is not null && request.Uri.AbsolutePath.EndsWith("/details"))
        {
            var failure = new HttpResponseMessage(HttpStatusCode.NotImplemented)
            { Content = JsonContent.Create(new AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new(QueryError, "Node diagnostics unavailable.")), options: Json) };
            failure.Headers.Add("X-Cluster-Gateway-Protocol", "1"); return failure;
        }
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.Uri.Query);
        ulong partition = ulong.TryParse(query.GetValueOrDefault("partition"), out var id) ? id : 17;
        object data = request.Uri.AbsolutePath switch
        {
            "/admin/v1/capabilities" => new AdminCapabilities("registry", Scope, AdminProtocol.Operations, Supported ? new Dictionary<string, string> { ["offlinePartitions"] = "v1" } : new()),
            "/admin/v1/partitions" => Empty ? Array.Empty<PartitionSummary>() : new[]
            {
                new PartitionSummary(7, "node-1", 1, 1, DateTimeOffset.UtcNow.AddMinutes(1), false, false, false, ProfileGroups: [Group(7)], RemoteUsers: [81]),
                new PartitionSummary(17, null, 1, 2, DateTimeOffset.UtcNow.AddMinutes(1), false, false, false, Offline: true, ProfileGroups: [Group(17)], RemoteUsers: []),
            },
            "/admin/v1/offline/partitions" => new OfflinePartitionCatalog([new(17, "17:2:3", "node-1", DateTimeOffset.UtcNow, 225000, 1024, null, null, ["holder-1"], [Group(17)])]),
            "/admin/v1/offline/details" => new OfflinePartitionDetails(query["node"]!, 1, partition, partition == 17, partition == 17 ? "17:2:3" : null, [Group((long)partition)], [Result((long)partition)]),
            "/admin/v1/offline/cache" => new OfflineCacheView("wa-1", 3, [new("rate/key?x=1", 0.5, 0.48, 3, 0.04, true, CacheEpoch)]),
            "/admin/v1/offline/prediction" => new OfflinePrediction(query["node"]!, 1, partition, "17:2:3", double.Parse(query["hours"]!, System.Globalization.CultureInfo.InvariantCulture), [Result(17)]),
            "/admin/v1/triggers/profile" or "/admin/v1/offline/cache/clear" => new AdminOperation("op-" + request.IdempotencyKey, "fixture", MutationError is null ? AdminOperationState.Succeeded : AdminOperationState.Failed,
                "admin", request.IdempotencyKey!, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, MutationError is null ? null : new(MutationError, "State changed; refresh and review before retrying.")),
            _ => throw new InvalidOperationException("Unexpected gateway route: " + request.Uri.AbsolutePath),
        };
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AdminEnvelope<object>(1, DateTimeOffset.UtcNow, data), options: Json) };
        response.Headers.Add("X-Cluster-Gateway-Protocol", "1"); return response;
    }
    private static OfflineGroupSummary Group(long group) => new(group, [group], "profiling", "Approximate", ["unknown mod logic"], 225000, 225000, []);
    internal static OfflineCompensationResult Result(long group) => new(group, 225000, 1125000, 225000, 3, true, "Approximate", null, -0.25,
        new Dictionary<string, decimal> { ["MyObjectBuilder_Component/SteelPlate"] = 109 }, new Dictionary<long, double> { [31] = 2.5 }, new Dictionary<long, double> { [32] = 2.25 },
        new Dictionary<long, double>(), new Dictionary<long, Dictionary<string, decimal>> { [31] = new() { ["MyObjectBuilder_Ingot/Uranium"] = 0.08m } },
        new Dictionary<long, double> { [34] = 109 }, new Dictionary<long, decimal> { [32] = 1 }, [new(32, false, false, true, 0, 0)], [32]);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _http.Dispose(); Catalog.Dispose(); Environment.SetEnvironmentVariable(_credential, null); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        base.Dispose(disposing);
    }
}
