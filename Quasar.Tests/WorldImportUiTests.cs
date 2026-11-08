using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class WorldImportUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-world-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    public WorldImportUiTests() { _previous = _cache.GetValue(null); _cache.SetValue(null, _root); }

    [Fact]
    public async Task FolderDialogRequiresProfileChoiceThenCommitsAndCleansStaging()
    {
        await using var context = await ContextAsync();
        var servers = context.Services.GetRequiredService<DedicatedServerCatalog>();
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        await dialogs.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<ServerWorldImportDialog>("Import World",
            new DialogParameters { [nameof(ServerWorldImportDialog.Target)] = servers.GetServer("target")! }));
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await dialogs.Find(".world-path-controls input").InputAsync(new() { Value = source });
        await dialogs.Find("input[type=checkbox]").ChangeAsync(new() { Value = true });
        await dialogs.FindAll("button").Single(button => button.TextContent == "Read World").ClickAsync(new());
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Ready to import", dialogs.Markup));
        Assert.True(dialogs.FindAll("button").Single(button => button.TextContent == "Import and Select").HasAttribute("disabled"));
        Assert.Contains("World settings could not be read", dialogs.Markup);
        await dialogs.Find("#world-save-name").InputAsync(new() { Value = "New" });
        await dialogs.Find("input[type=radio]").ChangeAsync(new() { Value = true });
        await dialogs.FindAll("button").Single(button => button.TextContent == "Import and Select").ClickAsync(new());
        Assert.Equal("New", servers.GetServer("target")!.WorldSaveName);
        await dialogs.WaitForAssertionAsync(() => Assert.Empty(Directory.GetDirectories(DedicatedServerPathResolver.Resolve(servers.GetServer("target")!).SavesPath, ".world-import-*")));
    }

    private async Task<BunitContext> ContextAsync()
    {
        var context = new QuasarBunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized("editor"); auth.SetPolicies(QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        var maintenance = new ServerRestoreCoordinator();
        var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance, maintenance);
        var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Current profile" });
        await servers.UpsertAsync(new() { UniqueName = "target", DisplayName = "Target server", WorldSaveName = "Old", ConfigProfileId = "profile" });
        var supervisor = new DedicatedServerSupervisor(servers, new AgentRegistry(null!, null!, null!, null!), null!, null!, null!, new(), maintenance, NullLogger<DedicatedServerSupervisor>.Instance);
        context.Services.AddSingleton(servers); context.Services.AddSingleton(profiles); context.Services.AddSingleton(supervisor);
        context.Services.AddSingleton(maintenance); context.Services.AddSingleton<ServerWorldImportService>();
        context.Services.AddSingleton(new WorldTemplateImportLocationService(new(), new BrowserStorageService(context.JSInterop.JSRuntime)));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        var options = new ManagedRuntimeOptions();
        var resolver = new ManagedDedicatedServerRuntimeResolver(NullLogger<ManagedDedicatedServerRuntimeResolver>.Instance, null!, options, null!, null!);
        context.Services.AddSingleton(new ManagedRuntimeWarmupService(resolver, NullLogger<ManagedRuntimeWarmupService>.Instance));
        return context;
    }

    [Fact]
    public async Task TemplateWizardRetainsStagedInputAcrossConfigurationStep()
    {
        await using var context = await ContextAsync();
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        await dialogs.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<WorldTemplateQuickImportDialog>("Add Template"));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<MudTabs>().Instance.ActivatePanelAsync(1));
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await dialogs.Find(".world-path-controls input").InputAsync(new() { Value = source });
        await dialogs.Find("input[type=checkbox]").ChangeAsync(new() { Value = true });
        await dialogs.FindAll("button").Single(button => button.TextContent == "Read World").ClickAsync(new());
        await dialogs.Find("#world-template-name").InputAsync(new() { Value = "Reusable world" });
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Continue").ClickAsync(new());
        Assert.Contains("Template config profile", dialogs.Markup);
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Import World Template").ClickAsync(new());
        var templates = context.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
        Assert.Equal("Reusable world", Assert.Single(templates.GetTemplates()).Name);
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
