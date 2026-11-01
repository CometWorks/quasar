using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ClusterPlayerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-players-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    private readonly KnownPlayerCatalog _known;

    public ClusterPlayerTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        _known = new(NullLogger<KnownPlayerCatalog>.Instance);
    }

    [Fact]
    public async Task RegistryPresenceSurvivesNodeMovesDisconnectsAndReloadWithoutMergingStandaloneNames()
    {
        using var handler = new Gateway();
        using var http = new HttpClient(handler);
        using var tracker = new ClusterPlayerService(null!, new(http), _known, NullLogger<ClusterPlayerService>.Instance);
        var cluster = new ClusterDefinition { UniqueName = "test", DisplayName = "Unified cluster", GatewayUrl = "http://gateway.test" };
        _known.ObserveSnapshot(new() { UniqueName = "test", Players = [new() { SteamId = 7, DisplayName = "Standalone player" }] });
        await tracker.RefreshAsync(cluster, default);
        handler.Clients = [Gateway.Player with { Node = "replacement-node" }, Gateway.Player];
        await tracker.RefreshAsync(cluster, default);
        Assert.Single(tracker.GetSnapshot("test")!.Clients);
        Assert.Equal(2, _known.GetPlayers().Count);
        var record = Assert.Single(_known.GetPlayers(), player => player.IsCluster);
        Assert.Equal("test", record.UniqueName);
        Assert.Equal("Cluster player", record.DisplayName);
        Assert.Equal("cluster::test::7", record.PlayerKey);
        var lastSeen = record.LastSeenUtc;

        handler.Clients = [];
        handler.Banned = [7];
        await tracker.RefreshAsync(cluster, default);
        Assert.Empty(tracker.GetSnapshot("test")!.Clients);
        record = Assert.Single(_known.GetPlayers(), player => player.IsCluster);
        Assert.True(record.IsBanned);
        Assert.Equal(lastSeen, record.LastSeenUtc);
        Assert.True(tracker.GetSnapshot("test")!.CanRun("DELETE /admin/v1/admission/bans/{userId}"));
        handler.Offline = true;
        await tracker.RefreshAsync(cluster, default);
        Assert.False(tracker.GetSnapshot("test")!.Available);
        Assert.False(tracker.GetSnapshot("test")!.CanRun("POST /admin/v1/admission/bans"));
        Assert.Equal(2, _known.GetPlayers().Count);

        await PersistAsync();
        var restored = new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance);
        Assert.True(Assert.Single(restored.GetPlayers(), player => player.IsCluster).IsBanned);
        Assert.Equal(1, await _known.CleanServerAsync("test"));
        Assert.True(Assert.Single(_known.GetPlayers()).IsCluster);
        Assert.Equal(0, await _known.CleanAllAsync(playerKeys: new HashSet<string> { "test::7" }));
        Assert.Single(_known.GetPlayers());
        Assert.Equal(1, await _known.CleanServerAsync("test", isCluster: true));
    }

    [Fact]
    public async Task WrongRegistryIdentityCannotPopulateClusterHistory()
    {
        using var handler = new Gateway { ClusterId = "another-cluster" };
        using var http = new HttpClient(handler);
        using var tracker = new ClusterPlayerService(null!, new(http), _known, NullLogger<ClusterPlayerService>.Instance);
        await tracker.RefreshAsync(new() { UniqueName = "test", GatewayUrl = "http://gateway.test" }, default);
        Assert.Empty(_known.GetPlayers());
        Assert.False(tracker.GetSnapshot("test")!.Available);
        Assert.False(new ClusterPlayerSnapshot([Gateway.Player], Gateway.Capabilities, DateTimeOffset.UtcNow.AddMinutes(-1), null).Available);
        Assert.False(new ClusterPlayerSnapshot([], Gateway.Capabilities with { Scope = Admin.AdminScope.Query }, DateTimeOffset.UtcNow, null)
            .CanRun("POST /admin/v1/admission/bans"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MainPlayersPageShowsOneClusterAndRetainsHistoricalPlayers(bool manage, bool excluded)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("viewer");
        if (excluded) auth.SetClaims(new System.Security.Claims.Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new System.Security.Claims.Claim(QuasarClaimTypes.Cluster, "another-cluster"));
        auth.SetPolicies(manage ? [QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage]
            : [QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery]);
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(_known);
        context.Services.AddSingleton(new AgentRegistry(null!, null!, null!, null!));
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        context.Services.AddSingleton(clusters);
        var cluster = await clusters.CreateAsync(new("test", "Unified cluster", "http://gateway.test", ""), default);
        using var handler = new Gateway();
        using var http = new HttpClient(handler);
        var tracker = new ClusterPlayerService(clusters, new(http), _known, NullLogger<ClusterPlayerService>.Instance);
        context.Services.AddSingleton(tracker);
        context.Services.AddSingleton(new ClusterCommandService(clusters, new ClusterOperationStore(Path.Combine(_root, "operations")), new(http), null!));
        await tracker.RefreshAsync(cluster, default);
        context.Render<MudPopoverProvider>();
        var page = context.Render<Players>();
        if (excluded)
        {
            Assert.DoesNotContain("Unified cluster", page.Markup);
            Assert.DoesNotContain("Cluster player", page.Markup);
            return;
        }
        Assert.Single(page.FindAll("tbody tr"));
        Assert.Contains("Unified cluster", page.Find("tbody").TextContent);
        Assert.Contains("Cluster player", page.Find("tbody").TextContent);
        Assert.Contains("online", page.Find("tbody").TextContent);
        Assert.DoesNotContain("Player actions", page.Markup);
        handler.Clients = [];
        handler.Banned = [7];
        await page.InvokeAsync(() => tracker.RefreshAsync(cluster, default));
        Assert.Single(page.FindAll("tbody tr"));
        Assert.Contains("Cluster player", page.Find("tbody").TextContent);
        Assert.Contains("offline", page.Find("tbody").TextContent);
        if (manage)
        {
            var views = (System.Collections.IEnumerable)typeof(Players).GetMethod("BuildKnownPlayerViews", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, null)!;
            var view = views.Cast<object>().Single();
            var send = typeof(Players).GetMethod("SendClusterPlayerCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await page.InvokeAsync(() => (Task)send.Invoke(page.Instance, [view, "unban"])!);
            Assert.Equal("DELETE /admin/v1/admission/bans/7", Assert.Single(handler.Mutations));
            auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery);
            await page.InvokeAsync(() => (Task)send.Invoke(page.Instance, [view, "unban"])!);
            Assert.Single(handler.Mutations);
        }
        // Query access revocation removes cluster rows and their filter entry.
        auth.SetPolicies(QuasarPolicyNames.CanView);
        await page.InvokeAsync(() => tracker.RefreshAsync(cluster, default));
        page.WaitForAssertion(() => Assert.DoesNotContain("Cluster player", page.Markup));
    }

    private async Task PersistAsync()
    {
        (typeof(KnownPlayerCatalog).GetField("_saveDebounce", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_known) as CancellationTokenSource)?.Cancel();
        await (Task)typeof(KnownPlayerCatalog).GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_known, [CancellationToken.None])!;
    }

    public void Dispose()
    {
        PersistAsync().GetAwaiter().GetResult();
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Gateway : HttpMessageHandler
    {
        internal static readonly Admin.ClientSummary Player = new(7, "Cluster player", 70, "Admitted", false, "node-a", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        internal static readonly Admin.AdminCapabilities Capabilities = new("gateway", Admin.AdminScope.Manage, Admin.AdminProtocol.Operations, new Dictionary<string, string>());
        internal string ClusterId = "test";
        internal bool Offline;
        internal Admin.ClientSummary[] Clients = [Player];
        internal ulong[] Banned = [];
        internal List<string> Mutations = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("offline");
            if (request.Method != HttpMethod.Get)
            {
                Mutations.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                string key = request.Headers.GetValues("Idempotency-Key").Single();
                var operation = new Admin.AdminOperation("op", "unban", Admin.AdminOperationState.Succeeded, "viewer", key,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "7", null, null);
                var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Admin.AdminEnvelope<Admin.AdminOperation>(1, DateTimeOffset.UtcNow, operation)) };
                reply.Headers.Add("X-Cluster-Gateway-Protocol", "1");
                return Task.FromResult(reply);
            }
            object data = request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "clients" => Clients,
                "bans" => new Admin.AdmissionBans(Banned),
                "capabilities" => Capabilities,
                _ => JsonSerializer.Deserialize<Admin.ClusterStatus>("{\"clusterId\":\"" + ClusterId + "\",\"nodes\":[]}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Admin.AdminEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }
}
