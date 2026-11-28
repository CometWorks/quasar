using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Quasar.Services.Auth;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Components.Pages;
using Quasar.Client.Formatting;
using Quasar.Components.Shared;
using Quasar.Client.Components;
using Quasar.Client.Fleet;
using Quasar.Client.Ui;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

public sealed class NativeUiThemeTests
{
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
    public async Task DashboardPopoversUseTheSelectedBrandingAndThemeMode()
    {
        await using var context = new QuasarBunitContext();
        context.Services.AddMudServices();
        context.Services.AddQuasarUi();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var branding = BrandingSettings.Normalize(null);
        branding.LightPalette.Surface = "#faf0d4";
        var providers = context.Render<QuasarThemeProvider>(p => p.Add(x => x.Branding, branding));
        var theme = providers.FindComponent<MudThemeProvider>();
        Assert.True(theme.Instance.GetState(x => x.IsDarkMode));
        await providers.InvokeAsync(() => providers.Instance.ApplyTheme(false, null));
        Assert.False(theme.Instance.GetState(x => x.IsDarkMode));
        Assert.Equal("#faf0d4ff", theme.Instance.Theme!.PaletteLight.Surface.ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA));
        var updated = branding.Clone();
        updated.DarkPalette.Surface = "#302640";
        await providers.InvokeAsync(() => providers.Instance.ApplyTheme(true, updated));
        Assert.Equal("#302640ff", theme.Instance.Theme!.PaletteDark.Surface.ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA));
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
        await using var context = new QuasarBunitContext();
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
        await using var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.AddAuthorization().SetAuthorized("admin");
        context.Services.AddDirectCoreOperations();
        context.Services.AddSingleton(provider => new Quasar.Client.DashboardSdk(new DashboardTestTransport(provider)));
        context.JSInterop.Setup<bool>("quasarSdk.darkMode").SetResult(true);
        context.Services.AddSingleton(_branding);
        var page = context.Render<Quasar.Client.Administration.AppearancePage>();
        Assert.Equal(58, page.FindAll("input[type=color]").Count);
        Assert.Equal("Quasar.png", page.Find("img[alt='Dark-mode logo preview']").GetAttribute("src"));
        Assert.Equal("Quasar-inverted-transparent.png", page.Find("img[alt='Light-mode logo preview']").GetAttribute("src"));
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
        // Optional offline inspection of the actual rendered component; no Quasar process.
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, $"<div data-theme=\"dark\" style=\"{QuasarTheme.BuildCss(_branding.Settings.DarkPalette, true)}\">{page.Markup}</div>");
        page.FindAll("button").Single(x => x.TextContent.Contains("Reset to Quasar default")).Click();
        await page.WaitForAssertionAsync(() => Assert.Equal(BrandingPresets.QuasarId, _branding.Settings.PresetId));
        Assert.Equal("Quasar", _branding.Settings.AppName);
    }

    [Fact]
    public async Task AppearanceImagesStayUnderAHostedPanelPrefix()
    {
        await using var context = new QuasarBunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.AddAuthorization().SetAuthorized("admin");
        context.Services.AddDirectCoreOperations();
        context.Services.AddSingleton(provider => new Quasar.Client.DashboardSdk(new DashboardTestTransport(provider)));
        context.JSInterop.Setup<bool>("quasarSdk.darkMode").SetResult(true);
        context.Services.AddSingleton(_branding);
        var navigation = new PrefixNavigation("https://dash.test/my-server-panel/");
        context.Services.AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(navigation);
        void AssertImagesUnderPrefix(IRenderedComponent<Quasar.Client.Administration.AppearancePage> rendered) => Assert.All(rendered.FindAll("img"), image =>
            Assert.StartsWith("/my-server-panel/", new Uri(new Uri(navigation.BaseUri), image.GetAttribute("src")!).AbsolutePath));

        var page = context.Render<Quasar.Client.Administration.AppearancePage>();
        AssertImagesUnderPrefix(page); // Dark palette, default logos.
        page.Find("button[aria-pressed='false']").Click();
        AssertImagesUnderPrefix(page); // Light palette, default logos.

        // Uploaded assets are stored with a root-relative /branding path.
        foreach (bool dark in new[] { true, false })
            await _branding.SaveLogoAsync(dark, new MemoryStream([1]), ".png");
        page = context.Render<Quasar.Client.Administration.AppearancePage>();
        AssertImagesUnderPrefix(page);
        page.Find("button[aria-pressed='false']").Click();
        AssertImagesUnderPrefix(page);
    }

    private sealed class PrefixNavigation : Microsoft.AspNetCore.Components.NavigationManager
    {
        public PrefixNavigation(string origin) => Initialize(origin, origin);
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    [Fact]
    public async Task ServerTableSortsMixedRowsKeepsControlsAndExpandsDetails()
    {
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.ComponentFactories.AddStub<FleetLiveDetails>();
        context.ComponentFactories.AddStub<FleetClusterOverview>();
        var warmup = new ManagedRuntimeWarmupSnapshot { Message = "Runtime preparing" };
        var data = new FleetDashboard(new(3,0,1,null,null,0,null,0,null,0,0,0),
            [FleetDashboardTestTransport.Row("zulu", "Zulu", false, 9999), FleetDashboardTestTransport.Row("alpha", "Alpha", false, 27016), FleetDashboardTestTransport.Row("cluster", "Beta Cluster", true, 18000)],
            [new("orphan", "alpha", "Orphan node", "", "missing", "slot", "node", 42, true, false)],
            [], [], [], [], warmup, false, false, true, true, true, true, false, false);
        var page = context.Render<FleetWorkspace>(p => p.Add(c => c.Dashboard, data).Add(c => c.IsList, true));
        page.Find(".quasar-agent-details button").Click();
        Assert.Equal(new[] { "alpha", "cluster", "zulu" }, page.FindAll(".quasar-server-name-cell").Select(x => x.GetAttribute("title")));
        Assert.Empty(page.FindAll("a[title='Convert to cluster']"));
        page.FindAll("thead button").Single(x => x.TextContent.Trim().StartsWith("Game port")).Click();
        Assert.Equal(new[] { "zulu", "cluster", "alpha" }, page.FindAll(".quasar-server-name-cell").Select(x => x.GetAttribute("title")));
        Assert.Equal("ascending", page.FindAll("th")[3].GetAttribute("aria-sort"));
        page.FindAll("thead button").Single(x => x.TextContent.Trim().StartsWith("Game port")).Click();
        Assert.Equal("descending", page.FindAll("th")[3].GetAttribute("aria-sort"));
        page.Find("input[type=search]").Input("18000");
        Assert.Equal("cluster", Assert.Single(page.FindAll(".quasar-server-name-cell")).GetAttribute("title"));
        page.Find("input[type=search]").Input("");
        Assert.True(page.Find("button[title='Runtime preparing']").HasAttribute("disabled"));
        foreach (var label in new[] { "Show console output", "Clone server", "Create world template", "Edit server", "Delete server" })
            Assert.NotEmpty(page.FindAll($"button[title='{label}']"));
        Assert.NotEmpty(page.FindAll("a[title='Open cluster details']"));
        var menuTarget = page.Find("button[title='More server actions']").GetAttribute("popovertarget");
        Assert.NotNull(page.Find($"[id='{menuTarget}'][popover=auto]"));
        page.Find("input[type=search]").Input("ALPHA");
        Assert.Equal("alpha", Assert.Single(page.FindAll(".quasar-server-name-cell")).GetAttribute("title"));
        page.Find("input[type=search]").Input("no matching server");
        page.FindAll("button").Single(x => x.TextContent == "Clear filters").Click();
        Assert.Equal(3, page.FindAll(".quasar-server-name-cell").Count);
        page.Find("input[type=checkbox]").Change(true);
        Assert.Contains("Agent / WA", page.Find("thead").TextContent);
        Assert.Contains("Process", page.Find("thead").TextContent);
        page.FindAll("button[title='Show live data']")[0].Click();
        Assert.Single(page.FindAll(".quasar-server-detail-row"));
        page.FindAll("button[title='Show live data']")[0].Click();
        Assert.Empty(page.FindAll(".quasar-server-detail-row"));
        page.FindAll("button[title='Show live data']")[1].Click();
        Assert.True(page.FindComponent<Stub<FleetClusterOverview>>().Instance.Parameters.Get(c => c.Row).IsCluster);
        page.Render(p => p.Add(c => c.IsList, false));
        var clusterCard = page.FindAll(".quasar-fleet-card").Single(card => card.TextContent.Contains("Beta Cluster"));
        Assert.Equal(new[] { "Gateway", "Desired state" }, clusterCard.QuerySelectorAll("dt").Select(label => label.TextContent));
        Assert.DoesNotContain("Process CPU", clusterCard.TextContent);
        Assert.True(page.FindComponent<Stub<FleetClusterOverview>>().Instance.Parameters.Get(c => c.CollapseDetails));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FleetActionsUseLifecycleColorsInBothLayouts(bool isList)
    {
        await using var context = new QuasarBunitContext();
        context.Services.AddMudServices();
        context.Services.AddScoped<Quasar.Client.DashboardSdk>();
        context.JSInterop.Setup<bool>("quasarSdk.allowed", "ClusterManage").SetResult(true);
        context.ComponentFactories.AddStub<FleetClusterOverview>();
        var data = new FleetDashboard(new(4, 2, 0, null, null, 0, null, 0, null, 0, 0, 0),
            [FleetDashboardTestTransport.Row("starting") with { State = "Starting", Goal = "On" },
             FleetDashboardTestTransport.Row("running") with { State = "Running", Goal = "On" },
             FleetDashboardTestTransport.Row("stopped-cluster", cluster: true),
             FleetDashboardTestTransport.Row("running-cluster", cluster: true) with { State = "Running", Goal = "On" }],
            [], [], [], [], [], new(), HealthDisabled: false, ConsentNeeded: false, Linux: true,
            EditServers: false, EditConfigs: false, Control: true, ClusterManage: true, ClusterQuery: false);
        var page = context.Render<FleetWorkspace>(p => p.Add(c => c.Dashboard, data).Add(c => c.IsList, isList));
        Assert.Contains("quasar-text-success", page.Find("button[aria-label='Start cluster']").ClassList);
        foreach (var label in new[] { "Stop server", "Kill server (unsaved changes are lost)", "Gracefully stop cluster", "Delete cluster" })
        {
            var buttons = page.FindAll($"button[aria-label='{label}']");
            Assert.NotEmpty(buttons);
            Assert.All(buttons, button => Assert.Contains("quasar-text-error", button.ClassList));
        }
        Assert.Equal(!isList, page.Find("button[aria-label='Show console output']").ClassList.Contains("quasar-tone-primary"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyticsSelectsWholeClustersAndIncludesEverySlot(bool migrateLegacySelection)
    {
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage);
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var metrics = new MetricsStoreService(catalog, new(), NullLogger<MetricsStoreService>.Instance);
        var profiler = new ProfilerStoreService();
        var stats = new PluginStatsStoreService();
        var registry = new AgentRegistry(null!, metrics, profiler, stats);
        foreach (var epoch in new long[] { 41, 42 })
            registry.UpsertHello(new() { AgentId = "node-" + epoch, UniqueName = "same-slot-name",
                ClusterMode = true, ClusterId = "cluster", ClusterSlot = "slot", ClusterNodeId = "node-" + epoch,
                ClusterEpoch = epoch }, "connection-" + epoch, (_, _) => Task.CompletedTask);
        registry.UpsertHello(new() { AgentId = "other", UniqueName = "same-slot-name", ClusterMode = true,
            ClusterId = "cluster", ClusterSlot = "other-slot", ClusterNodeId = "node-9", ClusterEpoch = 1 },
            "connection-other", (_, _) => Task.CompletedTask);
        foreach (var agent in registry.GetAgents())
        {
            var sample = new MetricSample(DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds(), 1, 20, 1024, 16.67f, 7, 100, 10, 20);
            metrics.Enqueue(agent.TelemetryKey, sample);
            metrics.GetStore(agent.TelemetryKey)!.Ingest(sample);
        }
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }).Build());
        await clusters.CreateAsync(new("cluster", "Survival cluster", "http://gateway.test", "ADMIN"), default);
        context.Services.AddSingleton(clusters);
        if (migrateLegacySelection)
            context.JSInterop.Setup<string?>("quasarConfigs.getLocalStorage", "quasar.analytics.view.v2")
                .SetResult(JsonSerializer.Serialize(new AnalyticsViewConfig { SelectedUniqueNames = [registry.GetAgents()[0].AnalyticsKey] }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        context.Services.AddSingleton(catalog);
        context.Services.AddSingleton(metrics);
        context.Services.AddSingleton(profiler);
        context.Services.AddSingleton(stats);
        context.Services.AddSingleton(new PluginStatsHistoryService(new(), NullLogger<PluginStatsHistoryService>.Instance));
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Render<MudPopoverProvider>();
        var page = context.Render<Quasar.Client.Components.AnalyticsView>();
        var labels = page.FindComponents<MudCheckBox<bool>>().Select(box => box.Instance.Label).ToList();
        Assert.Equal(["Survival cluster"], labels);
        Assert.Contains("other-slot", page.Find(".analytics-node-legend").TextContent);
        Assert.Contains("slot", page.Find(".analytics-node-legend").TextContent);
        Assert.DoesNotContain("epoch", page.Markup);
        Assert.DoesNotContain("same-slot-name", page.Markup);
        Assert.Contains($"Total process CPU: {NumberText.Display(40.0, "0.0")}%", page.Find(".analytics-chip-row").TextContent);
        Assert.Contains($"Total process memory: {NumberText.Display(2048, "0")} MB", page.Find(".analytics-chip-row").TextContent);
        Assert.DoesNotContain("Players:", page.Find(".analytics-chip-row").TextContent);
        // Check the actual browser request, which also drives the analytics HTTP endpoints.
        await page.WaitForAssertionAsync(() => Assert.NotEmpty(context.JSInterop.Invocations["quasarCharts.sync"]));
        var request = JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
        var agents = registry.GetAgents();
        Assert.Equal(2, agents.Select(agent => agent.AnalyticsKey).Distinct().Count());
        foreach (var agent in agents)
        {
            Assert.Contains(agent.AnalyticsKey, request);
            Assert.DoesNotContain(agent.TelemetryKey, request);
        }
        Assert.DoesNotContain("same-slot-name", request);
        Assert.True(JsonDocument.Parse(request).RootElement.GetProperty("lines").GetBoolean());

        // Selecting none is persistent; toggling the single cluster selects every node again.
        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Select none").ClickAsync();
        request = JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
        Assert.Empty(JsonDocument.Parse(request).RootElement.GetProperty("servers").EnumerateArray());
        await page.FindComponent<MudCheckBox<bool>>().InvokeAsync(() => page.FindComponent<MudCheckBox<bool>>().Instance.ValueChanged.InvokeAsync(true));
        request = JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
        Assert.Equal(2, JsonDocument.Parse(request).RootElement.GetProperty("servers").GetArrayLength());

        // A newly discovered slot joins the selected cluster automatically.
        registry.UpsertHello(new() { AgentId = "new-slot", UniqueName = "same-slot-name", ClusterMode = true,
            ClusterId = "cluster", ClusterSlot = "slot-new", ClusterNodeId = "new-node", ClusterEpoch = 1 },
            "connection-new", (_, _) => Task.CompletedTask);
        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Refresh").ClickAsync();
        request = JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0]);
        Assert.Equal(3, JsonDocument.Parse(request).RootElement.GetProperty("servers").GetArrayLength());
    }

    [Fact]
    public async Task ChatRoutesNodeViewsByIncarnationAndKeepsLifecycleControlsDisabled()
    {
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("editor");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers, QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
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
        var page = context.Render<Quasar.Client.Components.ChatView>();
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
        using var data = new TemporaryQuasarDirectory();
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers);
        var sent = new List<ServerCommandEnvelope>();
        var known = new KnownPlayerCatalog(NullLogger<KnownPlayerCatalog>.Instance);
        var registry = new AgentRegistry(known, null!, null!, null!);
        registry.UpsertHello(new() { AgentId = "agent", UniqueName = "alpha" }, "connection", (message, _) =>
        {
            sent.Add(message.Command!);
            registry.UpdateCommandResult(new() { AgentId = "agent", CommandId = message.Command!.CommandId, Success = true }, "connection");
            return Task.CompletedTask;
        });
        var snapshot = new AgentSnapshot { AgentId = "agent", UniqueName = "alpha", TelemetryPaused = true,
            Players = [new() { SteamId = 123, DisplayName = "Engineer", PromoteLevel = "Admin", PingMs = 15 }] };
        registry.UpdateSnapshot(snapshot,"connection");
        context.Services.AddSingleton(registry);
        context.Services.AddSingleton(known);
        var dialogs = context.Render<MudDialogProvider>();
        var panel = context.Render<FleetLiveDetails>(p => p.Add(x => x.UniqueName, "alpha").Add(x => x.AgentId, "agent")
            .Add(x => x.CanControl, true).Add(x => x.Running, true).Add(x => x.Connected, true));
        panel.Find("input").Input(" hello fleet ");
        await panel.Find("form").SubmitAsync();
        Assert.Equal(ServerCommandType.SendChat, Assert.Single(sent).CommandType);
        Assert.Equal("hello fleet", sent[0].Text);
        Assert.Equal(string.Empty, panel.Find("input").GetAttribute("value"));
        var actionsTarget = panel.Find("button[aria-label='Actions for Engineer']").GetAttribute("popovertarget");
        Assert.Equal(actionsTarget, panel.Find(".quasar-player-actions[popover='auto']").Id);
        Assert.True(panel.FindAll(".quasar-player-actions button").Single(x => x.TextContent == "Admin").HasAttribute("disabled"));
        snapshot.Players[0].PingMs = 25;
        registry.UpdateSnapshot(snapshot,"connection");
        var promotion = panel.FindAll(".quasar-player-actions button").Single(x => x.TextContent == "Moderator").ClickAsync();
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Set role to Moderator", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Confirm").Click();
        await promotion;
        Assert.Equal(ServerCommandType.SetPlayerPromoteLevel, sent[^1].CommandType);
        Assert.Equal(123, sent[^1].SteamId);
        Assert.Equal("Moderator", sent[^1].Text);
        Assert.Contains("25", panel.Find("tbody").TextContent);
        var count = sent.Count;
        var kick = panel.FindAll(".quasar-player-actions button").Single(x => x.TextContent == "Kick").ClickAsync();
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Kick", dialogs.Markup));
        auth.SetPolicies(QuasarPolicyNames.CanView);
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Confirm").Click();
        await kick;
        Assert.Equal(count, sent.Count);
        panel.Render(p => p.Add(x => x.CanControl, false));
        Assert.Empty(panel.FindAll(".quasar-player-actions, form"));
        Assert.Contains("Engineer", panel.Markup);
    }

    [Fact]
    public async Task FleetOverviewUsesLiveMetricsAndPreservesViews()
    {
        await using var context = new OperationsBrowserContext();
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
                LastSnapshotReceivedUtc = DateTimeOffset.UtcNow,
                Snapshot = new() { UniqueName = name, IsRunning = true, ServerName = names[i], WorldName = worlds[i], HostName = i < 3 ? "eu-01" : "eu-02",
                    Metrics = new() { PlayersOnline = players[i], MaxPlayers = slots[i], SimSpeed = i == 1 ? .92f : 1f, ServerCpuLoadPercent = 62 + i * 25, MemoryWorkingSetMb = memory[i] } } };
            var state = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("UniqueName")!.SetValue(state, name);
            stateType.GetProperty("Definition")!.SetValue(state, definition);
            stateType.GetProperty("State")!.SetValue(state, i < 5 ? DedicatedServerProcessState.Running : DedicatedServerProcessState.Stopped);
            stateType.GetProperty("StartedAtUtc")!.SetValue(state, DateTimeOffset.UtcNow.AddMinutes(-5 - i));
            states[name] = state;
        }
        context.Services.AddSingleton(catalog); context.Services.AddSingleton(clusters);
        context.Services.AddSingleton(new ClusterOperationStore(NullLogger<ClusterOperationStore>.Instance));
        context.Services.AddSingleton(new ClusterPlayerService(null!, null!, null!, NullLogger<ClusterPlayerService>.Instance));
        context.Services.AddSingleton(profiles); context.Services.AddSingleton(templates);
        context.Services.AddSingleton(consent); context.Services.AddSingleton(registry);
        context.Services.AddSingleton(supervisor); context.Services.AddSingleton(warmup);
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        context.Services.AddSingleton(_branding);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddSingleton(new WebServiceOptions());
        context.Services.AddSingleton<Quasar.Services.Backup.ServerRestoreCoordinator>();
        context.Services.AddSingleton<ServerWorldImportService>();
        using var http = new HttpClient(new UnavailableGateway());
        var gateway = new ClusterGatewayClient(http);
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(new ClusterCommandService(clusters, null!, gateway, null!));
        context.Services.AddSingleton(new ClusterReconciler(clusters, gateway, null!, null!, NullLogger<ClusterReconciler>.Instance));
        var page = context.Render<Home>();
        var totals = page.FindAll(".quasar-metric-value").Select(x => x.TextContent).ToArray();
        Assert.Equal(new[] { "5/ 6 running", "47/ 120 slots", NumberText.Display(0.98, "0.00") + "×", NumberText.Display(38.4, "0.0") + "GiB" }, totals);
        Assert.Equal(6, page.FindAll(".quasar-server-name-cell").Count);
        Assert.Equal(5, page.FindAll(".quasar-activity li").Count);
        Assert.Single(context.JSInterop.Invocations["quasarCharts.sync"]);
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview + ".fleet", $"<div style=\"{QuasarTheme.BuildCss(ThemePalette.QuasarDark(), true)}\">{page.Markup}</div>");
        page.FindAll(".quasar-view-selector button").Single(x => x.TextContent.Contains("Cards")).Click();
        Assert.Empty(page.FindAll(".quasar-server-name-cell"));
        Assert.NotEmpty(page.FindAll(".quasar-card-grid"));
        Assert.Equal(6, page.FindAll(".quasar-fleet-card").Count);
        Assert.Empty(page.FindAll(".quasar-players-table"));
        page.Find(".quasar-card-disclosure > button").Click();
        Assert.Single(page.FindAll(".quasar-players-table"));
        Assert.Single(page.FindAll(".quasar-chat-form"));
        Assert.Equal(5, page.FindAll(".quasar-card-actions button[title='Stop server']").Count);
        Assert.Single(page.FindAll(".quasar-card-actions button[title='Select a world save containing Sandbox.sbc.'][disabled]"));
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } cardsPreview)
            await File.WriteAllTextAsync(cardsPreview + ".cards", $"<div style=\"{QuasarTheme.BuildCss(ThemePalette.QuasarDark(), true)}\">{page.Markup}</div>");
        page.FindAll(".quasar-view-selector button").Single(x => x.TextContent.Contains("List")).Click();
        Assert.Equal(6, page.FindAll(".quasar-server-name-cell").Count);
    }

    [Fact]
    public async Task NativeConsoleKeepsLogSelectionDownloadsAndRolloverOnTabReturn()
    {
        await using var context = new OperationsBrowserContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var authorization = context.AddAuthorization();
        authorization.SetAuthorized("viewer");
        authorization.SetPolicies(QuasarPolicyNames.CanView);
        var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        await catalog.UpsertAsync(new() { UniqueName = "console-test", DisplayName = "Console", WorldSaveName = "World", ServerPort = 27022 });
        context.Services.AddSingleton(catalog);
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
        // The click handler is asynchronous. Wait until it has called the SDK, which a
        // loaded machine can delay, before reading the recorded call.
        page.WaitForAssertion(
            () => Assert.NotEmpty(context.JSInterop.Invocations["quasarSdk.download"]),
            TimeSpan.FromSeconds(5));
        var download = context.JSInterop.Invocations["quasarSdk.download"].Single();
        Assert.Equal("quasar.core", download.Arguments[0]);
        Assert.Equal("logs.server.read", download.Arguments[1]);
        Assert.Contains("SpaceEngineersDedicated-old.log", System.Text.Json.JsonSerializer.Serialize(download.Arguments[2]));
        page.FindAll(".quasar-console-tabs button")[1].Click();
        page.WaitForAssertion(() => Assert.Contains("Magnetar plugin output", page.Markup));
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["quasarConfigs.detachRolloverLog"]));
        var attachments = context.JSInterop.Invocations["quasarConfigs.attachRolloverLog"].Count;
        page.FindAll(".quasar-console-tabs button")[0].Click();
        page.WaitForAssertion(() => Assert.True(context.JSInterop.Invocations["quasarConfigs.attachRolloverLog"].Count > attachments));
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

    [Fact]
    public async Task PluginStatsHistoryChartsOneLabelOfAWholeCluster()
    {
        await using var context = new OperationsBrowserContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery);
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = Path.Combine(_root, "clusters") }).Build());
        await clusters.CreateAsync(new("cluster", "Survival cluster", "http://gateway.test", "ADMIN"), default);
        using var history = new PluginStatsHistoryService(new(), NullLogger<PluginStatsHistoryService>.Instance);
        var clusterKey = PluginStatsHistoryService.ClusterKey("cluster");
        history.Record(clusterKey, new PluginStatsProvider
        {
            Provider = "client-lag", CapturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Groups = [new() { Name = "ClientLagRow", LabelDescription = "Player Steam ID",
                Fields = [new() { Name = "P99Ms", Kind = "gauge", Description = "Tick time p99", Unit = "ms", OverTime = "Max" }],
                Instances = [new() { Label = "76561198000000001", Values = [12.5] }] }],
        });
        context.Services.AddSingleton(catalog);
        context.Services.AddSingleton(clusters);
        context.Services.AddSingleton(history);
        context.Render<MudPopoverProvider>();

        var page = context.Render<Quasar.Client.Components.PluginStatsHistoryView>();

        await page.WaitForAssertionAsync(() => Assert.NotEmpty(context.JSInterop.Invocations["quasarCharts.sync"]));
        var request = JsonDocument.Parse(JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0])).RootElement;
        Assert.Equal([clusterKey], request.GetProperty("servers").EnumerateArray().Select(server => server.GetString()));
        Assert.Equal("Survival cluster (cluster)", request.GetProperty("names").GetProperty(clusterKey).GetString());
        Assert.Equal(PluginStatsHistoryService.BuildMetricKey("client-lag", "ClientLagRow", "P99Ms", "76561198000000001"),
            request.GetProperty("metrics")[0].GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("yMax").ValueKind);

        // The Y axis max goes to the chart only
        var calls = context.JSInterop.Invocations["quasarCharts.sync"].Count;
        await page.InvokeAsync(() => page.Find(".plugin-history-ymax input").Change("500"));
        await page.WaitForAssertionAsync(() => Assert.True(context.JSInterop.Invocations["quasarCharts.sync"].Count > calls));
        request = JsonDocument.Parse(JsonSerializer.Serialize(context.JSInterop.Invocations["quasarCharts.sync"].Last().Arguments[0])).RootElement;
        Assert.Equal(500, request.GetProperty("yMax").GetDouble());
    }

    public void Dispose()
    {
        _branding.Dispose();
        _cache.SetValue(null, _previousRoot);
        TestDirectories.Delete(_root);
    }
}
