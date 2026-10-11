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
    public void CssRejectsDeclarationInjectionAndKeepsOriginalDefaults()
    {
        var palette = ThemePalette.QuasarDark();
        palette.Primary = "red;position:fixed;inset:0";
        var css = QuasarTheme.BuildCss(palette, true);
        Assert.DoesNotContain("position", css);
        Assert.Contains("--quasar-palette-primary:#f5f5f5ff;", css);
        Assert.Equal("#111111", BrandingPresets.GetLightPalette(BrandingPresets.QuasarId).Primary);
        Assert.Equal("#18191b", BrandingPresets.GetDarkPalette(BrandingPresets.CometWorksId).Background);
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
        Assert.Equal(new[] { "alpha", "cluster", "zulu" }, page.FindAll("tbody > tr").Select(x => x.Children[3].TextContent));
        Assert.Empty(page.FindAll("a[title='Convert to cluster']")); // Existing ClusterManage policy still applies.
        page.FindAll("thead button").Single(x => x.TextContent.Trim().StartsWith("Endpoint")).Click();
        Assert.Equal(new[] { "zulu", "alpha", "cluster" }, page.FindAll("tbody > tr").Select(x => x.Children[3].TextContent));
        Assert.Equal("ascending", page.FindAll("th")[4].GetAttribute("aria-sort"));
        page.FindAll("thead button").Single(x => x.TextContent.Trim().StartsWith("Endpoint")).Click();
        Assert.Equal("descending", page.FindAll("th")[4].GetAttribute("aria-sort"));
        Assert.True(page.Find("button[title='Runtime preparing']").HasAttribute("disabled"));
        Assert.True(page.Find("button[title^='Wait for a stable']").HasAttribute("disabled"));
        foreach (var label in new[] { "Show console output", "Clone server", "Create world template", "Edit server", "Delete server", "Open cluster details" })
            Assert.NotEmpty(page.FindAll($"button[title='{label}']"));
        if (Environment.GetEnvironmentVariable("QUASAR_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview + ".servers", $"<div style=\"{QuasarTheme.BuildCss(BrandingPresets.GetDarkPalette(BrandingPresets.CometWorksId), true)}\">{page.Markup}</div>");
        page.FindAll("button[title='Show live data']")[0].Click();
        Assert.Equal("true", page.FindAll("button[title='Show live data']")[0].GetAttribute("aria-expanded"));
        Assert.Single(page.FindAll(".quasar-server-detail-row"));
        page.FindAll("button[title='Show live data']")[0].Click();
        Assert.Empty(page.FindAll(".quasar-server-detail-row"));
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
