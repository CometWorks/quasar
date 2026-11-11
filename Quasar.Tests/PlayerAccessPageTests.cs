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
using Quasar.Components.Pages;
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
        var context = new QuasarBunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized(admin ? "admin" : "viewer");
        auth.SetPolicies(admin ? [QuasarPolicyNames.CanView, QuasarPolicyNames.CanManageSecurity] : [QuasarPolicyNames.CanView]);
        context.Services.AddSingleton(auth);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance));
        return context;
    }

    [Fact]
    public async Task ViewerCanOpenAccessAndLinkedAccountsWithoutLoadingPrivateSettings()
    {
        await using var context = Context(false);
        context.Services.AddSingleton<PlayerAccessCatalog>(_ => throw new Exception("Viewers must not load private access settings."));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Access>();
        Assert.Contains("Server access", page.Markup);
        Assert.Contains("Linked accounts", page.Markup);
        Assert.DoesNotContain("Save access settings", page.Markup);
        page.FindAll(".mud-tab").Single(tab => tab.TextContent.Trim() == "Linked accounts").Click();
        Assert.Contains("No player accounts are linked yet", page.Markup);
    }

    [Fact]
    public async Task AdminUsesBotGuildAndPreservesSavedRoleIdsWhileBotIsOffline()
    {
        await using var context = Context(true);
        var catalog = new PlayerAccessCatalog(Path.Combine(root, "access.json"), new EphemeralDataProtectionProvider());
        await catalog.SaveSettingsAsync(new()
        {
            DiscordClientId = "42", DiscordClientSecret = "legacy-secret-never-render",
            Policies = [new() { Target = "server:solo", GuildId = 77, RequiredRoles = [100], PriorityRoles = [200], Promotions = [new(300, 2)] }]
        });
        var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        await servers.UpsertAsync(new DedicatedServerDefinition { UniqueName = "solo", DisplayName = "Solo", WorldSaveName = "world" });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "Clusters") }).Build();
        var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, configuration);
        using var discord = new DiscordOptionsCatalog(NullLogger<DiscordOptionsCatalog>.Instance, new EphemeralDataProtectionProvider(), Path.Combine(root, "discord.json"));
        await discord.SaveAsync(new() { GuildId = 77 });
        var browser = new PlayerAuthBrowser(catalog, new HostedPanelService(null!, NullLogger<HostedPanelService>.Instance), new(), null!);
        var access = new PlayerAccessService(catalog, null!, browser, null!, servers, clusters);
        context.Services.AddSingleton(catalog); context.Services.AddSingleton(discord);
        context.Services.AddSingleton(browser); context.Services.AddSingleton(access);
        context.Services.AddSingleton(new PlayerAccessWorker(access, catalog, null!, clusters, null!, null!, browser, NullLogger<PlayerAccessWorker>.Instance));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Access>();
        Assert.Contains("auth.se-servers.net", page.Markup);
        Assert.Contains("Quasar's existing Discord bot", page.Markup);
        Assert.DoesNotContain("legacy-secret-never-render", page.Markup);
        Assert.DoesNotContain("Discord application ID", page.Markup);
        Assert.DoesNotContain("Comma-separated role IDs", page.Markup);
        Assert.Contains("Unavailable role (100)", page.Markup);
        Assert.Contains("Unavailable role (200)", page.Markup);
        Assert.Contains("Unavailable role (300)", page.Markup);
        await page.FindAll("button").Last(button => button.TextContent.Trim() == "Save access settings").ClickAsync();
        var saved = Assert.Single(catalog.GetSettings().Policies);
        Assert.Equal(77UL, saved.GuildId); Assert.Equal([100UL], saved.RequiredRoles); Assert.Equal([200UL], saved.PriorityRoles);
        Assert.Equal(new PlayerPromotionMapping(300, 2), Assert.Single(saved.Promotions));
        var revision = catalog.GetSettings().Revision;
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Add promotion role").Click();
        await page.FindAll("button").Last(button => button.TextContent.Trim() == "Save access settings").ClickAsync();
        Assert.Contains("Choose a Discord role for each promotion mapping", page.Markup);
        Assert.Equal(revision, catalog.GetSettings().Revision);
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies(QuasarPolicyNames.CanView);
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetAuthorized("viewer");
        page.WaitForAssertion(() => Assert.DoesNotContain("Save access settings", page.Markup));
    }

    public void Dispose() { cache.SetValue(null, previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
