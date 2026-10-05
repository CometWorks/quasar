using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Components.Shared;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ClusterCreateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-create-" + Guid.NewGuid());
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;

    public ClusterCreateTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InlineEnrollmentWaitsForConnectionAndPreservesClusterSettings()
    {
        await using var context = CreateContext();
        var page = context.Render<ClusterCreate>();
        Assert.Equal(0, page.FindComponent<MudStepper>().Instance.GetState(c => c.ActiveIndex));
        Assert.True(Button(page, "Continue to cluster configuration").Instance.Disabled);
        Assert.True(page.FindComponents<MudStep>()[1].Instance.GetState(c => c.Disabled));
        var enrollment = page.FindComponent<HostEnrollment>();
        var methods = enrollment.FindComponents<MudSelect<string>>().Single(c => c.Instance.Label == "Installation method");
        await page.InvokeAsync(() => methods.Instance.ValueChanged.InvokeAsync("command"));
        await Button(page, "Generate enrollment command").Find("button").ClickAsync();
        Assert.Contains("Enrollment command", page.Markup);
        var host = Assert.Single(context.Services.GetRequiredService<ClusterHostCatalog>().GetAll());
        Assert.Matches("^host-machine-[a-f0-9]{12}$", host.Name);
        Assert.Equal(host.Name, host.Id);
        Assert.True(Button(page, "Continue to cluster configuration").Instance.Disabled);

        // Exercise the authenticated enrollment channel without starting the Quasar worker.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(context.Services.GetRequiredService<ClusterHostCatalog>());
        builder.Services.AddSingleton(context.Services.GetRequiredService<ClusterHostTunnels>());
        builder.Services.AddSingleton(context.Services.GetRequiredService<ClusterHostInstaller>());
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + context.Services.GetRequiredService<ClusterCredentialStore>().Resolve(host.CredentialReference));
        await control.ConnectAsync(new UriBuilder(new Uri(new Uri(app.Urls.Single()), $"/api/v1/hosts/{host.Id}/connect")) { Scheme = "ws" }.Uri, default);
        await page.WaitForAssertionAsync(() => Assert.False(Button(page, "Continue to cluster configuration").Instance.Disabled), TimeSpan.FromSeconds(6));
        Assert.True(page.FindComponents<MudExpansionPanel>().Single(c => c.Instance.Text == "Add host machine").Instance.GetState(c => c.Expanded));
        await Button(page, "Continue to cluster configuration").Find("button").ClickAsync();
        Assert.Equal(1, page.FindComponent<MudStepper>().Instance.GetState(c => c.ActiveIndex));
        Assert.True(Button(page, "Create and deploy cluster").Instance.Disabled);
        var id = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Cluster ID" && c.Instance.HelperText?.Contains("Removed IDs") == true);
        await page.InvokeAsync(() => id.Instance.ValueChanged.InvokeAsync("my-cluster"));
        await Button(page, "Back to host enrollment").Find("button").ClickAsync();
        Assert.Same(enrollment.Instance, page.FindComponent<HostEnrollment>().Instance);
        Assert.Contains("Enrollment command", page.Markup);
        await Button(page, "Continue to cluster configuration").Find("button").ClickAsync();
        Assert.Equal("my-cluster", page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Cluster ID" && c.Instance.HelperText?.Contains("Removed IDs") == true).Instance.GetState(c => c.Value));
        Assert.Equal("http://localhost/clusters/new", context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
        await control.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnrollmentRequiresSecurityPermissionAndStandalonePageUsesSameForm()
    {
        await using var context = CreateContext(securityAdmin: false);
        var page = context.Render<ClusterCreate>();
        Assert.Empty(page.FindComponents<HostEnrollment>());
        Assert.Contains("A security administrator must enroll host machines", page.Markup);

        context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies(QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanManageSecurity);
        var standalone = context.Render<HostEnroll>();
        Assert.Single(standalone.FindComponents<HostEnrollment>());
        Assert.Contains("Add host machine", standalone.Markup);
    }

    [Fact]
    public async Task ResumeOpensBoundConfigurationEvenWhenHostIsOffline()
    {
        await using var context = CreateContext();
        var hosts = context.Services.GetRequiredService<ClusterHostCatalog>();
        await hosts.RegisterAsync("host-1", "One", "127.0.0.1", 18400, default);
        var request = new ClusterSetupRequest("resume", "Resume", "world", "profile", "host-1", 28000, [new("host-1", 2)]);
        string work = Path.Combine(_root, "ClusterSetup/resume");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "status.json"), JsonSerializer.Serialize(new ClusterSetupStatus(request, "Setup interrupted", "Offline", DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/clusters/new?resume=resume");
        var page = context.Render<ClusterCreate>();
        Assert.Equal(1, page.FindComponent<MudStepper>().Instance.GetState(c => c.ActiveIndex));
        Assert.True(Button(page, "Resume setup").Instance.Disabled);
        Assert.True(page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Cluster ID" && c.Instance.HelperText?.Contains("Removed IDs") == true).Instance.Disabled);
        await Button(page, "Back to host enrollment").Find("button").ClickAsync();
        Assert.Single(page.FindComponents<HostEnrollment>());
    }

    [Fact]
    public async Task BlankNamesRefillAndAreSavedWithoutChangingChosenIds()
    {
        await using var context = CreateContext();
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/clusters/new?register=true");
        var page = context.Render<ClusterCreate>();
        Assert.Equal(1, page.FindComponent<MudTabs>().Instance.GetState(c => c.ActivePanelIndex));
        var registration = page.FindComponents<MudTabPanel>().Single(c => c.Instance.Text == "Register existing cluster (advanced)");
        var name = registration.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server name");
        Assert.Matches("^cluster-[a-f0-9]{12}$", name.Instance.GetState(c => c.Value));
        var tabs = page.FindComponent<MudTabs>();
        await page.InvokeAsync(() => tabs.Instance.ActivatePanelAsync(0));
        await page.InvokeAsync(() => registration.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Cluster ID").Instance.ValueChanged.InvokeAsync("chosen-id"));
        Assert.Equal(0, tabs.Instance.GetState(c => c.ActivePanelIndex));
        await page.InvokeAsync(() => tabs.Instance.ActivatePanelAsync(1));
        await page.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync(""));
        await page.InvokeAsync(() => name.Instance.OnBlur.InvokeAsync(new()));
        string suggested = name.Instance.GetState(c => c.Value)!;
        Assert.Matches("^cluster-[a-f0-9]{12}$", suggested);
        await page.InvokeAsync(() => registration.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Cluster Registry admin URL").Instance.ValueChanged.InvokeAsync("http://10.1.0.1:28016"));
        await page.InvokeAsync(() => registration.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Admin token environment variable name").Instance.ValueChanged.InvokeAsync("ADMIN"));
        await Button(page, "Register existing cluster").Find("button").ClickAsync();
        Assert.Equal(suggested, context.Services.GetRequiredService<ClusterCatalog>().GetCluster("chosen-id")!.DisplayName);
    }

    [Fact]
    public async Task ClearingHostMachineNameSavesGeneratedSuggestionAndKeepsId()
    {
        await using var context = CreateContext();
        var page = context.Render<ClusterCreate>();
        var enrollment = page.FindComponent<HostEnrollment>();
        var fields = enrollment.FindComponents<MudTextField<string>>();
        var name = fields.Single(c => c.Instance.Label == "Host machine name");
        await page.InvokeAsync(() => fields.Single(c => c.Instance.Label == "Host machine ID").Instance.ValueChanged.InvokeAsync("chosen-host"));
        await page.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync(""));
        await page.InvokeAsync(() => name.Instance.OnBlur.InvokeAsync(new()));
        string suggested = name.Instance.GetState(c => c.Value)!;
        Assert.Matches("^host-machine-[a-f0-9]{12}$", suggested);
        await page.InvokeAsync(() => enrollment.FindComponents<MudSelect<string>>().Single(c => c.Instance.Label == "Installation method").Instance.ValueChanged.InvokeAsync("command"));
        await Button(page, "Generate enrollment command").Find("button").ClickAsync();
        Assert.Equal(suggested, context.Services.GetRequiredService<ClusterHostCatalog>().Get("chosen-host")!.Name);
    }

    [Fact]
    public async Task StandaloneServerNamesAreSuggestedAndExplicitIdsStayFixed()
    {
        await using var context = CreateContext();
        var provider = context.Render<MudDialogProvider>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<ServerEditorDialog>("Create Server"));
        var page = provider.FindComponent<ServerEditorDialog>();
        var name = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server name");
        var id = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server ID");
        Assert.Matches("^server-[a-f0-9]{12}$", name.Instance.GetState(c => c.Value));
        Assert.Equal(name.Instance.GetState(c => c.Value), id.Instance.GetState(c => c.Value));
        await page.InvokeAsync(() => id.Instance.ValueChanged.InvokeAsync("chosen-server"));
        await page.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync(""));
        await page.InvokeAsync(() => name.Instance.OnBlur.InvokeAsync(new()));
        Assert.Matches("^server-[a-f0-9]{12}$", name.Instance.GetState(c => c.Value));
        Assert.Equal("chosen-server", id.Instance.GetState(c => c.Value));
        await provider.InvokeAsync(() => dialogs.ShowAsync<ServerEditorDialog>("Edit Server", new DialogParameters
        {
            [nameof(ServerEditorDialog.IsEditing)] = true,
            [nameof(ServerEditorDialog.Definition)] = new DedicatedServerDefinition { UniqueName = "existing", DisplayName = "Existing name" },
        }));
        var edit = provider.FindComponents<ServerEditorDialog>().Single(c => c.Instance.IsEditing);
        Assert.Equal("Existing name", edit.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server name").Instance.GetState(c => c.Value));
        Assert.Equal("existing", edit.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server ID").Instance.GetState(c => c.Value));
    }

    [Fact]
    public async Task ConversionKeepsSourceNameUntilClearedAndPreservesDestinationId()
    {
        await using var context = CreateContext();
        await context.Services.GetRequiredService<ClusterCatalog>().CreateAsync(new("source", "Source server", "http://10.1.0.1:28016", "ADMIN"), default);
        var page = context.Render<ClusterConversion>(p => p.Add(c => c.ClusterName, "source"));
        var name = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server name");
        var id = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server ID");
        Assert.Equal("Source server", name.Instance.GetState(c => c.Value));
        Assert.Equal("source-standalone", id.Instance.GetState(c => c.Value));
        await page.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync(""));
        await page.InvokeAsync(() => name.Instance.OnBlur.InvokeAsync(new()));
        Assert.Matches("^server-[a-f0-9]{12}$", name.Instance.GetState(c => c.Value));
        Assert.Equal("source-standalone", id.Instance.GetState(c => c.Value));
    }

    private BunitContext CreateContext(bool securityAdmin = true)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetPolicies(securityAdmin
            ? [QuasarPolicyNames.ClusterManage, QuasarPolicyNames.CanEditConfigs, QuasarPolicyNames.CanManageSecurity]
            : [QuasarPolicyNames.ClusterManage]);
        context.Services.AddSingleton(auth);
        var credentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(_root, "credentials.json"));
        var hosts = new ClusterHostCatalog(credentials, Path.Combine(_root, "Hosts"));
        string binary = Path.Combine(_root, "Quasar.Host");
        File.WriteAllText(binary, "test Host binary");
        var installer = new ClusterHostInstaller(hosts, credentials, binary, TimeProvider.System);
        var tunnels = new ClusterHostTunnels();
        context.Services.AddSingleton(credentials);
        context.Services.AddSingleton(hosts);
        context.Services.AddSingleton(installer);
        context.Services.AddSingleton(tunnels);
        context.Services.AddSingleton(new ClusterLocalHostUpdater(hosts, installer, tunnels, new WebServiceOptions(), NullLogger<ClusterLocalHostUpdater>.Instance));
        context.Services.AddSingleton(new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build()));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new QuasarAuthOptions(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        context.Services.AddSingleton(new ClusterSetupService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));
        context.Services.AddSingleton(new ClusterConversionService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new WebServiceOptions()));
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddSingleton<QuasarPermissionService>();
        context.ComponentFactories.AddStub<ClusterBetaNotice>();
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/clusters/new");
        context.Render<MudPopoverProvider>();
        return context;
    }

    private static IRenderedComponent<MudButton> Button(IRenderedComponent<ClusterCreate> page, string text) =>
        page.FindComponents<MudButton>().Single(c => c.Markup.Contains(text, StringComparison.Ordinal));

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
