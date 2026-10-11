using System.Reflection;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.ServerList;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ServerListingPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "listing-page-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;

    public ServerListingPageTests()
    {
        _previous = _cache.GetValue(null); _cache.SetValue(null, _root);
        var path = MagnetarPaths.GetQuasarServerDefinitionPath("solo");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new DedicatedServerDefinition { UniqueName = "solo",
            DisplayName = "Standalone", InGameServerName = "Public solo", WorldSaveName = "world", ServerPort = 27116 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private async Task<BunitContext> ContextAsync()
    {
        var context = new BunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized("admin"); auth.SetPolicies(QuasarPolicyNames.CanManageSecurity);
        context.Services.AddSingleton(auth);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:ClusterCatalogPath"] = Path.Combine(_root, "Clusters") }).Build();
        var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, configuration);
        await clusters.CreateAsync(new("fleet", "Cluster fleet", "http://private-admin:28016", "TEST_ADMIN"), default);
        context.Services.AddSingleton(clusters);
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        context.Services.AddSingleton(new ServerListSettingsCatalog(configuration, NullLogger<ServerListSettingsCatalog>.Instance));
        context.Services.AddSingleton(new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance));
        var rbac = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        context.Services.AddSingleton(rbac);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), rbac));
        context.Services.AddSingleton<QuasarPermissionService>();
        return context;
    }

    [Fact]
    public async Task DiscoversBothKindsAndSavesSelectionThroughThePage()
    {
        await using var context = await ContextAsync();
        var page = context.Render<ServerListing>();
        Assert.Contains("Standalone Magnetar server", page.Markup);
        Assert.Contains("Cluster · Steam gateway", page.Markup);
        Assert.Contains("Accept diagnostic data handling", page.Markup);
        Assert.Contains("Add a Steam identity", page.Markup);
        Assert.DoesNotContain("private-admin", page.Markup);
        var names = page.FindAll("input[name='public-name']");
        Assert.Contains(names, c => c.GetAttribute("value") == "Public solo");
        Assert.Contains(names, c => c.GetAttribute("value") == "Cluster fleet");
        var ports = page.FindAll("input[name='public-port']");
        Assert.Contains(ports, c => c.GetAttribute("value") == "27116");
        Assert.Contains(ports, c => c.GetAttribute("value") == "28000");
        for (int i = 0; i < names.Count; i++)
        {
            page.FindAll("input[name='public-host']")[i].Input("play.example.org");
            page.FindAll("input[name='publish']")[i].Change(true);
        }
        await page.Find("form").SubmitAsync();
        var saved = context.Services.GetRequiredService<ServerListSettingsCatalog>().GetSnapshot().Options;
        Assert.True(saved.Enabled); Assert.Equal(2, saved.Listings.Count);
        Assert.All(saved.Listings, e => { Assert.True(e.Enabled); Assert.Equal("play.example.org", e.PublicHost); });
        // Configuring destinations never grants diagnostic or publication consent.
        Assert.False(context.Services.GetRequiredService<DataHandlingConsentCatalog>().GetSettings().ListingGranted);
    }

    [Fact]
    public async Task SavingRechecksPermissionAfterThePageHasOpened()
    {
        await using var context = await ContextAsync();
        var page = context.Render<ServerListing>();
        var settings = context.Services.GetRequiredService<ServerListSettingsCatalog>();
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies();
        await page.Find("form").SubmitAsync();
        Assert.False(File.Exists(settings.SettingsPath));
        Assert.Equal(0, settings.GetSnapshot().Revision);
        page.Find("input[name='listing-visibility']").Change(true);
        Assert.False(page.Find("input[name='listing-visibility']").HasAttribute("checked"));
        Assert.False(context.Services.GetRequiredService<DataHandlingConsentCatalog>().GetSettings().ListingGranted);
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
