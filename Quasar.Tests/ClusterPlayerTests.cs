using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Bunit.TestDoubles;
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
        context.Services.AddDirectCoreOperations();
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
        var popovers = context.Render<MudPopoverProvider>();
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
        if (manage)
        {
            page.Find("tbody .mud-menu button").Click();
            popovers.WaitForAssertion(() => Assert.Contains("Kick", popovers.Find(".mud-popover-open").TextContent));
            await page.InvokeAsync(() => tracker.RefreshAsync(cluster, default));
            Assert.Contains("Kick", popovers.Find(".mud-popover-open").TextContent);
            await page.FindComponent<MudMenu>().InvokeAsync(() => page.FindComponent<MudMenu>().Instance.CloseMenuAsync());
        }
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
            page.Find("tbody .mud-menu button").Click();
            popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Unban").Click();
            page.WaitForAssertion(() => Assert.Single(handler.Mutations));
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

    [Theory]
    [InlineData("query", "management scope")]
    [InlineData("offline", "Cannot reach")]
    [InlineData("unsupported", "does not support player actions")]
    [InlineData("stale", "status is stale")]
    public async Task UnavailableClusterActionsStillOpenAndExplainWhy(string mode, string message)
    {
        using var handler = new Gateway();
        if (mode == "query") handler.CurrentCapabilities = Gateway.Capabilities with { Scope = Admin.AdminScope.Query };
        if (mode == "unsupported") handler.CurrentCapabilities = Gateway.Capabilities with { Operations = [] };
        await using var context = await PlayerContextAsync(handler);
        var tracker = context.Services.GetRequiredService<ClusterPlayerService>();
        if (mode == "stale")
        {
            var snapshots = (System.Collections.Concurrent.ConcurrentDictionary<string, ClusterPlayerSnapshot>)typeof(ClusterPlayerService)
                .GetField("_snapshots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tracker)!;
            snapshots["test"] = tracker.GetSnapshot("test")! with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        }
        if (mode == "offline")
        {
            handler.Offline = true;
            await tracker.RefreshAsync(context.Services.GetRequiredService<ClusterCatalog>().GetCluster("test")!, default);
        }
        var popovers = context.Render<MudPopoverProvider>();
        var page = context.Render<Players>();
        var button = page.Find("tbody .mud-menu button");
        Assert.False(button.HasAttribute("disabled"));
        button.Click();
        var menu = popovers.Find(".mud-popover-open");
        Assert.Contains(message, menu.TextContent);
        if (mode == "unsupported") Assert.Contains("does not advertise cluster role changes", menu.TextContent);
        Assert.All(menu.QuerySelectorAll(".mud-menu-item"), item => Assert.Equal("true", item.GetAttribute("aria-disabled")));
        Assert.Empty(handler.Mutations);
    }

    [Theory]
    [InlineData("kick", true)]
    [InlineData("ban", true)]
    [InlineData("kick", false)]
    [InlineData("ban", false)]
    public async Task ClusterActionsConfirmAndUseClusterPlayerIdentity(string action, bool confirm)
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim().Equals(action, StringComparison.OrdinalIgnoreCase)).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Cluster player (7)", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Trim().Equals(confirm ? action : "Cancel", StringComparison.OrdinalIgnoreCase)).Click();
        if (confirm)
        {
            page.WaitForAssertion(() => Assert.Single(handler.Mutations));
            Assert.Equal(action == "kick" ? "POST /admin/v1/clients/7/kick" : "POST /admin/v1/admission/bans", handler.Mutations.Single());
            Assert.Equal("gateway.test", handler.MutationHost);
            if (action == "ban") Assert.Equal(7UL, JsonSerializer.Deserialize<Admin.BanRequest>(handler.MutationBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.UserId);
        }
        else Assert.Empty(handler.Mutations);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Scripter")]
    [InlineData("Moderator")]
    [InlineData("SpaceMaster")]
    [InlineData("Admin")]
    public async Task ClusterRolesConfirmAndRouteToGatewayWithExactPlayer(string level)
    {
        using var handler = new Gateway { Moderation = new([new(7, level == "None" ? Admin.PlayerPromotionLevel.Admin : Admin.PlayerPromotionLevel.None)], []) };
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == level).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Cluster player (7)", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Set role to " + level).Click();
        page.WaitForAssertion(() => Assert.Single(handler.Mutations));
        Assert.Equal("PUT /admin/v1/players/7/promotion", handler.Mutations.Single());
        Assert.Equal("gateway.test", handler.MutationHost);
        Assert.Equal(level, JsonDocument.Parse(handler.MutationBody!).RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public async Task ClusterCooldownIsObservedForOfflinePlayerAndClearedAtAdmissionOwner()
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        handler.Clients = [];
        handler.Moderation = new([new(7, Admin.PlayerPromotionLevel.Scripter)], [new(7, DateTimeOffset.UtcNow.AddMinutes(4))]);
        await context.Services.GetRequiredService<ClusterPlayerService>().RefreshAsync(
            context.Services.GetRequiredService<ClusterCatalog>().GetCluster("test")!, default);
        var popovers = context.Render<MudPopoverProvider>();
        var page = context.Render<Players>();
        Assert.Contains("kicked (", page.Markup);
        Assert.Contains("offline", page.Find("tbody").TextContent);
        Assert.Equal("Scripter", _known.GetPlayers().Single().PromoteLevel);
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Clear kick cooldown").Click();
        page.WaitForAssertion(() => Assert.Equal("DELETE /admin/v1/admission/kicks/7", Assert.Single(handler.Mutations)));
        Assert.Equal("gateway.test", handler.MutationHost);
    }

    [Theory]
    [InlineData("old-gateway")]
    [InlineData("old-wa")]
    public async Task OlderPeersDisableNewActionsWithoutLosingKickOrBan(string mode)
    {
        using var handler = new Gateway();
        if (mode == "old-gateway") handler.CurrentCapabilities = Gateway.Capabilities with { Operations = Gateway.Capabilities.Operations
            .Where(operation => !operation.Contains("promotion") && !operation.Contains("moderation") && !operation.Contains("admission/kicks")).ToArray() };
        else handler.Moderation = new(null, [], "World Authority promotion state is unavailable.");
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        var items = popovers.FindAll(".mud-menu-item");
        Assert.Equal("true", items.Single(item => item.TextContent.Trim() == "Admin").GetAttribute("aria-disabled"));
        Assert.NotEqual("true", items.Single(item => item.TextContent.Trim() == "Kick").GetAttribute("aria-disabled"));
        Assert.NotEqual("true", items.Single(item => item.TextContent.Trim() == "Ban").GetAttribute("aria-disabled"));
        Assert.Contains(mode == "old-gateway" ? "does not advertise" : "promotion state is unavailable", popovers.Markup);
        Assert.Empty(handler.Mutations);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("permission")]
    [InlineData("cluster-scope")]
    [InlineData("capability")]
    [InlineData("wa-state")]
    public async Task ClusterRoleConfirmationRechecksAuthorityAndCanBeCancelled(string change)
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Admin").Click();
        var auth = context.Services.GetRequiredService<BunitAuthorizationContext>();
        if (change == "permission") auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery);
        if (change == "cluster-scope") auth.SetClaims(new System.Security.Claims.Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new System.Security.Claims.Claim(QuasarClaimTypes.Cluster, "another"));
        if (change == "capability") handler.CurrentCapabilities = Gateway.Capabilities with { Scope = Admin.AdminScope.Query };
        if (change == "wa-state") handler.Moderation = new(null, [], "WA unavailable");
        await context.Services.GetRequiredService<ClusterPlayerService>().RefreshAsync(
            context.Services.GetRequiredService<ClusterCatalog>().GetCluster("test")!, default);
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == (change == "cancel" ? "Cancel" : "Set role to Admin")).Click();
        Assert.Empty(handler.Mutations);
    }

    [Theory]
    [InlineData("set-player-promotion")]
    [InlineData("clear-kick-cooldown")]
    public async Task NewCommandsValidateCapabilitiesIdentityAndReplayIdempotently(string action)
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var service = context.Services.GetRequiredService<ClusterCommandService>();
        var request = ClusterAdminCommand.Create(action, action == "set-player-promotion" ? new Admin.SetPlayerPromotionRequest(Admin.PlayerPromotionLevel.Admin, 0) : null, "7");
        var first = await service.SubmitAsync("test", request, "stable", "admin");
        var replay = await service.SubmitAsync("test", request, "stable", "admin");
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Single(handler.Mutations);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => service.SubmitAsync("test", request with { Target = "8" }, "stable", "admin"));
        handler.CurrentCapabilities = Gateway.Capabilities with { Operations = [] };
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => service.SubmitAsync("test", request, "unsupported", "admin"));
        handler.CurrentCapabilities = Gateway.Capabilities;
        handler.ClusterId = "another";
        Assert.Equal("cluster_identity_mismatch", (await Assert.ThrowsAsync<ClusterGatewayException>(() => service.SubmitAsync("test", request, "wrong", "admin"))).Code);
        Assert.Single(handler.Mutations);
    }

    [Theory]
    [InlineData("0", "Admin")]
    [InlineData("someone", "Admin")]
    [InlineData("7", "Owner")]
    [InlineData("7", "5")]
    public async Task InvalidClusterRoleTargetsAndValuesNeverReachGateway(string target, string level)
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var command = ClusterAdminCommand.Create("set-player-promotion", new { level, expectedRevision = 0 }, target);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => context.Services.GetRequiredService<ClusterCommandService>().SubmitAsync("test", command, "invalid", "admin"));
        Assert.Empty(handler.Mutations);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Clear kick cooldown")]
    public async Task NewPlayerActionFailuresRemainVisible(string label)
    {
        using var handler = new Gateway { MutationFails = true,
            Moderation = new([], [new(7, DateTimeOffset.UtcNow.AddMinutes(4))]) };
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == label).Click();
        if (label == "Admin") dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Set role to Admin").Click();
        page.WaitForAssertion(() => Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
            snackbar => snackbar.Message == "Gateway refused moderation." && snackbar.Severity == Severity.Error));
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("permission")]
    [InlineData("cluster-scope")]
    [InlineData("presence")]
    public async Task ClusterChangesWhileConfirmingStopCommand(string change)
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Kick").Click();
        if (change == "capability") handler.CurrentCapabilities = Gateway.Capabilities with { Scope = Admin.AdminScope.Query };
        if (change == "presence") handler.Clients = [];
        var auth = context.Services.GetRequiredService<BunitAuthorizationContext>();
        if (change == "permission") auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery);
        if (change == "cluster-scope") auth.SetClaims(new System.Security.Claims.Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new System.Security.Claims.Claim(QuasarClaimTypes.Cluster, "another-cluster"));
        await context.Services.GetRequiredService<ClusterPlayerService>().RefreshAsync(
            context.Services.GetRequiredService<ClusterCatalog>().GetCluster("test")!, default);
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Kick").Click();
        if (change is "capability" or "presence")
            page.WaitForAssertion(() => Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
                snackbar => snackbar.Message.Contains(change == "capability" ? "management scope" : "no longer connected")));
        Assert.Empty(handler.Mutations);
    }

    [Fact]
    public async Task ClusterFailureIsShownWithGatewayReason()
    {
        using var handler = new Gateway { MutationFails = true };
        await using var context = await PlayerContextAsync(handler);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.Find("tbody .mud-menu button").Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Kick").Click();
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Kick").Click();
        page.WaitForAssertion(() => Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
            snackbar => snackbar.Message == "Gateway refused moderation." && snackbar.Severity == Severity.Error));
    }

    [Theory]
    [InlineData("None", ServerCommandType.SetPlayerPromoteLevel)]
    [InlineData("Scripter", ServerCommandType.SetPlayerPromoteLevel)]
    [InlineData("Moderator", ServerCommandType.SetPlayerPromoteLevel)]
    [InlineData("SpaceMaster", ServerCommandType.SetPlayerPromoteLevel)]
    [InlineData("Admin", ServerCommandType.SetPlayerPromoteLevel)]
    [InlineData("Kick", ServerCommandType.KickPlayer)]
    [InlineData("Ban", ServerCommandType.BanPlayer)]
    [InlineData("Unban", ServerCommandType.UnbanPlayer)]
    [InlineData("Clear kick cooldown", ServerCommandType.ClearKickCooldown)]
    public async Task StandaloneActionsUseExactAgentAndReportItsResult(string label, ServerCommandType type)
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var registry = context.Services.GetRequiredService<AgentRegistry>();
        var commands = ConnectStandalone(registry);
        if (label == "None") _known.ApplyCommandOutcome(new() { UniqueName = "test", SteamId = 7,
            CommandType = ServerCommandType.SetPlayerPromoteLevel, Text = "Admin" }, new() { Success = true });
        if (label == "Unban") _known.ApplyCommandOutcome(new() { UniqueName = "test", SteamId = 7,
            CommandType = ServerCommandType.BanPlayer }, new() { Success = true });
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        long steamId = type == ServerCommandType.ClearKickCooldown ? 8 : 7;
        var row = page.FindAll("tbody tr").Single(row => row.TextContent.Contains(steamId == 8 ? "Kicked solo" : "Online solo"));
        row.QuerySelector(".mud-menu button")!.Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == label).Click();
        if (type is ServerCommandType.KickPlayer or ServerCommandType.BanPlayer)
            dialogs.FindAll("button").Single(button => button.TextContent.Trim() == label).Click();
        page.WaitForAssertion(() => Assert.Single(commands));
        var command = commands.Single();
        Assert.Equal("solo-agent", command.AgentId);
        Assert.Equal("solo-server", command.ServerId);
        Assert.Equal("test", command.UniqueName); // Same unique name and Steam id as the cluster.
        Assert.Equal(steamId, command.SteamId);
        Assert.Equal(type, command.CommandType);
        Assert.Equal(type == ServerCommandType.SetPlayerPromoteLevel ? label : "", command.Text);
        page.WaitForAssertion(() => Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
            snackbar => snackbar.Message == "Agent applied action." && snackbar.Severity == Severity.Success));
        Assert.Empty(handler.Mutations);
    }

    [Fact]
    public async Task AgentFailureIsShownInsteadOfOnlyReportingCommandSent()
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        ConnectStandalone(context.Services.GetRequiredService<AgentRegistry>(), success: false);
        var popovers = context.Render<MudPopoverProvider>();
        var page = context.Render<Players>();
        page.FindAll("tbody tr").Single(row => row.TextContent.Contains("Online solo")).QuerySelector(".mud-menu button")!.Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Admin").Click();
        page.WaitForAssertion(() => Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
            snackbar => snackbar.Message == "Session not ready." && snackbar.Severity == Severity.Error));
    }

    [Fact]
    public async Task DisconnectWhileConfirmingDoesNotSendToAnotherAgent()
    {
        using var handler = new Gateway();
        await using var context = await PlayerContextAsync(handler);
        var registry = context.Services.GetRequiredService<AgentRegistry>();
        var commands = ConnectStandalone(registry);
        var popovers = context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<Players>();
        page.FindAll("tbody tr").Single(row => row.TextContent.Contains("Online solo")).QuerySelector(".mud-menu button")!.Click();
        popovers.FindAll(".mud-menu-item").Single(item => item.TextContent.Trim() == "Kick").Click();
        await page.InvokeAsync(() => registry.MarkDisconnected("solo-connection"));
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Kick").Click();
        page.WaitForAssertion(() => Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
            snackbar => snackbar.Message.Contains("not connected")));
        Assert.Empty(commands);
        Assert.Empty(handler.Mutations);
    }

    private List<ServerCommandEnvelope> ConnectStandalone(AgentRegistry registry, bool success = true)
    {
        var commands = new List<ServerCommandEnvelope>();
        registry.UpsertHello(new() { AgentId = "solo-agent", UniqueName = "test", ServerId = "solo-server" }, "solo-connection", (message, _) =>
        {
            commands.Add(message.Command!);
            registry.UpdateCommandResult(new() { CommandId = message.Command!.CommandId, AgentId = "solo-agent",
                UniqueName = "test", ServerId = "solo-server", Success = success,
                Message = success ? "Agent applied action." : "Session not ready." }, "solo-connection");
            return Task.CompletedTask;
        });
        // Observe the kicked player before disconnect, then the live server with its cooldown.
        _known.ObserveSnapshot(new() { UniqueName = "test", Players = [new() { SteamId = 8, DisplayName = "Kicked solo" }] });
        registry.UpdateSnapshot(new() { AgentId = "solo-agent", UniqueName = "test", ServerId = "solo-server", TelemetryPaused = true,
            Players = [new() { SteamId = 7, DisplayName = "Online solo", PromoteLevel = "None" }],
            KickedPlayers = [new() { SteamId = 8, RemainingCooldownMs = 60000 }] }, "solo-connection");
        return commands;
    }

    private async Task<BunitContext> PlayerContextAsync(Gateway handler)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers,
            QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage);
        context.Services.AddSingleton(auth);
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddDirectCoreOperations();
        context.Services.AddSingleton(_known);
        context.Services.AddSingleton(new AgentRegistry(_known, null!, null!, null!));
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        context.Services.AddSingleton(clusters);
        var cluster = await clusters.CreateAsync(new("test", "Unified cluster", "http://gateway.test", ""), default);
        var http = new HttpClient(handler, disposeHandler: false);
        context.Services.AddSingleton(http);
        var gateway = new ClusterGatewayClient(http);
        var tracker = new ClusterPlayerService(clusters, gateway, _known, NullLogger<ClusterPlayerService>.Instance);
        context.Services.AddSingleton(tracker);
        context.Services.AddSingleton(new ClusterCommandService(clusters, new ClusterOperationStore(Path.Combine(_root, "operations")), gateway, null!));
        await tracker.RefreshAsync(cluster, default);
        return context;
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

    internal sealed class Gateway : HttpMessageHandler
    {
        internal static readonly Admin.ClientSummary Player = new(7, "Cluster player", 70, "Admitted", false, "node-a", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        internal static readonly Admin.AdminCapabilities Capabilities = new("gateway", Admin.AdminScope.Manage, Admin.AdminProtocol.Operations, new Dictionary<string, string>());
        internal string ClusterId = "test";
        internal bool Offline;
        internal Admin.AdminCapabilities CurrentCapabilities = Capabilities;
        internal string? MutationHost;
        internal string? MutationBody;
        internal bool MutationFails;
        internal Admin.ClientSummary[] Clients = [Player];
        internal ulong[] Banned = [];
        internal Admin.PlayerModerationStatus Moderation = new([], []);
        internal List<string> Mutations = [];
        internal Admin.ChatMessage[] Chat = [];
        internal Admin.NodeStatus[] Nodes = [];
        internal Admin.AdminEvent[] Events = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("offline");
            if (request.Method != HttpMethod.Get)
            {
                Mutations.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                MutationHost = request.RequestUri.Host;
                MutationBody = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult();
                string key = request.Headers.GetValues("Idempotency-Key").Single();
                var operation = new Admin.AdminOperation("op", "unban", Admin.AdminOperationState.Succeeded, "viewer", key,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "7", null, null);
                if (MutationFails) operation = operation with { State = Admin.AdminOperationState.Failed,
                    Error = new("denied", "Gateway refused moderation.") };
                var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Admin.AdminEnvelope<Admin.AdminOperation>(1, DateTimeOffset.UtcNow, operation)) };
                reply.Headers.Add("X-Cluster-Gateway-Protocol", "1");
                return Task.FromResult(reply);
            }
            object data = request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "clients" => Clients,
                "bans" => new Admin.AdmissionBans(Banned),
                "capabilities" => CurrentCapabilities,
                "moderation" => Moderation,
                "history" => new Admin.ChatHistory(Chat.Where(message => message.Seq
                    > long.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["cursor"]!)).ToArray(),
                    Chat.Length == 0 ? 0 : Chat.Max(message => message.Seq), 1),
                "events" => new Admin.AdminEventPage(Events, Events.LastOrDefault()?.Seq ?? 0, Events.FirstOrDefault()?.Seq ?? 0),
                _ => JsonSerializer.Deserialize<Admin.ClusterStatus>("{\"clusterId\":\"" + ClusterId + "\",\"nodes\":[]}", new JsonSerializerOptions(JsonSerializerDefaults.Web))! with { Nodes = Nodes },
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Admin.AdminEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }
}
