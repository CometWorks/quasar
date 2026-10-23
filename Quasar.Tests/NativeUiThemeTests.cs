using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Services.Auth;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Components.Pages;
using Quasar.Components.Shared;
using Quasar.Components.Dashboard;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

public sealed class NativeUiThemeTests
{
    [Theory]
    [InlineData("light", true, false)]
    [InlineData("dark", false, true)]
    [InlineData("system", false, false)]
    [InlineData("\"dark\"", false, true)] // Existing JSON-encoded browser values.
    public async Task StoredModeSurvivesInitializationAndSystemChanges(string stored, bool systemDark, bool expected)
    {
        await using var context = new BunitContext();
        context.JSInterop.Setup<string?>("quasarConfigs.getLocalStorage", "quasar.theme.mode").SetResult(stored);
        context.JSInterop.Setup<bool>("quasarConfigs.getSystemDarkMode").SetResult(systemDark);
        var service = new ThemePreferenceService(new(context.JSInterop.JSRuntime), null!, context.JSInterop.JSRuntime);
        await service.InitializeAsync();
        Assert.Equal(expected, service.IsDarkMode);
        var notifications = new List<bool>();
        service.ThemeModeChanged += notifications.Add;
        service.SyncSystemDarkMode(!systemDark);
        Assert.Equal(service.Mode == ThemeMode.System ? !systemDark : expected, service.IsDarkMode);
        Assert.Equal(service.Mode == ThemeMode.System ? 1 : 0, notifications.Count);
    }

    [Fact]
    public async Task ManualModeAndReturnToSystemPersistAndNotifyAnalytics()
    {
        await using var context = new BunitContext();
        context.JSInterop.Setup<bool>("quasarConfigs.getSystemDarkMode").SetResult(true);
        context.JSInterop.SetupVoid("quasarConfigs.setLocalStorage", _ => true).SetVoidResult();
        var service = new ThemePreferenceService(new(context.JSInterop.JSRuntime), null!, context.JSInterop.JSRuntime);
        var notifications = new List<bool>();
        service.ThemeModeChanged += notifications.Add;
        await service.SetModeAsync(ThemeMode.Light);
        service.SyncSystemDarkMode(true);
        await service.SetModeAsync(ThemeMode.System);
        Assert.Equal(new[] { false, true }, notifications);
        Assert.Equal(new[] { "light", "system" }, context.JSInterop.Invocations["quasarConfigs.setLocalStorage"].Select(call => call.Arguments[1]));
    }

    [Fact]
    public void ExistingBrandingRoundTripsEveryColorIntoNativeAndLegacyThemes()
    {
        var settings = BrandingSettings.Normalize(null);
        settings.PresetId = null;
        settings.LogoDarkPath = "/branding/existing.svg";
        settings.DarkPalette.HoverBackground = "rgba(30, 40, 50, 0.2)";
        var reloaded = BrandingSettings.Normalize(JsonSerializer.Deserialize<BrandingSettings>(JsonSerializer.Serialize(settings)));
        Assert.Null(reloaded.PresetId);
        Assert.Equal(settings.LogoDarkPath, reloaded.LogoDarkPath);
        Assert.Equal(settings.DarkPalette.HoverBackground, reloaded.DarkPalette.HoverBackground);
        var css = QuasarTheme.BuildCss(reloaded.DarkPalette, true);
        foreach (var property in typeof(ThemePalette).GetProperties())
        {
            var token = Regex.Replace(property.Name, "([a-z])([A-Z])", "$1-$2").ToLowerInvariant();
            var color = QuasarTheme.NormalizeColor((string)property.GetValue(reloaded.DarkPalette)!, "#000000");
            Assert.Contains($"--quasar-palette-{token}:{color};", css);
            if (property.Name.StartsWith("Hover")) continue;
            var mudProperty = typeof(MudBlazor.PaletteDark).GetProperty(property.Name)!;
            Assert.Equal(color, ((MudBlazor.Utilities.MudColor)mudProperty.GetValue(reloaded.DarkPalette.ToMudPaletteDark())!).ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA));
        }
        Assert.Equal(29, typeof(ThemePalette).GetProperties().Length);
    }

    [Fact]
    public void CssRejectsDeclarationInjectionAndUsesApprovedDefaults()
    {
        var palette = ThemePalette.QuasarDark();
        palette.Primary = "red;position:fixed;inset:0";
        var css = QuasarTheme.BuildCss(palette, true);
        Assert.DoesNotContain("position", css);
        Assert.Contains("--quasar-palette-primary:#00c0fdff;", css);
        Assert.Equal("#00769d", BrandingPresets.GetLightPalette(BrandingPresets.QuasarId).Primary);
        Assert.Equal("#18191b", BrandingPresets.GetDarkPalette(BrandingPresets.CometWorksId).Background);
    }

    [Fact]
    public void OldDefaultColorsUpgradeWithoutChangingIdentityOrCustomPalettes()
    {
        var old = new BrandingSettings { LightPalette = ThemePalette.LegacyQuasarLight(), DarkPalette = ThemePalette.LegacyQuasarDark(),
            AppName = "My fleet", AppSubtitle = "Private operations", LogoDarkPath = "/branding/my-logo.png", FaviconPath = "/branding/my-icon.ico" };
        var saved = JsonSerializer.Deserialize<BrandingSettings>(JsonSerializer.Serialize(old));
        var upgraded = BrandingSettings.Normalize(saved);
        Assert.Equal("#00c0fd", upgraded.DarkPalette.Primary);
        Assert.Equal("#18191b", upgraded.DarkPalette.Background);
        Assert.Equal("#00769d", upgraded.LightPalette.Primary);
        Assert.Equal(old.AppName, upgraded.AppName);
        Assert.Equal(old.AppSubtitle, upgraded.AppSubtitle);
        Assert.Equal(old.LogoDarkPath, upgraded.LogoDarkPath);
        Assert.Equal(old.FaviconPath, upgraded.FaviconPath);
        Assert.Equal(JsonSerializer.Serialize(upgraded), JsonSerializer.Serialize(BrandingSettings.Normalize(upgraded)));

        // Any one edited field in either mode protects the whole custom pair,
        // even if a hand-edited branding file still names the default preset.
        foreach (var dark in new[] { false, true })
        foreach (var field in typeof(ThemePalette).GetProperties())
        {
            var custom = old.Clone();
            field.SetValue(dark ? custom.DarkPalette : custom.LightPalette, "#12345678");
            Assert.Equal(JsonSerializer.Serialize(custom), JsonSerializer.Serialize(BrandingSettings.Normalize(custom)));
        }
        old.PresetId = null;
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(BrandingSettings.Normalize(old)));
    }

    [Fact]
    public async Task ColorPickerRetainsAlphaAndInvalidDraftDoesNotChangeSavedValue()
    {
        await using var context = new BunitContext();
        var values = new List<string>();
        var validity = new List<bool>();
        var field = context.Render<QuasarColorField>(p => p.Add(x => x.Label, "Hover background").Add(x => x.Value, "#11223333")
            .Add(x => x.ValueChanged, (string value) => values.Add(value)).Add(x => x.ValidityChanged, (bool valid) => validity.Add(valid)));
        field.Find("input[type=color]").Input("#445566");
        Assert.Equal("#44556633", Assert.Single(values));
        field.Find("input[type=text]").Change("invalid");
        Assert.Single(values);
        Assert.Equal("true", field.Find("input[type=text]").GetAttribute("aria-invalid"));
        Assert.Equal(new[] { true, false }, validity);
        Assert.Contains("valid color", field.Find("[role=alert]").TextContent);
    }
}

[CollectionDefinition("Native appearance", DisableParallelization = true)]
public sealed class NativeAppearanceCollection;

[Collection("Native appearance")]
public sealed class NativeAppearanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-native-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;
    private readonly BrandingService _branding;

    public NativeAppearanceTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        _branding = new(NullLogger<BrandingService>.Instance);
    }

    [Fact]
    public async Task AppearanceEditsBothPalettesAndSavingUpdatesBranding()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(_branding);
        context.Services.AddSingleton(new ThemePreferenceService(new(context.JSInterop.JSRuntime), _branding, context.JSInterop.JSRuntime));
        var page = context.Render<Appearance>();
        Assert.Equal(58, page.FindAll("input[type=color]").Count);
        Assert.Equal("/Quasar.png", page.Find("img[alt='Dark-mode logo preview']").GetAttribute("src"));
        Assert.Equal("/Quasar-inverted-transparent.png", page.Find("img[alt='Light-mode logo preview']").GetAttribute("src"));
        var darkPrimary = page.FindAll("input[type=text]")[31]; // Two identity inputs followed by 29 light fields.
        darkPrimary.Change("invalid");
        Assert.True(page.Find("button[type=submit]").HasAttribute("disabled"));
        page.Find("button[aria-pressed='false']").Click();
        Assert.True(page.Find("button[type=submit]").HasAttribute("disabled"));
        page.Find("select").Change(BrandingPresets.CometWorksId);
        Assert.False(page.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.Empty(page.FindAll("input[aria-invalid=true]"));
        page.FindAll("input[type=text]")[0].Input("Fleet Ops");
        Assert.Equal("Quasar", _branding.Settings.AppName); // Draft is local until submit.
        page.Find("form").Submit();
        await page.WaitForAssertionAsync(() => Assert.Equal("Fleet Ops", _branding.Settings.AppName));
        Assert.Equal(BrandingPresets.CometWorksId, _branding.Settings.PresetId);
        Assert.Contains("Appearance saved", page.Markup);
        // Optional offline inspection of the actual rendered component; no Quasar process.
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, $"<div data-theme=\"dark\" style=\"{QuasarTheme.BuildCss(_branding.Settings.DarkPalette, true)}\">{page.Markup}</div>");
        page.FindAll("button").Single(x => x.TextContent.Contains("Reset to Quasar default")).Click();
        await page.WaitForAssertionAsync(() => Assert.Equal(BrandingPresets.QuasarId, _branding.Settings.PresetId));
        Assert.Equal("Quasar", _branding.Settings.AppName);
    }

    [Fact]
    public async Task ServerTableSortsMixedRowsKeepsControlsAndExpandsDetails()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView);
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        await catalog.UpsertAsync(new() { UniqueName = "zulu", DisplayName = "Zulu", WorldSaveName = "World", ServerPort = 9999 });
        await catalog.UpsertAsync(new() { UniqueName = "alpha", DisplayName = "Alpha", WorldSaveName = "World", ServerPort = 27016 });
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        await clusters.CreateAsync(new("cluster", "Beta Cluster", "http://gateway.invalid:27017", "QUASAR_TEST_MISSING_GATEWAY_TOKEN"), default);
        var registry = new AgentRegistry(null!, null!, null!, null!);
        // Nodes use slot/process names, even before their incarnation receipt is ready.
        foreach (var epoch in new long[] { 0, 42 })
            registry.UpsertHello(new() { AgentId = "node-" + epoch, UniqueName = "slot-" + epoch,
                ClusterMode = true, ClusterId = "cluster", ClusterSlot = "slot-" + epoch,
                ClusterNodeId = "node-" + epoch, ClusterEpoch = epoch }, "connection-" + epoch, (_, _) => Task.CompletedTask);
        registry.UpsertHello(new() { AgentId = "orphan", UniqueName = "alpha", ServerName = "Orphan node",
            ClusterMode = true, ClusterId = "missing-cluster" }, "orphan", (_, _) => Task.CompletedTask);
        registry.UpsertHello(new() { AgentId = "unmanaged", UniqueName = "cluster", ServerName = "Unmanaged standalone" },
            "unmanaged", (_, _) => Task.CompletedTask);
        using var supervisor = new DedicatedServerSupervisor(catalog, registry, null!, null!, null!, new(), null!, NullLogger<DedicatedServerSupervisor>.Instance);
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var templates = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        using var http = new HttpClient(new UnavailableGateway());
        var gateway = new ClusterGatewayClient(http);
        context.Services.AddSingleton(catalog);
        context.Services.AddSingleton(clusters);
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(supervisor);
        context.Services.AddSingleton(profiles);
        context.Services.AddSingleton(templates);
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(new ClusterCommandService(clusters, null!, gateway, null!));
        context.Services.AddSingleton(new ClusterReconciler(clusters, gateway, null!, null!, NullLogger<ClusterReconciler>.Instance));
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(new WebServiceOptions());
        context.Services.AddScoped<ServerManagementActions>();
        context.ComponentFactories.AddStub<ServerDetailPanel>();
        context.ComponentFactories.AddStub<ClusterDetailPanel>();
        var page = context.Render<Servers>(p => p.Add(x => x.LaunchBlocked, true).Add(x => x.LaunchBlockedMessage, "Runtime preparing"));
        Assert.Equal(2, page.FindAll(".quasar-agent-details").Count);
        Assert.Contains("Orphan node", page.FindAll(".quasar-agent-details")[0].TextContent);
        Assert.Contains("Register the owning cluster", page.Markup);
        Assert.DoesNotContain("slot-42", string.Join(" ", page.FindAll(".quasar-agent-details").Select(x => x.TextContent)));
        Assert.Equal(new[] { "alpha", "cluster", "zulu" }, page.FindAll(".quasar-server-name-cell").Select(x => x.GetAttribute("title")));
        Assert.Empty(page.FindAll("a[title='Convert to cluster']")); // Existing ClusterManage policy still applies.
        page.FindAll("thead button").Single(x => x.TextContent.Trim().StartsWith("Endpoint")).Click();
        Assert.Equal(new[] { "zulu", "alpha", "cluster" }, page.FindAll(".quasar-server-name-cell").Select(x => x.GetAttribute("title")));
        Assert.Equal("ascending", page.FindAll("th")[3].GetAttribute("aria-sort"));
        page.FindAll("thead button").Single(x => x.TextContent.Trim().StartsWith("Endpoint")).Click();
        Assert.Equal("descending", page.FindAll("th")[3].GetAttribute("aria-sort"));
        Assert.True(page.Find("button[title='Runtime preparing']").HasAttribute("disabled"));
        Assert.True(page.Find("button[title^='Wait for a stable']").HasAttribute("disabled"));
        foreach (var label in new[] { "Show console output", "Clone server", "Create world template", "Edit server", "Delete server", "Open cluster details" })
            Assert.NotEmpty(page.FindAll($"button[title='{label}']"));
        var menuTarget = page.Find("button[title='Clone server']").GetAttribute("popovertarget");
        Assert.NotNull(menuTarget);
        Assert.NotNull(page.Find($"[id='{menuTarget}'][popover=auto]"));
        page.Find("input[type=search]").Input("ALPHA");
        Assert.Equal("alpha", Assert.Single(page.FindAll(".quasar-server-name-cell")).GetAttribute("title"));
        page.Find("input[type=search]").Input("no matching server");
        Assert.Contains("No servers match", page.Markup);
        page.FindAll("button").Single(x => x.TextContent == "Clear filters").Click();
        Assert.Equal(3, page.FindAll(".quasar-server-name-cell").Count);
        page.Find("input[type=checkbox]").Change(true);
        Assert.Contains("Agent / WA", page.Find("thead").TextContent);
        Assert.Contains("Process", page.Find("thead").TextContent);
        page.Find("input[type=checkbox]").Change(false);
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview + ".servers", $"<div style=\"{QuasarTheme.BuildCss(BrandingPresets.GetDarkPalette(BrandingPresets.CometWorksId), true)}\">{page.Markup}</div>");
        page.FindAll("button[title='Show live data']")[0].Click();
        Assert.Equal("true", page.FindAll("button[title='Show live data']")[0].GetAttribute("aria-expanded"));
        Assert.Single(page.FindAll(".quasar-server-detail-row"));
        page.FindAll("button[title='Show live data']")[0].Click();
        Assert.Empty(page.FindAll(".quasar-server-detail-row"));
    }

    [Fact]
    public async Task AnalyticsUsesSeparateIncarnationKeysAndReadableNodeLabels()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView);
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var metrics = new MetricsStoreService(catalog, new(), NullLogger<MetricsStoreService>.Instance);
        var profiler = new ProfilerStoreService();
        var stats = new PluginStatsStoreService();
        var registry = new AgentRegistry(null!, metrics, profiler, stats);
        foreach (var epoch in new long[] { 41, 42 })
            registry.UpsertHello(new() { AgentId = "node-" + epoch, UniqueName = "same-slot-name",
                ClusterMode = true, ClusterId = "cluster", ClusterSlot = "slot", ClusterNodeId = "node",
                ClusterEpoch = epoch }, "connection-" + epoch, (_, _) => Task.CompletedTask);
        context.Services.AddSingleton(catalog);
        context.Services.AddSingleton(metrics);
        context.Services.AddSingleton(profiler);
        context.Services.AddSingleton(stats);
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(new BrowserStorageService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton(new ThemePreferenceService(new(context.JSInterop.JSRuntime), _branding, context.JSInterop.JSRuntime));
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Render<MudPopoverProvider>();
        var page = context.Render<Quasar.Components.Pages.Analytics>();
        Assert.Contains("cluster / slot / node / epoch 41", page.Markup);
        Assert.Contains("cluster / slot / node / epoch 42", page.Markup);
        Assert.DoesNotContain("same-slot-name", page.Markup);
        // Check the actual browser request, which also drives the analytics HTTP endpoints.
        await page.WaitForAssertionAsync(() => Assert.NotEmpty(context.JSInterop.Invocations["quasarCharts.sync"]));
        var request = JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
        foreach (var agent in registry.GetAgents()) Assert.Contains(agent.TelemetryKey, request);
        Assert.DoesNotContain("same-slot-name", request);
    }

    [Fact]
    public async Task ChatRoutesNodeViewsByIncarnationAndKeepsLifecycleControlsDisabled()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("editor");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        var registry = new AgentRegistry(null!, null!, null!, null!);
        var sent = new List<ServerCommandEnvelope>();
        foreach (var epoch in new long[] { 41, 42 })
            registry.UpsertHello(new() { AgentId = "node-" + epoch, UniqueName = "same-slot-name",
                ClusterMode = true, ClusterId = "cluster", ClusterSlot = "slot", ClusterNodeId = "node",
                ClusterEpoch = epoch }, "connection-" + epoch, (message, _) =>
                { sent.Add(message.Command!); return Task.CompletedTask; });
        using var supervisor = new DedicatedServerSupervisor(catalog, registry, null!, null!, null!, new(), null!, NullLogger<DedicatedServerSupervisor>.Instance);
        context.Services.AddSingleton(catalog);
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(supervisor);
        context.Render<MudPopoverProvider>();
        var page = context.Render<Chat>();
        var selector = page.FindComponent<MudSelect<string>>();
        foreach (var agent in registry.GetAgents())
        {
            await page.InvokeAsync(() => selector.Instance.ValueChanged.InvokeAsync(agent.TelemetryKey));
            Assert.True(page.FindAll("button").Single(b => b.TextContent.Trim() == "Save").HasAttribute("disabled"));
            Assert.True(page.FindAll("button").Single(b => b.TextContent.Trim() == "Restart").HasAttribute("disabled"));
            await page.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh").ClickAsync();
            Assert.Equal(agent.AgentId, sent[^1].AgentId);
            Assert.Equal("same-slot-name", sent[^1].UniqueName);
        }
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task NativeDetailsPreserveCommandsLiveUpdatesAndPermissions()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers);
        var sent = new List<ServerCommandEnvelope>();
        var registry = new AgentRegistry(null!, null!, null!, null!);
        registry.UpsertHello(new() { AgentId = "agent", UniqueName = "alpha" }, "connection", (message, _) =>
        {
            sent.Add(message.Command!);
            return Task.CompletedTask;
        });
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(profiles);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        var agent = new AgentRuntimeState { AgentId = "agent", IsConnected = true,
            Snapshot = new() { UniqueName = "alpha", ServerName = "Alpha", Metrics = new() { SimSpeed = 0.8f },
                Players = [new() { SteamId = 123, DisplayName = "Engineer", PromoteLevel = "Admin", PingMs = 15 }] } };
        var runtime = new DedicatedServerRuntimeSnapshot { UniqueName = "alpha", State = DedicatedServerProcessState.Running };
        var panel = context.Render<ServerDetailPanel>(p => p.Add(x => x.Agent, agent).Add(x => x.Runtime, runtime));
        var dialogs = context.Render<MudDialogProvider>();
        panel.Find("input").Input(" hello fleet ");
        await panel.Find("form").SubmitAsync();
        Assert.Equal(ServerCommandType.SendChat, Assert.Single(sent).CommandType);
        Assert.Equal("hello fleet", sent[0].Text);
        Assert.Equal(string.Empty, panel.Find("input").GetAttribute("value"));
        var popoverId = panel.Find("[popover]").Id;
        Assert.True(panel.FindAll("[popover] button").Single(x => x.TextContent == "Admin").HasAttribute("disabled"));
        await panel.FindAll("[popover] button").Single(x => x.TextContent == "Moderator").ClickAsync();
        Assert.Equal(ServerCommandType.SetPlayerPromoteLevel, sent[^1].CommandType);
        Assert.Equal(123, sent[^1].SteamId);
        Assert.Equal("Moderator", sent[^1].Text);
        agent.Snapshot.Players = [new() { SteamId = 123, DisplayName = "Engineer", PromoteLevel = "Moderator", PingMs = 25 }];
        runtime.State = DedicatedServerProcessState.Stopping;
        panel.Render();
        Assert.Equal(popoverId, panel.Find("[popover]").Id); // Snapshot updates preserve the native menu element.
        Assert.Contains("25", panel.Find("tbody").TextContent);
        Assert.True(panel.FindAll("button").Single(x => x.TextContent.Trim() == "Save").HasAttribute("disabled"));
        var count = sent.Count;
        var kick = panel.FindAll("[popover] button").Single(x => x.TextContent == "Kick").ClickAsync();
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Kick player?", dialogs.Markup));
        Assert.Equal(count, sent.Count);
        // Role revocation while the confirmation is open cannot execute the action.
        auth.SetPolicies(QuasarPolicyNames.CanView);
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Kick").Click();
        await kick;
        Assert.Equal(count, sent.Count);
        panel.Render();
        Assert.Empty(panel.FindAll("[popover], form"));
        Assert.Contains("Engineer", panel.Markup);
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
        {
            auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers);
            runtime.State = DedicatedServerProcessState.Running;
            panel.Render();
            await File.WriteAllTextAsync(preview + ".details", $"<div style=\"{QuasarTheme.BuildCss(BrandingPresets.GetDarkPalette(BrandingPresets.CometWorksId), true)}\">{panel.Markup}</div>");
        }
    }

    [Fact]
    public async Task FleetOverviewUsesLiveMetricsAndPreservesViews()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers, QuasarPolicyNames.CanEditConfigs);
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var templates = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        using var consent = new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance);
        await consent.SaveAsync(false);
        var registry = new AgentRegistry(null!, null!, null!, null!);
        var resolver = new ManagedDedicatedServerRuntimeResolver(NullLogger<ManagedDedicatedServerRuntimeResolver>.Instance, null!, new(), null!, null!);
        using var warmup = new ManagedRuntimeWarmupService(resolver, NullLogger<ManagedRuntimeWarmupService>.Instance);
        typeof(ManagedRuntimeWarmupService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(warmup, new ManagedRuntimeWarmupSnapshot { State = ManagedRuntimeWarmupState.Complete });
        using var supervisor = new DedicatedServerSupervisor(catalog, registry, null!, resolver, warmup, new(), null!, NullLogger<DedicatedServerSupervisor>.Instance);
        // Seed snapshots without launching hosted services or a dedicated server.
        var agents = (Dictionary<string, AgentRuntimeState>)typeof(AgentRegistry).GetField("_agents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        var states = (System.Collections.IDictionary)typeof(DedicatedServerSupervisor).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supervisor)!;
        var stateType = typeof(DedicatedServerSupervisor).GetNestedType("ManagedServerState", BindingFlags.NonPublic)!;
        var names = new[] { "Europa · Survival", "Pertam · PvP", "Luna · Creative", "Earth · Vanilla", "Mars · Survival", "Test · Staging" };
        var worlds = new[] { "Europa Station", "Dust Frontier", "Lunar Workshop", "Homeworld", "Red Horizon", "Plugin Sandbox" };
        var players = new[] { 12, 18, 4, 7, 6, 99 };
        var slots = new[] { 24, 32, 16, 24, 24, 99 };
        var memory = new long[] { 8396, 9830, 5222, 7475, 8396, 999999 };
        for (var i = 0; i < names.Length; i++)
        {
            var name = "server-" + i;
            var definition = new DedicatedServerDefinition { UniqueName = name, DisplayName = names[i], WorldSaveName = worlds[i], ServerPort = 27016 + i };
            await catalog.UpsertAsync(definition);
            agents[name] = new AgentRuntimeState { AgentId = name, IsConnected = i < 5, LastSeenUtc = DateTimeOffset.UtcNow,
                Snapshot = new() { UniqueName = name, ServerName = names[i], WorldName = worlds[i], HostName = i < 3 ? "eu-01" : "eu-02",
                    Metrics = new() { PlayersOnline = players[i], MaxPlayers = slots[i], SimSpeed = i == 1 ? .92f : 1f, ServerCpuLoadPercent = 62 + i * 25, MemoryWorkingSetMb = memory[i] } } };
            var state = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("UniqueName")!.SetValue(state, name);
            stateType.GetProperty("Definition")!.SetValue(state, definition);
            stateType.GetProperty("State")!.SetValue(state, i < 5 ? DedicatedServerProcessState.Running : DedicatedServerProcessState.Stopped);
            stateType.GetProperty("StartedAtUtc")!.SetValue(state, DateTimeOffset.UtcNow.AddMinutes(-5 - i));
            states[name] = state;
        }
        context.Services.AddSingleton(catalog); context.Services.AddSingleton(clusters);
        context.Services.AddSingleton(profiles); context.Services.AddSingleton(templates);
        context.Services.AddSingleton(consent); context.Services.AddSingleton(registry);
        context.Services.AddSingleton(supervisor); context.Services.AddSingleton(warmup);
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        context.Services.AddSingleton(_branding);
        context.Services.AddSingleton(new BrowserStorageService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton(new ThemePreferenceService(new(context.JSInterop.JSRuntime), _branding, context.JSInterop.JSRuntime));
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        context.Services.AddSingleton(new WebServiceOptions());
        context.Services.AddScoped<ServerManagementActions>();
        using var http = new HttpClient(new UnavailableGateway());
        var gateway = new ClusterGatewayClient(http);
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(new ClusterCommandService(clusters, null!, gateway, null!));
        context.Services.AddSingleton(new ClusterReconciler(clusters, gateway, null!, null!, NullLogger<ClusterReconciler>.Instance));
        var page = context.Render<Home>();
        var totals = page.FindAll(".quasar-metric-value").Select(x => x.TextContent).ToArray();
        Assert.Equal(new[] { "5/ 6 running", "47/ 120 slots", "0.98×", "38.4GiB" }, totals);
        Assert.Equal(6, page.FindAll(".quasar-server-name-cell").Count);
        Assert.Equal(5, page.FindAll(".quasar-activity li").Count);
        Assert.Single(context.JSInterop.Invocations["quasarCharts.sync"]);
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview + ".fleet", $"<div style=\"{QuasarTheme.BuildCss(ThemePalette.QuasarDark(), true)}\">{page.Markup}</div>");
        page.FindAll(".quasar-view-selector button").Single(x => x.TextContent.Contains("Cards")).Click();
        Assert.Empty(page.FindAll(".quasar-server-name-cell"));
        Assert.NotEmpty(page.FindAll(".quasar-card-grid"));
        Assert.Equal(6, page.FindAll(".quasar-fleet-card").Count);
        Assert.Equal(5, page.FindAll("details.quasar-card-disclosure").Count);
        Assert.All(page.FindAll("details.quasar-card-disclosure"), details => Assert.False(details.HasAttribute("open")));
        Assert.Equal(5, page.FindAll("details.quasar-card-disclosure form").Count);
        Assert.Equal(5, page.FindAll(".quasar-card-actions button").Count(button => button.TextContent == "Stop"));
        Assert.Single(page.FindAll(".quasar-card-actions button").Where(button => button.TextContent == "Start"));
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } cardsPreview)
            await File.WriteAllTextAsync(cardsPreview + ".cards", $"<div style=\"{QuasarTheme.BuildCss(ThemePalette.QuasarDark(), true)}\">{page.Markup}</div>");
        page.FindAll(".quasar-view-selector button").Single(x => x.TextContent.Contains("List")).Click();
        Assert.Equal(6, page.FindAll(".quasar-server-name-cell").Count);
    }

    [Fact]
    public async Task NativeConsoleKeepsLogSelectionDownloadsAndRolloverOnTabReturn()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        var ds = MagnetarPaths.GetQuasarServerDedicatedServerAppDataDirectory("console-test");
        var magnetar = MagnetarPaths.GetQuasarServerMagnetarAppDataDirectory("console-test");
        Directory.CreateDirectory(ds);
        Directory.CreateDirectory(magnetar);
        await File.WriteAllTextAsync(Path.Combine(ds, "SpaceEngineersDedicated-old.log"), "Historical server output\n");
        await File.WriteAllTextAsync(Path.Combine(ds, "SpaceEngineersDedicated.log"), "Live server output\nException: sample failure\n");
        File.SetLastWriteTimeUtc(Path.Combine(ds, "SpaceEngineersDedicated-old.log"), DateTime.UtcNow.AddDays(-1));
        await File.WriteAllTextAsync(Path.Combine(magnetar, "info.log"), "Magnetar plugin output\n");
        var page = context.Render<MudDialogProvider>();
        await page.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<ServerConsoleDialog>("Server console", new DialogParameters { [nameof(ServerConsoleDialog.UniqueName)] = "console-test" }));
        page.WaitForAssertion(() => Assert.Contains("Live server output", page.Markup));
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["quasarConfigs.attachRolloverLog"]));
        page.Find("select").Change("SpaceEngineersDedicated-old.log");
        page.WaitForAssertion(() => Assert.Contains("Historical server output", page.Markup));
        Assert.True(page.FindAll("button").Single(x => x.TextContent == "Refresh").HasAttribute("disabled"));
        page.Find("button[title='Download full server log']").Click();
        Assert.Contains("SpaceEngineersDedicated-old.log", context.JSInterop.Invocations["quasarConfigs.downloadFile"].Single().Arguments[0]!.ToString());
        page.FindAll(".quasar-console-tabs button")[1].Click();
        page.WaitForAssertion(() => Assert.Contains("Magnetar plugin output", page.Markup));
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["quasarConfigs.detachRolloverLog"]));
        var attachments = context.JSInterop.Invocations["quasarConfigs.attachRolloverLog"].Count;
        page.FindAll(".quasar-console-tabs button")[0].Click();
        page.WaitForAssertion(() => Assert.True(context.JSInterop.Invocations["quasarConfigs.attachRolloverLog"].Count > attachments));
        Assert.Contains("Historical server output", page.Markup);
        page.Find("select").Change("__latest__");
        page.FindAll("button").Single(x => x.TextContent == "Errors").Click();
        page.WaitForAssertion(() => Assert.Contains("sample failure", page.Markup));
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview + ".console", $"<div style=\"{QuasarTheme.BuildCss(ThemePalette.QuasarDark(), true)}\">{page.Markup}</div>");
    }

    private sealed class UnavailableGateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    public void Dispose()
    {
        _branding.Dispose();
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
