using System.Net.WebSockets;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
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
using Quasar.Services.Backup;
using Quasar.Services.PluginSdk;
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
    public async Task DefaultPluginsAreOnForANewClusterAndNameWhatTheProfileLacks()
    {
        await using var context = CreateContext();
        var dialogs = context.Render<MudDialogProvider>();
        await context.Services.GetRequiredService<QuasarConfigProfileCatalog>().UpsertAsync(new() { ConfigProfileId = "half", Name = "Half",
            Plugins = [new() { PluginId = QuasarDefaultPlugins.BugfixesPluginId, DisplayName = "Bugfixes" }] });
        var page = context.Render<ClusterCreate>();
        var option = page.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label?.Contains("Bugfixes and Performance") == true);
        Assert.True(option.Instance.GetState(c => c.Value));
        await page.InvokeAsync(() => page.FindComponents<MudSelect<string>>().Single(c => c.Instance.Label == "Configuration profile").Instance.ValueChanged.InvokeAsync("half"));
        Assert.Contains("Adds Performance from MagnetarHub", page.Markup);
        Assert.Contains("Havok physics crash", page.Markup);
        Assert.Contains("unsaved progress may be lost", page.Markup);
        var disabling = option.Find("input").ChangeAsync(new ChangeEventArgs { Value = false });
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Skip recommended server plugins?", dialogs.Markup));
        Assert.Contains("Performance will not be added", dialogs.Markup);
        await dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Keep enabled").ClickAsync();
        await disabling;
        Assert.True(option.Instance.GetState(c => c.Value));
        Assert.True(option.Find("input").HasAttribute("checked"));

        disabling = option.Find("input").ChangeAsync(new ChangeEventArgs { Value = false });
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Skip recommended server plugins?", dialogs.Markup));
        await dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Skip recommended plugins").ClickAsync();
        await disabling;
        Assert.False(option.Instance.GetState(c => c.Value));
        Assert.Contains("The cluster gets only the plugins the selected profile has.", page.Markup);
        Assert.Contains("Existing profile plugins stay selected", page.Markup);
        Assert.Single(context.Services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfile("half")!.Plugins);
        await option.Find("input").ChangeAsync(new ChangeEventArgs { Value = true });
        Assert.True(option.Instance.GetState(c => c.Value));
    }

    [Fact]
    public async Task SkippingDefaultAdditionsDoesNotWarnWhenBothPluginsAreAlreadySelected()
    {
        await using var context = CreateContext();
        var dialogs = context.Render<MudDialogProvider>();
        var profile = new QuasarConfigProfile { ConfigProfileId = "full", Name = "Full" };
        QuasarDefaultPlugins.Apply(profile, true);
        await context.Services.GetRequiredService<QuasarConfigProfileCatalog>().UpsertAsync(profile);
        var page = context.Render<ClusterCreate>();
        await page.InvokeAsync(() => page.FindComponents<MudSelect<string>>().Single(c => c.Instance.Label == "Configuration profile").Instance.ValueChanged.InvokeAsync("full"));
        var option = page.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label?.Contains("Bugfixes and Performance") == true);
        await option.Find("input").ChangeAsync(new ChangeEventArgs { Value = false });
        Assert.False(option.Instance.GetState(c => c.Value));
        Assert.DoesNotContain("Skip recommended server plugins?", dialogs.Markup);
        Assert.Equal(2, context.Services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfile("full")!.Plugins.Count);
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
        File.WriteAllText(Path.Combine(work, "status.json"), JsonSerializer.Serialize(new ClusterSetupStatus(request, "Setup interrupted",
            "Changed: Package/cli/cluster.py\n  Expected: SHA-256 approved\n  Actual: SHA-256 changed", DateTimeOffset.UtcNow,
            "Verifying the pinned deployment installation"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/clusters/new?resume=resume");
        var page = context.Render<ClusterCreate>();
        Assert.Equal(1, page.FindComponent<MudStepper>().Instance.GetState(c => c.ActiveIndex));
        Assert.True(Button(page, "Resume setup").Instance.Disabled);
        Assert.Contains("Setup interrupted during: Verifying the pinned deployment installation", page.Markup);
        Assert.Contains("Changed: Package/cli/cluster.py", page.Markup);
        Assert.Contains("Expected: SHA-256 approved", page.Markup);
        Assert.Contains("Keep this cluster", page.Markup);
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
    public async Task StandaloneConversionHasOnlyDestinationChoiceAndOneAction()
    {
        await using var context = CreateContext();
        var clusters = context.Services.GetRequiredService<ClusterCatalog>();
        await clusters.CreateAsync(new("empty", "Empty", "http://10.1.0.1:28016", "ADMIN"), default);
        await clusters.CreateAsync(new("busy", "Busy", "http://10.1.0.2:28016", "ADMIN"), default);
        await clusters.SetGoalStateAsync("busy", DedicatedServerGoalState.On);
        var page = context.Render<ClusterConversion>(p => p.Add(c => c.ServerName, "source"));
        Assert.Equal("Destination cluster", Assert.Single(page.FindComponents<MudSelect<string>>()).Instance.Label);
        var destinations = page.FindComponents<MudSelectItem<string>>();
        Assert.Contains(destinations, d => d.Instance.Value == "");
        Assert.Contains(destinations, d => d.Instance.Value == "empty");
        Assert.DoesNotContain(destinations, d => d.Instance.Value == "busy");
        Assert.Empty(page.FindComponents<MudTextField<string>>());
        Assert.Empty(page.FindComponents<MudNumericField<int>>());
        Assert.Empty(page.FindComponents<MudCheckBox<bool>>());
        Assert.Equal("Convert to cluster", Assert.Single(page.FindComponents<MudButton>()).Find("button").TextContent.Trim());
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

    [Theory]
    [InlineData(false, "Complete — destination remains stopped", "/")]
    [InlineData(true, "Complete — destination remains stopped", "/clusters/destination")]
    [InlineData(false, "Failed", null)]
    [InlineData(true, "Failed", null)]
    [InlineData(false, "Copying world", null)]
    [InlineData(true, "Copying world", null)]
    public async Task SavedConversionOffersResumeOnlyWhileUnfinished(bool toCluster, string phase, string? destination)
    {
        await using var context = CreateContext();
        var id = Guid.NewGuid();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string cluster = toCluster ? "destination" : "source";
        object request = toCluster
            ? new ServerToClusterRequest(id, "source", "revision", "1.208", "host", 27016, "JOIN", "TOKENS", [], [])
            : new ClusterToServerRequest(id, "destination", "Destination", 27017, "profile", "lifecycle");
        string work = Path.Combine(_root, "backups", "Conversions", id.ToString("N"));
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "request.json"), JsonSerializer.Serialize(new
        {
            cluster, direction = toCluster ? "to-cluster" : "to-server", request
        }, json));
        await File.WriteAllTextAsync(Path.Combine(work, "progress.json"), JsonSerializer.Serialize(
            new ClusterConversionStatus(id, cluster, toCluster ? "to-cluster" : "to-server", phase,
                destination, phase == "Failed" ? "Interrupted" : null, DateTimeOffset.UtcNow), json));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/{(toCluster ? "servers" : "clusters")}/source/{(toCluster ? "convert-to-cluster" : "convert-to-server")}?conversion={id}");
        var page = context.Render<ClusterConversion>(p => p
            .Add(c => c.ServerName, toCluster ? "source" : null)
            .Add(c => c.ClusterName, toCluster ? null : "source"));

        Assert.Contains(id.ToString(), page.Markup);
        Assert.Contains(phase, page.Markup);
        var buttons = page.FindComponents<MudButton>();
        Assert.Equal(destination is null, buttons.Any(b => b.Markup.Contains("Resume conversion")));
        Assert.DoesNotContain(buttons, b => b.Markup.Contains("Back up and convert"));
        Assert.Contains(buttons, b => b.Markup.Contains("Edit as a new conversion"));
        Assert.Equal(destination is not null, buttons.Any(b => b.Instance.Href == destination && b.Markup.Contains("Review destination")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionShowsProgressDuringPermissionChecksAndReportsFailureAtTheAction(bool permitted)
    {
        var authorization = new DelayedConversionAuthorization();
        await using var context = CreateContext(conversionAuthorization: authorization);
        var page = context.Render<ClusterConversion>(p => p.Add(c => c.ServerName, "source"));
        Assert.Empty(page.FindComponents<MudCheckBox<bool>>());
        var button = page.FindComponents<MudButton>().Single(c => c.Markup.Contains("Convert to cluster"));
        var queuedClick = button.Instance.OnClick;
        var clicking = button.Find("button").ClickAsync();

        await page.WaitForAssertionAsync(() => Assert.Single(page.FindComponents<QuasarOperationLoader>()));
        Assert.Contains("Validating the source and destination before backup.", page.Find("[role=status]").TextContent);
        Assert.Equal("true", page.Find("[aria-busy]").GetAttribute("aria-busy"));
        Assert.DoesNotContain(page.FindComponents<MudButton>(), b => b.Markup.Contains("Convert to cluster"));
        Assert.DoesNotContain(page.FindComponents<MudAlert>(), a => a.Instance.Severity == Severity.Warning);
        Assert.False(clicking.IsCompleted);

        // A queued second click must not enter authorization or start another attempt.
        await page.InvokeAsync(() => queuedClick.InvokeAsync(new()));
        Assert.Equal(1, authorization.Checks);
        authorization.Result.SetResult(permitted ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        await clicking;

        Assert.Empty(page.FindComponents<QuasarOperationLoader>());
        Assert.Contains(permitted ? "Source server was not found" : "You do not have permission",
            page.Find("[role=alert]").TextContent);
        Assert.False(page.FindComponents<MudButton>().Single(b => b.Markup.Contains(permitted ? "Resume conversion" : "Convert to cluster")).Instance.Disabled);
        string work = Path.Combine(_root, "backups", "Conversions");
        Assert.Equal(permitted, Directory.Exists(work));
        if (permitted)
        {
            string requestFile = Assert.Single(Directory.GetFiles(work, "request.json", SearchOption.AllDirectories));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(requestFile));
            var request = saved.RootElement.GetProperty("request");
            Assert.True(request.GetProperty("automatic").GetBoolean());
            Assert.True(request.GetProperty("createCluster").GetBoolean());
            Assert.Equal("cluster-" + request.GetProperty("id").GetGuid().ToString("N"),
                saved.RootElement.GetProperty("cluster").GetString());
        }
    }

    private sealed class DelayedConversionAuthorization : IAuthorizationService
    {
        public TaskCompletionSource<AuthorizationResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Checks { get; private set; }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Checks++;
            return Result.Task;
        }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private BunitContext CreateContext(bool securityAdmin = true, IAuthorizationService? conversionAuthorization = null)
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
        if (conversionAuthorization is not null)
            context.Services.AddSingleton(conversionAuthorization);
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
        context.Services.AddSingleton(new ClusterHostClient(new HttpClient(), credentials));
        context.Services.AddSingleton(new ClusterLocalHost(hosts, credentials, installer, tunnels, new WebServiceOptions(), NullLogger<ClusterLocalHost>.Instance, Path.Combine(_root, "LocalHost"), supported: false));
        context.Services.AddSingleton(new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build()));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorkshopModResolver(new QuasarAuthOptions(), null!, null!, NullLogger<QuasarWorkshopModResolver>.Instance));
        context.Services.AddSingleton(new ClusterSetupService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));
        context.Services.AddSingleton(sp => new ClusterConversionService(sp.GetRequiredService<ClusterCatalog>(),
            sp.GetRequiredService<DedicatedServerCatalog>(), null!, new ServerRestoreCoordinator(),
            sp.GetRequiredService<QuasarConfigProfileCatalog>(),
            new PluginConfigService(null!, NullLogger<PluginConfigService>.Instance, Path.Combine(_root, "configs")),
            null!, null!, null!, null!, null!, new ClusterOperationStore(Path.Combine(_root, "operations")),
            new WebServiceOptions { BackupDirectory = Path.Combine(_root, "backups") }, null!));
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
