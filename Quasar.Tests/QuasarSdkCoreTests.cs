using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Plugin.Abstractions;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

public sealed partial class QuasarSdkTests
{
    private static void RegisterCore(IServiceCollection services)
    {
        services.AddSingleton(_ => new MetricsStoreService(null!, new AnalyticsStoreOptions(), NullLogger<MetricsStoreService>.Instance));
        services.AddSingleton<ProfilerStoreService>();
        services.AddSingleton<PluginStatsStoreService>();
        services.AddSingleton<AnalyticsSeriesService>();
        services.AddSingleton<DedicatedServerCatalog>();
        services.AddQuasarCoreOperations();
    }

    [Fact]
    public async Task CoreReadAndDownloadShareSdkRulesAndWorkWithoutNebula()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using (var denied = await client.GetAsync(Prefix + "/api/analytics/series")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", QuasarRoles.Viewer);
        var catalog = app.Services.GetRequiredService<DedicatedServerCatalog>();
        var server = new DedicatedServerDefinition { UniqueName = "sdk-logs", WorldSaveName = "World" };
        await catalog.UpsertAsync(server);
        var directory = DedicatedServerPathResolver.Resolve(catalog.GetServer("sdk-logs")!).DedicatedServerAppDataPath;
        Directory.CreateDirectory(directory);
        var bytes = System.Text.Encoding.UTF8.GetBytes("complete log bytes\nincluding unicode: Δ\n");
        await File.WriteAllBytesAsync(Path.Combine(directory, "SpaceEngineersDedicated-test.log"), bytes);
        var metrics = app.Services.GetRequiredService<MetricsStoreService>();
        var sample = new MetricSample(60, 1, 155, 1024, 16.67f, 0, 0, 0, 0);
        metrics.Enqueue("sdk-logs", sample);
        metrics.GetStore("sdk-logs")!.Ingest(sample);
        var query = "?from=60&to=63&servers=sdk-logs&metrics=cpu&maxPoints=1000";
        using var legacy = await client.GetAsync(Prefix + "/api/analytics/series" + query);
        using var sdk = await SendSdkAsync(client, "/operations/quasar.core/analytics.series.read",
            """{"from":60,"to":63,"servers":["sdk-logs"],"metrics":["cpu"],"maxPoints":1000}""");
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(await legacy.Content.ReadAsStringAsync(), await sdk.Content.ReadAsStringAsync());
        Assert.Equal(155, (await sdk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("charts")[0].GetProperty("series")[0].GetProperty("y")[0].GetDouble());
        var logs = Prefix + "/api/servers/sdk-logs/logs/server/download";
        using var oldRead = await client.GetAsync(logs);
        Assert.Equal(bytes, await oldRead.Content.ReadAsByteArrayAsync());
        Assert.True(oldRead.Headers.CacheControl?.NoStore);
        using var reserved = await SendSdkAsync(client, "/resources/quasar.core/logs.server.read", """{"uniqueName":"sdk-logs"}""");
        Assert.Equal(HttpStatusCode.OK, reserved.StatusCode);
        var resource = await reserved.Content.ReadFromJsonAsync<JsonElement>();
        using var streamed = await client.GetAsync(resource.GetProperty("url").GetString());
        Assert.Equal(bytes, await streamed.Content.ReadAsByteArrayAsync());
        Assert.Contains("SpaceEngineersDedicated-test.log", streamed.Content.Headers.ContentDisposition!.ToString());
        using var traversal = await client.GetAsync(logs + "?name=../SpaceEngineersDedicated-test.log");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<QuasarOperationService>().SaveRuleAsync(User(QuasarRoles.Admin),
            "quasar.core", "logs.server.read", new() { Enabled = false, RequiredPolicies = [QuasarPolicyNames.CanView] });
        using var closed = await client.GetAsync(logs);
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        // No Nebula connector, manager or HTTP dependency is registered in this fixture.
        using var unchanged = await client.GetAsync(Prefix + "/_quasar/plugins/legacy/api/ping");
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal("legacy", await unchanged.Content.ReadFromJsonAsync<string>());
    }

    private sealed class LegacyPlugin : IQuasarPlugin
    {
        public string Id => "legacy";
        public string DisplayName => "Legacy";
        public IEnumerable<System.Reflection.Assembly> GetRazorAssemblies() => [];
        public IEnumerable<Quasar.Plugin.Abstractions.Navigation.QuasarNavItem> GetNavItems() => [];
        public IEnumerable<Quasar.Plugin.Abstractions.Extensions.QuasarExtensionContribution> GetExtensions() => [];
        public void ConfigureServices(IServiceCollection services, QuasarPluginContext context) { }
        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) =>
            endpoints.MapGet("/_quasar/plugins/legacy/api/ping", () => Microsoft.AspNetCore.Http.Results.Json("legacy"))
                .RequireAuthorization(QuasarPolicyNames.CanView);
    }
}
