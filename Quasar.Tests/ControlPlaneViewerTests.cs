using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components;
using Quasar.Components.Layout;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Discord;
using Quasar.Services.Plugins;
using Quasar.Services.PluginSdk;
using Xunit;
using DiscordPage = Quasar.Components.Pages.Discord;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ControlPlaneViewerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "viewer-pages-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;

    public ControlPlaneViewerTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
    }

    private BunitContext Context(bool editor = false)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized(editor ? "editor" : "viewer");
        auth.SetPolicies(editor
            ? [QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditConfigs, QuasarPolicyNames.CanControlServers, QuasarPolicyNames.CanManageDiscord]
            : [QuasarPolicyNames.CanView]);
        context.Services.AddSingleton(auth);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        context.Services.AddSingleton(new AgentRegistry(null!, null!, null!, null!));
        return context;
    }

    private static T Registered<T>(BunitContext context) where T : class =>
        (T)context.Services.Last(service => service.ServiceType == typeof(T)).ImplementationInstance!;

    [Fact]
    public void ViewerNavigationIncludesEveryBuiltInControlPlanePage()
    {
        using var context = Context();
        context.Services.AddSingleton(QuasarUiPluginCatalog.Create(new ConfigurationBuilder().Build(),
            new HostingEnvironment { ContentRootPath = _root }, loadPluginAssemblies: false));
        var page = context.Render<NavMenu>();
        var links = page.FindAll("a").Select(a => a.GetAttribute("href")).ToArray();
        foreach (var route in new[] { "/", "/configs", "/world-templates", "/plugins", "/players", "/chat", "/entities", "/analytics", "/discord", "/settings/server-list", "/hosts", "/notifications" })
            Assert.Contains(route, links);
        foreach (var route in new[] { "/settings/security", "/settings/ui-plugins", "/backup", "/settings/updates", "/settings/appearance" })
            Assert.DoesNotContain(route, links);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigProfilesRemainBrowsableAndOnlyEditorsReceiveTheEditor(bool editor)
    {
        await using var context = Context(editor);
        var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var profile = new QuasarConfigProfile { Name = "Survival", Description = "Visible description", RootSettings = new() { ServerPassword = "secret-game-password" },
            Plugins = [new() { PluginId = "example", DisplayName = "Example plugin" }],
            Mods = [new() { WorkshopId = 123, DisplayName = "Example mod" }] };
        await profiles.UpsertAsync(profile);
        context.Services.AddSingleton(profiles);
        var devFolders = new QuasarDevFolderCatalog(NullLogger<QuasarDevFolderCatalog>.Instance);
        context.Services.AddSingleton(devFolders);
        var pluginHubs = new QuasarPluginHubCatalog(NullLogger<QuasarPluginHubCatalog>.Instance);
        context.Services.AddSingleton(pluginHubs);
        context.Services.AddSingleton(new QuasarPluginCatalogService(NullLogger<QuasarPluginCatalogService>.Instance, null!, devFolders, pluginHubs));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        var credentials = new SteamWorkshopCredentialsCatalog(NullLogger<SteamWorkshopCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider());
        await credentials.SaveAsync(new() { WebApiKey = "secret-workshop-key", SteamPassword = "secret-steam-password" });
        context.Services.AddSingleton(credentials);
        context.Render<MudPopoverProvider>();
        var page = context.Render<Configs>(p => p.Add(x => x.InitialProfileId, profile.ConfigProfileId));
        Assert.Contains("Survival", page.Markup);
        Assert.Contains("Visible description", page.Markup);
        Assert.Equal(editor, page.FindAll("button").Any(b => b.TextContent.Trim() == "Save Profile"));
        Assert.Equal(editor, page.FindAll("button").Any(b => b.TextContent.Trim() == "Create profile"));
        if (!editor)
        {
            Assert.DoesNotContain("secret-", page.Markup);
            Assert.Contains("Configured (hidden)", page.Markup);
            Assert.Contains("Example mod", page.Markup);
            Assert.Contains("Example plugin", page.Markup);
            page.Find("input[type=search]").Input("max players");
            Assert.Contains("Max Players", page.Markup);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscordViewerGetsStatusAndBindingsWithoutCredentialsOrActions(bool editor)
    {
        await using var context = Context(editor);
        var options = new DiscordOptionsCatalog(
            NullLogger<DiscordOptionsCatalog>.Instance,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        await options.SaveAsync(new() { BotToken = "secret-discord-token", Enabled = true, GuildId = 12345 });
        context.Services.AddSingleton(options);
        context.Services.AddSingleton(new DeathMessagesCatalog(NullLogger<DeathMessagesCatalog>.Instance));
        context.Services.AddSingleton(new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(_root, "clusters") }).Build()));
        var registry = Registered<AgentRegistry>(context);
        var supervisor = new DedicatedServerSupervisor(Registered<DedicatedServerCatalog>(context), registry, null!, null!, null!, new(), null!, NullLogger<DedicatedServerSupervisor>.Instance);
        context.Services.AddSingleton(supervisor);
        var bridge = new DiscordClusterBridge(Registered<ClusterCatalog>(context), null!, null!, registry, null!, null!, NullLogger<DiscordClusterBridge>.Instance);
        context.Services.AddSingleton(new DiscordBotService(options, registry, supervisor, null!, null!, null!, null!, null!, null!, null!, NullLogger<DiscordBotService>.Instance, bridge));
        context.Render<MudPopoverProvider>();
        var page = context.Render<DiscordPage>();
        Assert.Contains("12345", page.Markup);
        Assert.Equal(editor, page.FindAll("button").Any(b => b.TextContent.Trim() == "Save Bot Settings"));
        Assert.Equal(editor, page.FindAll("button").Any(b => b.TextContent.Trim() == "Console Logs"));
        if (!editor) Assert.DoesNotContain("secret-discord-token", page.Markup);
    }

    [Fact]
    public async Task ViewerCanRefreshChatButCannotSendCommandsEvenAfterAnEditorSessionIsDemoted()
    {
        await using var context = Context(editor: true);
        var registry = Registered<AgentRegistry>(context);
        var sent = new List<ServerCommandEnvelope>();
        registry.UpsertHello(new() { AgentId = "agent", UniqueName = "server" }, "connection", (message, _) =>
        { sent.Add(message.Command!); return Task.CompletedTask; });
        context.Services.AddSingleton(new DedicatedServerSupervisor(Registered<DedicatedServerCatalog>(context), registry, null!, null!, null!, new(), null!, NullLogger<DedicatedServerSupervisor>.Instance));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Chat>();
        Assert.Contains(page.FindAll("button"), b => b.TextContent.Trim() == "Send");
        var auth = context.Services.GetRequiredService<BunitAuthorizationContext>();
        auth.SetPolicies(QuasarPolicyNames.CanView);
        auth.SetAuthorized("viewer");
        await page.WaitForAssertionAsync(() => Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Trim() is "Send" or "Save" or "Restart"));
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh").ClickAsync();
        Assert.Equal(ServerCommandType.Refresh, Assert.Single(sent).CommandType);
        await page.InvokeAsync(() => (Task)typeof(Chat).GetMethod("SendAgentCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, [ServerCommandType.SendChat, "!dangerous command"])!);
        Assert.Single(sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginInventoryDoesNotExposeUnclassifiedConfigurationValues(bool cluster)
    {
        await using var context = Context();
        var registry = Registered<AgentRegistry>(context);
        registry.UpsertHello(new() { AgentId = "agent", UniqueName = "server", ClusterMode = cluster, ClusterId = cluster ? "fleet" : "" }, "connection", (_, _) => Task.CompletedTask);
        var configs = new PluginConfigService(registry, NullLogger<PluginConfigService>.Instance);
        configs.IngestSnapshot(new() { AgentId = "agent", Plugins = [new() { PluginId = "plugin", DisplayName = "Example plugin", ConfigJson = "{\"token\":\"secret-plugin-token\"}" }] }, "connection");
        context.Services.AddSingleton(configs);
        context.ComponentFactories.AddStub<PluginLogPanel>();
        context.Render<MudPopoverProvider>();
        var page = context.Render<Plugins>();
        await page.InvokeAsync(() => page.FindComponent<MudExpansionPanel>().Instance.ExpandedChanged.InvokeAsync(true));
        Assert.Contains("Example plugin", page.Markup);
        Assert.Contains("may contain credentials", page.Markup);
        Assert.DoesNotContain("secret-plugin-token", page.Markup);
        Assert.Empty(page.FindComponents<PluginConfigEditor>());
    }

    [Fact]
    public async Task ViewerCanBrowseWorldsWithoutImportOrDeleteControls()
    {
        await using var context = Context();
        var worlds = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await worlds.ImportAsync("Viewer world", "World description", source);
        context.Services.AddSingleton(worlds);
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        context.Services.AddSingleton(new WorldTemplateImportLocationService(new(), new BrowserStorageService(context.JSInterop.JSRuntime)));
        var page = context.Render<WorldTemplates>();
        Assert.Contains("Viewer world", page.Markup);
        Assert.Contains("World description", page.Markup);
        Assert.DoesNotContain("Custom import", page.Markup);
        Assert.DoesNotContain("Delete template", page.Markup);
        Assert.DoesNotContain("Clone template", page.Markup);
    }

    [Fact]
    public async Task ViewerCanBrowsePlayersWithoutCleanupOrModerationControls()
    {
        await using var context = Context();
        var path = MagnetarPaths.GetQuasarKnownPlayersPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { new KnownPlayerRecord { PlayerKey = "server:76561198000000000", UniqueName = "server", SteamId = 76561198000000000, DisplayName = "Test player", LastSeenUtc = DateTimeOffset.UtcNow } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var players = new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance);
        context.Services.AddSingleton(players);
        context.Render<MudPopoverProvider>();
        var page = context.Render<Players>();
        Assert.Contains("Test player", page.Markup);
        Assert.DoesNotContain("Player actions", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Trim() is "Clean Server" or "Clean All" or "Save");
    }

    [Fact]
    public async Task ViewerCanQueryLiveEntitiesWithoutReceivingDeleteControls()
    {
        await using var context = Context();
        var registry = new AgentRegistry(new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance), null!, null!, null!);
        var commands = new List<ServerCommandEnvelope>();
        registry.UpsertHello(new() { AgentId = "agent", UniqueName = "server" }, "connection", (message, _) =>
        {
            var command = message.Command!;
            commands.Add(command);
            registry.UpdateCommandResult(new() { CommandId = command.CommandId, AgentId = "agent", Success = true,
                Payload = JsonSerializer.Serialize(new EntityListResult { TotalCount = 1, TotalEntityCount = 1,
                    Entities = [new() { EntityId = 42, DisplayName = "Test grid", TypeTag = "Grid" }] }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, "connection");
            return Task.CompletedTask;
        });
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(new EntityService(registry));
        context.Services.AddSingleton(QuasarUiPluginCatalog.Create(new ConfigurationBuilder().Build(),
            new HostingEnvironment { ContentRootPath = _root }, loadPluginAssemblies: false));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Entities>();
        await page.WaitForAssertionAsync(() => Assert.Contains("Test grid", page.Markup));
        Assert.Equal(ServerCommandType.ListEntities, Assert.Single(commands).CommandType);
        Assert.DoesNotContain("Delete entity", page.Markup);
        Assert.DoesNotContain("Delete selected", page.Markup);
    }

    [Theory]
    [InlineData(QuasarRoles.Viewer, false, false)]
    [InlineData(QuasarRoles.Editor, true, false)]
    [InlineData(QuasarRoles.Admin, true, true)]
    public async Task DefaultPoliciesKeepViewingSeparateFromEditing(string role, bool canEdit, bool canAdmin)
    {
        using var services = new ServiceCollection().AddLogging().AddAuthorization(Program.ConfigureAuthorizationPolicies).BuildServiceProvider();
        var auth = services.GetRequiredService<IAuthorizationService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        foreach (var policy in new[] { QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery })
            Assert.True((await auth.AuthorizeAsync(user, null, policy)).Succeeded);
        foreach (var policy in new[] { QuasarPolicyNames.CanEditConfigs, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanControlServers, QuasarPolicyNames.CanManageDiscord, QuasarPolicyNames.CanManageAppearance, QuasarPolicyNames.ClusterManage })
            Assert.Equal(canEdit, (await auth.AuthorizeAsync(user, null, policy)).Succeeded);
        foreach (var policy in new[] { QuasarPolicyNames.CanManageSecurity, QuasarPolicyNames.CanShutdownQuasar })
            Assert.Equal(canAdmin, (await auth.AuthorizeAsync(user, null, policy)).Succeeded);
        Assert.False((await auth.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, QuasarPolicyNames.CanView)).Succeeded);
    }

    public static TheoryData<Type, string> WriteHandlers => new()
    {
        { typeof(Configs), "CreateProfileAsync" }, { typeof(Configs), "SaveTemplateInternalAsync" },
        { typeof(Configs), "CloneProfileAsync" }, { typeof(Configs), "CloneCurrentProfileForCrossplayAsync" },
        { typeof(Configs), "DeleteProfileAsync" }, { typeof(Configs), "RefreshPluginCatalogAsync" },
        { typeof(Configs), "OpenWorkshopApiKeyDialogAsync" }, { typeof(Configs), "OpenSteamAccountDialogAsync" },
        { typeof(Configs), "AddDevFolderAsync" }, { typeof(Configs), "SetDevFolderDebugAsync" }, { typeof(Configs), "RemoveDevFolder" },
        { typeof(WorldTemplates), "ImportAsync" }, { typeof(WorldTemplates), "CloneAsync" },
        { typeof(WorldTemplates), "ImportInstalledTemplateAsync" }, { typeof(WorldTemplates), "CreateConfigProfileFromTemplateAsync" },
        { typeof(WorldTemplates), "CreateConfigProfileAsync" }, { typeof(WorldTemplates), "OpenFolderPickerAsync" }, { typeof(WorldTemplates), "DeleteAsync" },
        { typeof(Players), "SaveRetentionDaysAsync" }, { typeof(Players), "CleanSelectedServerAsync" },
        { typeof(Players), "CleanAllAsync" }, { typeof(Players), "SendPlayerCommandAsync" },
        { typeof(Chat), "RestartSelectedServerAsync" }, { typeof(PluginConfigEditor), "ApplyAsync" },
        { typeof(Entities), "DeleteSelectedEntitiesAsync" }, { typeof(Entities), "DeleteEntityAsync" },
        { typeof(DiscordPage), "SaveAsync" }, { typeof(DiscordPage), "ResetDeathTemplatesAsync" },
        { typeof(DiscordPage), "SaveDeathTemplatesAsync" }, { typeof(DiscordPage), "OpenDiscordConsoleDialogAsync" },
    };

    [Theory]
    [MemberData(nameof(WriteHandlers))]
    public async Task ViewerWriteHandlersDenyBeforeAccessingDialogsOrMutableServices(Type componentType, string methodName)
    {
        using var services = new ServiceCollection().AddLogging().AddAuthorization(Program.ConfigureAuthorizationPolicies).BuildServiceProvider();
        var permissions = new QuasarPermissionService(new ViewerState(), services.GetRequiredService<IAuthorizationService>(), new QuasarRoleMapper(new(), null!));
        var instance = Activator.CreateInstance(componentType)!;
        if (instance is PluginConfigEditor)
            componentType.GetProperty(nameof(PluginConfigEditor.Plugin))!.SetValue(instance, new PluginConfigData { PluginId = "example", DisplayName = "Example plugin" });
        componentType.GetProperty("Permissions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, permissions);
        var method = componentType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Every other collaborator is intentionally absent: a denied event must not reach any of them.
        var args = method.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        await (Task)method.Invoke(instance, args)!;
    }

    private sealed class ViewerState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, QuasarRoles.Viewer)], "test"))));
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
