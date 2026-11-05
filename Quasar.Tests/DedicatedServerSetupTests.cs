using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Components.Shared;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class DedicatedServerSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ds-setup-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;

    public DedicatedServerSetupTests() { _previous = _cache.GetValue(null); _cache.SetValue(null, _root); }

    [Theory]
    [InlineData("downloading", 15, ManagedRuntimeInstallPhase.Downloading)]
    [InlineData("verifying install", 67, ManagedRuntimeInstallPhase.Installing)]
    [InlineData("verifying update", 100, ManagedRuntimeInstallPhase.Installing)]
    [InlineData("preallocating", 0, ManagedRuntimeInstallPhase.Installing)]
    [InlineData("staging", 8, ManagedRuntimeInstallPhase.Installing)]
    [InlineData("committing", 99, ManagedRuntimeInstallPhase.Installing)]
    public void SteamCmdReportsActualProgressWithoutRawOutput(string activity, int percent, ManagedRuntimeInstallPhase phase)
    {
        var progress = ManagedDedicatedServerRuntimeResolver.ParseDedicatedServerInstallProgress(
            $" Update state (0x61) {activity}, progress: {percent}.4 (100 / 1000) secret-output", "/server");
        // SteamCMD validation may report exactly 100%.
        if (percent == 100)
            progress = ManagedDedicatedServerRuntimeResolver.ParseDedicatedServerInstallProgress(
                $"Update state (0x61) {activity}, progress: 100.0 (1000 / 1000)", "/server");
        Assert.NotNull(progress);
        Assert.Equal(percent, progress.Percent);
        Assert.Equal(phase, progress.Phase);
        Assert.Equal("/server", progress.Path);
        Assert.DoesNotContain("secret-output", progress.Message);
    }

    [Theory]
    [InlineData("Logging in with password secret")]
    [InlineData("Update state (0x61) secret, progress: 50")]
    [InlineData("Update state (0x61) downloading, progress: -1")]
    [InlineData("Update state (0x61) downloading, progress: 101")]
    [InlineData("Update state (0x61) downloading, progress: NaN")]
    [InlineData("Update state (0x61) downloading, progress: 1e10")]
    public void UnrecognizedSteamCmdOutputIsNotShown(string line) =>
        Assert.Null(ManagedDedicatedServerRuntimeResolver.ParseDedicatedServerInstallProgress(line, "/server"));

    [Fact]
    public async Task CopyReportsFileCountsSkipsBackupsAndPublishesOnlyAfterCompletion()
    {
        using var catalog = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        string source = World("source");
        Directory.CreateDirectory(Path.Combine(source, "Backup"));
        File.WriteAllText(Path.Combine(source, "Backup", "old.sbc"), "old");
        var updates = new List<WorldTemplateImportProgress>();
        var progress = new InlineProgress<WorldTemplateImportProgress>(update =>
        {
            updates.Add(update);
            Assert.Empty(catalog.GetTemplates());
        });
        var template = await catalog.ImportAsync("Earth", "", source, progress: progress);
        Assert.Equal([0, 1, 2], updates.Select(u => u.CompletedFiles));
        Assert.All(updates, u => Assert.Equal(2, u.TotalFiles));
        Assert.True(File.Exists(Path.Combine(catalog.GetWorldDirectory(template.WorldTemplateId), "Sandbox.sbc")));
        Assert.False(Directory.Exists(Path.Combine(catalog.GetWorldDirectory(template.WorldTemplateId), "Backup")));
        Assert.Single(catalog.GetTemplates());
        Assert.True(File.Exists(Path.Combine(source, "Backup", "old.sbc")));
    }

    [Fact]
    public async Task CancelledCopyRemovesPartialTemplateAndKeepsOriginal()
    {
        using var catalog = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        string source = World("source");
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ImportAsync("Earth", "", source, stop.Token,
            new InlineProgress<WorldTemplateImportProgress>(p => { if (p.CompletedFiles == 1) stop.Cancel(); })));
        Assert.Empty(catalog.GetTemplates());
        Assert.Empty(Directory.GetDirectories(MagnetarPaths.GetQuasarWorldTemplatesDirectory()));
        Assert.True(File.Exists(Path.Combine(source, "Sandbox.sbc")));
    }

    [Fact]
    public async Task ConcurrentImportsReserveDifferentIds()
    {
        using var catalog = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        string source = World("source");
        var templates = await Task.WhenAll(catalog.ImportAsync("Earth", "", source), catalog.ImportAsync("Earth", "", source));
        Assert.NotEqual(templates[0].WorldTemplateId, templates[1].WorldTemplateId);
        Assert.Equal(2, catalog.GetTemplates().Count);
    }

    [Fact]
    public void DiscoveryUsesTheRuntimeSelectedInstallationAndListsSearchedFolders()
    {
        string world = World(Path.Combine("actual", "Content", "CustomWorlds", "Earth"));
        string ds64 = Path.Combine(_root, "actual", "DedicatedServer64");
        Directory.CreateDirectory(ds64);
        foreach (string file in new[] { "SpaceEngineersDedicated.exe", "SpaceEngineers.Game.dll", "VRage.dll", "Sandbox.Game.dll" })
            File.WriteAllText(Path.Combine(ds64, file), "");
        var options = new ManagedRuntimeOptions { DedicatedServer64OverridePath = ds64 };
        var resolver = Resolver(options);
        // The location service cannot find this root from its own configured paths.
        var locations = new WorldTemplateImportLocationService(new ManagedRuntimeOptions
            { DedicatedServerInstallDirectory = Path.Combine(_root, "missing") }, null!, resolver);
        var result = locations.DiscoverInstalledWorldTemplates();
        Assert.Contains(result.Templates, t => t.SourcePath == world);
        Assert.Contains(Path.Combine(_root, "actual", "Content", "CustomWorlds"), result.Locations);
        Assert.Empty(result.Warnings);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => locations.DiscoverInstalledWorldTemplates(stop.Token));
    }

    [Fact]
    public async Task WorldChooserExplainsPreparationThenRescansWhenServerFilesAreReady()
    {
        await using var context = Context();
        var provider = context.Render<MudDialogProvider>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () => dialog = await dialogs.ShowAsync<WorldTemplateQuickImportDialog>("Choose world",
            new DialogParameters { [nameof(WorldTemplateQuickImportDialog.TemplateOnly)] = true }));
        await provider.WaitForAssertionAsync(() => Assert.Contains("Waiting for the server's starting worlds", provider.Markup));
        Assert.Contains("Where Quasar looked", provider.Markup);
        string source = World(Path.Combine("install", "Content", "CustomWorlds", "Earth"));
        var warmup = context.Services.GetRequiredService<ManagedRuntimeWarmupService>();
        await provider.InvokeAsync(() => Report(warmup, ManagedRuntimeInstallPhase.Ready));
        await provider.WaitForAssertionAsync(() => Assert.Contains("Use this world", provider.Markup));
        Assert.DoesNotContain("Waiting for the server's starting worlds", provider.Markup);
        // A failed copy stays in the chooser and explains how to retry.
        File.Delete(Path.Combine(source, "Sandbox.sbc"));
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "Use this world").ClickAsync());
        await provider.WaitForAssertionAsync(() => Assert.Contains("Could not finish preparing this world", provider.Markup));
        File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "Use this world").ClickAsync());
        var result = await dialog.Result;
        var imported = Assert.IsType<WorldTemplateQuickImportDialog.WorldTemplateQuickImportResult>(result!.Data);
        Assert.Equal("Earth", imported.Template.Name);
        Assert.True(File.Exists(Path.Combine(source, "Sandbox.sbc")));
    }

    [Fact]
    public async Task SetupStatusExplainsFailuresAndAllowsWorldChoiceBeforeMagnetarFinishes()
    {
        await using var context = Context();
        var status = context.Render<DedicatedServerSetupStatus>();
        var warmup = context.Services.GetRequiredService<ManagedRuntimeWarmupService>();
        await status.InvokeAsync(() => Report(warmup, ManagedRuntimeInstallPhase.Failed));
        Assert.Contains("Preparation needs attention", status.Markup);
        Assert.Contains("Retry preparation", status.Markup);
        await status.InvokeAsync(() => Report(warmup, ManagedRuntimeInstallPhase.Ready));
        Assert.Contains("Choose a starting world now", status.Markup);
        Assert.DoesNotContain("Retry preparation", status.Markup);
        Assert.False(warmup.IsReady);
    }

    private BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanEditConfigs, QuasarPolicyNames.CanEditServers);
        context.Services.AddSingleton(new QuasarRoleMapper(new(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        var options = new ManagedRuntimeOptions { DedicatedServerInstallDirectory = Path.Combine(_root, "install") };
        context.Services.AddSingleton(new WorldTemplateImportLocationService(options, null!));
        context.Services.AddSingleton(new ManagedRuntimeWarmupService(Resolver(options), NullLogger<ManagedRuntimeWarmupService>.Instance));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        return context;
    }

    private static ManagedDedicatedServerRuntimeResolver Resolver(ManagedRuntimeOptions options) =>
        new(NullLogger<ManagedDedicatedServerRuntimeResolver>.Instance, null!, options, null!, null!);

    private static void Report(ManagedRuntimeWarmupService warmup, ManagedRuntimeInstallPhase phase) =>
        typeof(ManagedRuntimeWarmupService).GetMethod("ApplyProgress", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(warmup, [new ManagedRuntimeInstallProgress(ManagedRuntimeInstallComponent.DedicatedServer, phase, "Test status")]);

    private string World(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint><SessionName>Earth</SessionName></MyObjectBuilder_Checkpoint>");
        File.WriteAllText(Path.Combine(path, "Sandbox_config.sbc"), "<MyObjectBuilder_WorldConfiguration />");
        return path;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
