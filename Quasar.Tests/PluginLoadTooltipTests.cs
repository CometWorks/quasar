using Bunit;
using Magnetar.Protocol.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class PluginLoadTooltipTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PluginWarningNamesMissingPluginsAndUpdatesWithLiveTelemetry(bool collapseDetails)
    {
        using var data = new TemporaryQuasarDirectory();
        await using var context = new QuasarBunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.AddAuthorization().SetAuthorized("viewer");
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        await profiles.UpsertAsync(new()
        {
            ConfigProfileId = "plugins", Name = "Plugins",
            Plugins = [new() { PluginId = "loaded", DisplayName = "Loaded plugin" },
                new() { PluginId = "broken", DisplayName = "Broken <plugin>" },
                new() { PluginId = "no-name" }],
        });
        context.Services.AddSingleton(profiles);
        context.Services.AddSingleton(new AgentRegistry(null!, null!, null!, null!));
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        var snapshot = new AgentSnapshot
        {
            Metrics = new() { PluginsLoaded = 10 },
            Plugins = [new() { PluginId = @"C:\plugins\LOADED\Main.dll", IsLoaded = true },
                new() { PluginId = "broken", DisplayName = "Broken <plugin>", IsLoaded = false },
                new() { PluginId = "extra", IsLoaded = true }],
        };
        var panel = context.Render<ServerDetailPanel>(p => p
            .Add(x => x.Server, new DedicatedServerDefinition { ConfigProfileId = "plugins" })
            .Add(x => x.Agent, new AgentRuntimeState { IsConnected = true, Snapshot = snapshot })
            .Add(x => x.CollapseDetails, collapseDetails));
        AngleSharp.Dom.IElement PluginMetric() => panel.FindAll(".quasar-detail-metrics > div")
            .Single(e => e.QuerySelector(".quasar-eyebrow")?.TextContent == "Plugins");

        var metric = PluginMetric();
        Assert.Equal("1/3", metric.QuerySelector("strong")!.TextContent);
        Assert.Contains("quasar-tone-warning", metric.QuerySelector("strong")!.ClassList);
        Assert.Equal("Not loaded: Broken <plugin>, no-name", metric.GetAttribute("title"));
        Assert.Equal(metric.GetAttribute("title"), metric.QuerySelector(".quasar-visually-hidden")!.TextContent);
        Assert.Empty(metric.QuerySelectorAll("plugin")); // Plugin names remain text, including in the tooltip.

        // Count-only telemetry cannot identify which selections failed to load.
        snapshot.Plugins.Clear();
        snapshot.Metrics.PluginsLoaded = 1;
        panel.Render();
        Assert.Equal("1/3", PluginMetric().QuerySelector("strong")!.TextContent);
        Assert.Equal("Some configured plugins are not loaded. Plugin names are unavailable; check the server logs.",
            PluginMetric().GetAttribute("title"));

        snapshot.Plugins = [new() { PluginId = "loaded", IsLoaded = true },
            new() { PluginId = "broken", IsLoaded = true }, new() { PluginId = "no-name", IsLoaded = true }];
        panel.Render();
        Assert.Equal("3/3", PluginMetric().QuerySelector("strong")!.TextContent);
        Assert.DoesNotContain("quasar-tone-warning", PluginMetric().QuerySelector("strong")!.ClassList);
        Assert.False(PluginMetric().HasAttribute("title"));
        Assert.Empty(PluginMetric().QuerySelector(".quasar-visually-hidden")!.TextContent);
    }
}
