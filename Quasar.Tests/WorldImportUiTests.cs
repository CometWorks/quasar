using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Client;
using Quasar.Client.Components;
using Quasar.Client.Fleet;
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
        var inventory = await context.Services.GetRequiredService<DashboardSdk>().RequestAsync<WorldInventory>("worlds.inventory");
        var form = context.Render<WorldImportForm>(p => p.Add(c => c.Inventory, inventory).Add(c => c.Server, inventory.Servers.Single()));
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await form.Find("input[type=text]").ChangeAsync(new() { Value = source });
        await form.Find("input[type=checkbox]").ChangeAsync(new() { Value = true });
        await form.FindAll("button").Single(button => button.TextContent.Trim() == "Read World").ClickAsync();
        var commit = () => form.FindAll("button").Single(button => button.TextContent.Trim() == "Import and select save");
        Assert.True(commit().HasAttribute("disabled"));
        await form.FindComponent<MudTextField<string>>().InvokeAsync(() => form.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("New"));
        await form.InvokeAsync(() => form.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync("keep"));
        await commit().ClickAsync();
        Assert.Equal("New", servers.GetServer("target")!.WorldSaveName);
        await form.WaitForAssertionAsync(() => Assert.Empty(Directory.GetDirectories(DedicatedServerPathResolver.Resolve(servers.GetServer("target")!).SavesPath, ".world-import-*")));
    }

    private async Task<BunitContext> ContextAsync()
    {
        var context = new OperationsBrowserContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized("editor");
        auth.SetClaims(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "editor")); auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        var maintenance = new ServerRestoreCoordinator();
        var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance, maintenance);
        var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Current profile" });
        await servers.UpsertAsync(new() { UniqueName = "target", DisplayName = "Target server", WorldSaveName = "Old", ConfigProfileId = "profile" });
        var supervisor = new DedicatedServerSupervisor(servers, new AgentRegistry(null!, null!, null!, null!), null!, null!, null!, new(), maintenance, NullLogger<DedicatedServerSupervisor>.Instance);
        context.Services.AddSingleton(servers); context.Services.AddSingleton(profiles); context.Services.AddSingleton(supervisor);
        context.Services.AddSingleton(maintenance); context.Services.AddSingleton<ServerWorldImportService>();
        context.Services.AddSingleton(new WorldTemplateImportLocationService(new()));
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
        var inventory = await context.Services.GetRequiredService<DashboardSdk>().RequestAsync<WorldInventory>("worlds.inventory");
        var form = context.Render<WorldImportForm>(p => p.Add(c => c.Inventory, inventory));
        await form.FindAll("button").Single(button => button.TextContent.Trim() == "Server folder").ClickAsync();
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await form.Find("input[type=text]").ChangeAsync(new() { Value = source });
        await form.Find("input[type=checkbox]").ChangeAsync(new() { Value = true });
        await form.FindAll("button").Single(button => button.TextContent.Trim() == "Read World").ClickAsync();
        await form.InvokeAsync(() => form.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Template name").Instance.ValueChanged.InvokeAsync("Reusable world"));
        Assert.Contains("Configuration", form.Markup);
        await form.FindAll("button").Single(button => button.TextContent.Trim() == "Create world template").ClickAsync();
        var templates = context.Services.GetRequiredService<QuasarWorldTemplateCatalog>();
        Assert.Equal("Reusable world", Assert.Single(templates.GetTemplates()).Name);
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
