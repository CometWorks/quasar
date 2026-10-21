using System.Reflection;
using Bunit;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

// #1332: a config profile created for a single server selects the default plugins (Bugfixes and
// Performance, QuasarDefaultPlugins) with their default settings; existing profiles are left alone.
[Collection("Exact server creation")]
public sealed class SingleServerDefaultPluginsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-default-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;

    public SingleServerDefaultPluginsTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ProfileCreatedFromTheServerEditorSelectsBothDefaults()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<ConfigProfileQuickCreateDialog>("Create Config Profile"));
        var dialog = provider.FindComponent<ConfigProfileQuickCreateDialog>();
        var name = dialog.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Profile name");
        await dialog.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Survival"));
        await dialog.FindComponents<MudButton>().Single(c => c.Markup.Contains("Create Profile")).Find("button").ClickAsync();

        AssertDefaults(Assert.Single(context.Services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfiles()));
    }

    [Fact]
    public async Task ProfileCreatedFromAWorldTemplateSelectsBothDefaults()
    {
        string world = Path.Combine(_root, "world");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, WorldSandboxConfigEditor.SandboxConfigFileName),
            "<MyObjectBuilder_WorldConfiguration><Settings><MaxPlayers>8</MaxPlayers></Settings></MyObjectBuilder_WorldConfiguration>");
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<WorldTemplateConfigProfileDialog>("Create Matching Config Profile",
            new DialogParameters
            {
                [nameof(WorldTemplateConfigProfileDialog.WorldName)] = "World",
                [nameof(WorldTemplateConfigProfileDialog.WorldPath)] = world,
                [nameof(WorldTemplateConfigProfileDialog.WorldTemplateId)] = "world",
                [nameof(WorldTemplateConfigProfileDialog.AllowSkip)] = false,
            }));
        var dialog = provider.FindComponent<WorldTemplateConfigProfileDialog>();
        await dialog.FindComponents<MudButton>().Single(c => c.Markup.Contains("Create Profile")).Find("button").ClickAsync();

        var result = await reference!.Result;
        var profile = Assert.IsType<WorldTemplateConfigProfileDialog.WorldTemplateConfigProfileDialogResult>(result!.Data).Profile!;
        AssertDefaults(profile);
        Assert.Equal(8, profile.SessionSettings.MaxPlayers);
    }

    [Fact]
    public void ImportedServerProfileSelectsBothDefaults()
    {
        var world = new ExistingServerWorldCandidate { Name = "World", Path = _root, ImportedProfile = new QuasarConfigProfile() };
        var analysis = new ExistingServerImportAnalysis
        {
            Kind = ExistingServerKind.Vanilla,
            SourcePath = _root,
            AppDataPath = _root,
            ConfigPath = Path.Combine(_root, "SpaceEngineers-Dedicated.cfg"),
            ServerName = "Server",
            WorldName = "World",
            ServerIp = "0.0.0.0",
            Worlds = [world],
            Warnings = [],
            ImportedProfile = new QuasarConfigProfile(),
        };

        AssertDefaults(ExistingServerImportService.BuildProfile(analysis, world,
            new QuasarWorldTemplate { WorldTemplateId = "world", Name = "World" }, "Imported", ExistingServerImportSections.ServerSettings));
    }

    [Fact]
    public async Task RemovedDefaultStaysRemoved()
    {
        var created = new QuasarConfigProfile { Name = "Survival" };
        QuasarDefaultPlugins.Apply(created, enable: true);
        using (var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance))
        {
            await profiles.UpsertAsync(created);
            var edited = profiles.GetProfile(created.ConfigProfileId)!;
            edited.Plugins.RemoveAll(p => p.PluginId == QuasarDefaultPlugins.PerformancePluginId);
            await profiles.UpsertAsync(edited);
        }

        using var reloaded = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var profile = reloaded.GetProfile(created.ConfigProfileId)!;
        Assert.Equal(QuasarDefaultPlugins.BugfixesPluginId, Assert.Single(profile.Plugins).PluginId);
        Assert.Empty(QuasarDefaultPlugins.Pending(profile));
        Assert.Empty(QuasarDefaultPlugins.Apply(profile, enable: true));
        Assert.Single(profile.Plugins);
    }

    [Fact]
    public async Task ExistingProfileIsNotChanged()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ConfigProfiles", "legacy"));
        File.WriteAllText(Path.Combine(_root, "ConfigProfiles", "legacy", "profile.json"),
            """{ "configProfileId": "legacy", "name": "Legacy", "plugins": [{ "pluginId": "example", "displayName": "Example" }] }""");
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var profile = profiles.GetProfile("legacy");
        Assert.NotNull(profile);
        Assert.Equal("example", Assert.Single(profile.Plugins).PluginId);

        // Saving it again, as the server editor does when a new server uses this profile, adds nothing.
        await profiles.UpsertAsync(profile);
        var saved = profiles.GetProfile("legacy")!;
        Assert.Equal("example", Assert.Single(saved.Plugins).PluginId);
        Assert.Null(saved.OfferedDefaultPlugins);
    }

    private static void AssertDefaults(QuasarConfigProfile profile)
    {
        Assert.Equal(
            [QuasarDefaultPlugins.PerformancePluginId, QuasarDefaultPlugins.BugfixesPluginId],
            profile.Plugins.Select(p => p.PluginId).Order(StringComparer.Ordinal));
        // Default settings and the latest hub commit: nothing pinned, no plugin configuration written.
        Assert.All(profile.Plugins, p => Assert.Equal(string.Empty, p.SelectedVersion));
        Assert.Equal(QuasarDefaultPlugins.PluginIds.Order(StringComparer.OrdinalIgnoreCase),
            profile.OfferedDefaultPlugins!.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Render<MudPopoverProvider>();
        return context;
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
