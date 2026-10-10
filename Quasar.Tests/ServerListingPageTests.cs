using System.Reflection;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Extensions;
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
        var names = page.FindComponents<MudTextField<string>>().Where(c => c.Instance.Label == "Public server name").ToArray();
        Assert.Contains(names, c => c.Instance.GetState(x => x.Value) == "Public solo");
        Assert.Contains(names, c => c.Instance.GetState(x => x.Value) == "Cluster fleet");
        var ports = page.FindComponents<MudNumericField<int>>();
        Assert.Contains(ports, c => c.Instance.GetState(x => x.Value) == 27116);
        Assert.Contains(ports, c => c.Instance.GetState(x => x.Value) == 28000);

        var addresses = page.FindComponents<MudTextField<string>>().Where(c => c.Instance.Label == "Public hostname or IP address").ToArray();
        var switches = page.FindComponents<MudSwitch<bool>>().Where(c => c.Instance.Label == "Publish this server").ToArray();
        for (int i = 0; i < addresses.Length; i++)
        {
            await page.InvokeAsync(() => addresses[i].Instance.ValueChanged.InvokeAsync("play.example.org"));
            await page.InvokeAsync(() => switches[i].Instance.ValueChanged.InvokeAsync(true));
        }
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Save listings").ClickAsync();
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
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Save listings").ClickAsync();
        Assert.False(File.Exists(settings.SettingsPath));
        Assert.Equal(0, settings.GetSnapshot().Revision);
        var visibility = page.FindComponents<MudSwitch<bool>>().Single(c => c.Instance.Label == "Show configured servers on se-servers.net");
        await page.InvokeAsync(() => visibility.Instance.ValueChanged.InvokeAsync(true));
        Assert.False(context.Services.GetRequiredService<DataHandlingConsentCatalog>().GetSettings().ListingGranted);
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
