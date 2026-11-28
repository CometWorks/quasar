using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Client.Components;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Discord;
using Quasar.Services.Players;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class PlayerAccessPageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "access-page-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previous;

    public PlayerAccessPageTests() { previous = cache.GetValue(null); cache.SetValue(null, root); }

    private BunitContext Context(bool admin)
    {
        var context = new OperationsBrowserContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized(admin ? "admin" : "viewer");
        auth.SetPolicies(admin ? [QuasarPolicyNames.CanView, QuasarPolicyNames.CanManageSecurity] : [QuasarPolicyNames.CanView]);
        context.Services.AddSingleton(auth);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddSingleton(new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance));
        return context;
    }

    [Fact]
    public async Task ViewerCanOpenAccessAndLinkedAccountsWithoutLoadingPrivateSettings()
    {
        await using var context = Context(false);
        context.Services.AddSingleton<PlayerAccessCatalog>(_ => throw new Exception("Viewers must not load private access settings."));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Quasar.Client.Components.AccessView>();
        Assert.DoesNotContain("Save access settings", page.Markup);
        page.FindAll(".mud-tab").Single(tab => tab.TextContent.Trim() == "Linked accounts").Click();
    }

    [Fact]
    public void RoleSearchKeepsHiddenSelectionsAndShowsSavedRolesAfterDisconnect()
    {
        using var context = Context(true);
        const ulong supporter = 9007199254740993;
        const ulong moderator = 9007199254740995;
        var roles = new Dictionary<ulong, string>
        {
            [supporter] = $"Supporter ({supporter})",
            [moderator] = $"Moderator ({moderator})"
        };
        List<ulong> selected = [supporter];
        var picker = context.Render<DiscordRolePicker>(p => p
            .Add(c => c.Label, "Priority Discord roles")
            .Add(c => c.Help, "Priority access")
            .Add(c => c.Roles, roles)
            .Add(c => c.Value, selected)
            .Add(c => c.ValueChanged, values => selected = values));

        picker.Find("input[type=search]").Input("MODer");
        var match = Assert.Single(picker.FindAll("input[type=checkbox]"));
        Assert.Contains("Moderator", match.ParentElement!.TextContent);
        match.Change(true);
        Assert.Equal([supporter, moderator], selected);
        picker.Render(p => p.Add(c => c.Value, selected));
        picker.Find("input[type=search]").Input("4740993");
        Assert.Contains("Supporter", Assert.Single(picker.FindAll(".quasar-check")).TextContent);
        picker.Find("input[type=search]").Input("missing");
        Assert.Empty(picker.FindAll("input[type=checkbox]"));

        picker.Render(p => p.Add(c => c.Roles, new Dictionary<ulong, string>()));
        picker.Find("input[type=search]").Input("");
        Assert.Equal(2, picker.FindAll("input:checked").Count);
        picker.FindAll(".quasar-check").Single(row => row.TextContent.Contains($"Unavailable role ({moderator})"))
            .QuerySelector("input")!.Change(false);
        Assert.Equal([supporter], selected);
    }

    [Fact]
    public async Task AdminCanEnablePrivatePlayerAccessAndPreserveRoleIdsWhileBotIsOffline()
    {
        await using var context = Context(true);
        var catalog = new PlayerAccessCatalog(Path.Combine(root, "access.json"));
        await catalog.SaveSettingsAsync(new()
        {
            Policies = [new() { Target = "server:solo", GuildId = 77, RequiredRoles = [100], PriorityRoles = [200], Promotions = [new(300, 2)] }]
        });
        var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        await servers.UpsertAsync(new DedicatedServerDefinition { UniqueName = "solo", DisplayName = "Solo", WorldSaveName = "world" });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "Clusters") }).Build();
        var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, configuration);
        using var discord = new DiscordOptionsCatalog(NullLogger<DiscordOptionsCatalog>.Instance, new EphemeralDataProtectionProvider(), Path.Combine(root, "discord.json"));
        await discord.SaveAsync(new() { Enabled = true, GuildId = 77, BotToken = "fixture-token" });
        var access = new PlayerAccessService(catalog, null!, null!, servers, clusters);
        context.Services.AddSingleton(catalog); context.Services.AddSingleton(discord);
        context.Services.AddSingleton(access);
        context.Services.AddSingleton(new AgentRegistry(null!, null!, null!, null!));
        context.Services.AddSingleton(new PlayerAccessWorker(access, catalog, null!, clusters, null!, null!, NullLogger<PlayerAccessWorker>.Instance));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Quasar.Client.Components.AccessView>();
        Assert.Contains("auth.se-servers.net", page.Markup);
        Assert.DoesNotContain("Self-hosted public website", page.Markup);
        Assert.DoesNotContain("Verify saved website", page.Markup);
        Assert.DoesNotContain("Discord application ID", page.Markup);
        Assert.DoesNotContain("Comma-separated role IDs", page.Markup);
        Assert.Contains("Unavailable role (100)", page.Markup);
        Assert.Contains("Unavailable role (200)", page.Markup);
        Assert.Contains("Unavailable role (300)", page.Markup);
        Assert.Contains("/authorize", page.Markup);
        page.FindAll("input[type=search]")[0].Input("unmatched");
        var pickers = page.FindAll(".quasar-role-options");
        Assert.Equal(2, pickers.Count);
        Assert.Empty(pickers[0].QuerySelectorAll("input[type=checkbox]"));
        Assert.Contains("Unavailable role (200)", pickers[1].TextContent);
        page.FindAll("input[type=checkbox]").First().Change(true);
        await page.FindAll("button").Last(button => button.TextContent.Trim() == "Save access settings").ClickAsync();
        var saved = Assert.Single(catalog.GetSettings().Policies);
        Assert.True(saved.Enabled);
        Assert.Equal(77UL, saved.GuildId); Assert.Equal([100UL], saved.RequiredRoles); Assert.Equal([200UL], saved.PriorityRoles);
        Assert.Equal(new PlayerPromotionMapping(300, 2), Assert.Single(saved.Promotions));
        page.FindAll(".quasar-role-options")[1].QuerySelector("input:checked")!.Change(false);
        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Save access settings").ClickAsync();
        var edited = Assert.Single(catalog.GetSettings().Policies);
        Assert.Equal([100UL], edited.RequiredRoles);
        Assert.Empty(edited.PriorityRoles);
        var revision = catalog.GetSettings().Revision;
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Add promotion role").Click();
        await page.FindAll("button").Last(button => button.TextContent.Trim() == "Save access settings").ClickAsync();
        Assert.Equal(revision, catalog.GetSettings().Revision);
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies(QuasarPolicyNames.CanView);
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetAuthorized("viewer");
        var reopened = context.Render<AccessView>();
        Assert.DoesNotContain("Save access settings", reopened.Markup);
    }

    public void Dispose() { cache.SetValue(null, previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
