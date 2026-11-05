using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Shared;
using Quasar.Hosting;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class AccessSetupDialogTests : IDisposable
{
    private const string SteamId = "76561198000000000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "access-setup-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;

    public AccessSetupDialogTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
    }

    private BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("Trusted network");
        auth.SetPolicies(QuasarPolicyNames.CanManageSecurity);
        context.Services.AddSingleton(auth);
        context.Services.AddSingleton(new QuasarAuthOptions());
        context.Services.AddSingleton(new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance));
        context.Services.AddSingleton<QuasarRoleMapper>();
        context.Services.AddSingleton<QuasarPermissionService>();
        context.Services.AddHttpClient();
        context.Services.AddSingleton<HostedPanelService>();
        return context;
    }

    private static async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(BunitContext context)
    {
        var provider = context.Render<MudDialogProvider>();
        await provider.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<AccessSetupDialog>("Set up administrator access"));
        return provider;
    }

    [Fact]
    public async Task MissingAdministratorIsSavedBeforeAccessChoicesAndSurvivesReload()
    {
        await using var context = Context();
        var rbac = context.Services.GetRequiredService<RbacConfigCatalog>();
        await rbac.SaveAsync(new RbacConfig
        {
            SubjectRoleMappings = [new() { Subject = SteamId, Roles = [QuasarRoles.Viewer] }],
            PolicyOverrides = new() { [QuasarPolicyNames.CanView] = [QuasarRoles.Viewer] },
        });
        var provider = await OpenAsync(context);
        Assert.Contains("Set the first Quasar administrator", provider.Markup);
        Assert.DoesNotContain("Use hosted panel", provider.Markup);
        provider.Find("input").Input(" " + SteamId + " ");
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Save administrator")).ClickAsync();
        Assert.Contains("Use hosted panel", provider.Markup);
        Assert.DoesNotContain("Set the first Quasar administrator", provider.Markup);
        using var reloaded = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        Assert.Equal([QuasarRoles.Admin, QuasarRoles.Viewer], reloaded.GetSubjectRoles(QuasarAuthSchemes.Steam, SteamId));
        Assert.Contains(QuasarPolicyNames.CanView, reloaded.GetConfig().PolicyOverrides.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSubjectOrClaimAdministratorSkipsTheStep(bool claimMapping)
    {
        await using var context = Context();
        var rbac = context.Services.GetRequiredService<RbacConfigCatalog>();
        await rbac.SaveAsync(claimMapping
            ? new RbacConfig { ClaimRoleMappings = [new() { Claim = "groups", Value = "operators", Roles = [QuasarRoles.Admin] }] }
            : RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var provider = await OpenAsync(context);
        Assert.DoesNotContain("Set the first Quasar administrator", provider.Markup);
        Assert.Contains("Use hosted panel", provider.Markup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rbac.AddFirstSteamAdminAsync("76561198000000001"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7656119800000000")]
    [InlineData("7656119800000000x")]
    [InlineData("７６５６１１９８０００００００００")]
    public async Task InvalidSteamIdCannotBeSaved(string? steamId)
    {
        await using var context = Context();
        var provider = await OpenAsync(context);
        provider.Find("input").Input(steamId);
        Assert.True(provider.FindAll("button").Single(b => b.TextContent.Contains("Save administrator")).HasAttribute("disabled"));
        var rbac = context.Services.GetRequiredService<RbacConfigCatalog>();
        await Assert.ThrowsAsync<ArgumentException>(() => rbac.AddFirstSteamAdminAsync(steamId));
        Assert.False(RbacConfigCatalog.HasAdminMapping(rbac.GetConfig()));
    }

    [Fact]
    public async Task SavingRechecksSecurityPermission()
    {
        await using var context = Context();
        var provider = await OpenAsync(context);
        provider.Find("input").Input(SteamId);
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies();
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Save administrator")).ClickAsync();
        Assert.Contains("Administrator permission is required", provider.Markup);
        Assert.False(RbacConfigCatalog.HasAdminMapping(context.Services.GetRequiredService<RbacConfigCatalog>().GetConfig()));
    }

    [Fact]
    public async Task SimultaneousSetupCannotReplaceTheFirstAdministrator()
    {
        await using var context = Context();
        var rbac = context.Services.GetRequiredService<RbacConfigCatalog>();
        var first = rbac.AddFirstSteamAdminAsync(SteamId);
        var second = rbac.AddFirstSteamAdminAsync("76561198000000001");
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(SteamId, Assert.Single(rbac.GetConfig().SubjectRoleMappings).Subject);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> HostedDialogAsync(BunitContext context, EnrollmentHandler handler)
    {
        context.Services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => handler);
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var provider = await OpenAsync(context);
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Use hosted panel")).ClickAsync();
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Next")).ClickAsync();
        provider.Find("input").Input("test-panel");
        return provider;
    }

    [Fact]
    public async Task AccessChoicesRequireNextAndBackPreservesTheHostedName()
    {
        await using var context = Context();
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var provider = await OpenAsync(context);
        Assert.Contains("Step 1 of 3", provider.Markup);
        Assert.True(provider.FindAll("button").Single(b => b.TextContent.Contains("Next")).HasAttribute("disabled"));
        var hosted = provider.FindAll("button").Single(b => b.TextContent.Contains("Use hosted panel"));
        Assert.Contains("mud-button-outlined", hosted.ClassList);
        await hosted.ClickAsync();
        Assert.Equal("true", provider.FindAll("button").Single(b => b.TextContent.Contains("Use hosted panel")).GetAttribute("aria-pressed"));
        Assert.False(File.Exists(HostedPanelState.StatePath));
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Next")).ClickAsync();
        Assert.Contains("Step 2 of 3", provider.Markup);
        Assert.DoesNotContain("Self-deploy", provider.Markup);
        provider.Find("input").Input("my-panel");
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Back")).ClickAsync();
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Next")).ClickAsync();
        Assert.Equal("my-panel", provider.Find("input").GetAttribute("value"));
        Assert.False(File.Exists(HostedPanelState.StatePath));
    }

    [Fact]
    public async Task SelfDeploymentIsAppliedOnlyAfterNext()
    {
        await using var context = Context();
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var provider = await OpenAsync(context);
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Self-deploy")).ClickAsync();
        Assert.False(File.Exists(HostedPanelState.StatePath));
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Next")).ClickAsync();
        Assert.Equal("self", HostedPanelState.Read()!.Choice);
        Assert.EndsWith("/settings/security", context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public async Task StartingShowsLoaderAndOnlyCancelWhichStopsThePendingRequest()
    {
        await using var context = Context();
        var handler = new EnrollmentHandler { HoldCreation = true };
        var provider = await HostedDialogAsync(context, handler);
        var starting = provider.FindAll("button").Single(b => b.TextContent.Contains("Start hosted enrollment")).ClickAsync();
        await provider.WaitForAssertionAsync(() => Assert.Contains("Creating your enrollment", provider.Markup));
        Assert.False(provider.Find("progress").HasAttribute("value"));
        Assert.Equal("Cancel", Assert.Single(provider.FindAll(".access-setup-actions button")).TextContent.Trim());
        await provider.Find(".access-setup-actions button").ClickAsync();
        await starting;
        Assert.True(handler.CreationCanceled);
        Assert.Equal("self", HostedPanelState.Read()!.Choice);
    }

    [Fact]
    public async Task LiveUpdatesWaitForMatchingTunnelAndWorkerBeforeShowingDashboardButton()
    {
        await using var context = Context();
        var handler = new EnrollmentHandler();
        var provider = await HostedDialogAsync(context, handler);
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Start hosted enrollment")).ClickAsync();
        Assert.Contains("Continue enrollment with Steam", provider.Markup);
        Assert.DoesNotContain("Self-deploy", provider.Markup);
        Assert.DoesNotContain("Open Quasar dashboard", provider.Markup);
        Assert.Equal("Cancel", Assert.Single(provider.FindAll(".access-setup-actions button")).TextContent.Trim());

        handler.Active = true;
        var service = context.Services.GetRequiredService<HostedPanelService>();
        await new HostedConnectorHealth("other-panel", true, true, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(default);
        await service.RefreshAsync();
        await provider.WaitForAssertionAsync(() => Assert.Contains("Bootstrap is connecting", provider.Markup));
        Assert.DoesNotContain("Open Quasar dashboard", provider.Markup);
        Assert.DoesNotContain("Continue enrollment with Steam", provider.Markup);

        await new HostedConnectorHealth("test-panel", true, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(default);
        await service.RefreshAsync();
        await provider.WaitForAssertionAsync(() => Assert.Contains("local Quasar is restarting", provider.Markup));
        Assert.DoesNotContain("Open Quasar dashboard", provider.Markup);

        await new HostedConnectorHealth("test-panel", true, true, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()).SaveAsync(default);
        await service.RefreshAsync();
        Assert.False(service.IsReady);

        await new HostedConnectorHealth("test-panel", true, true, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(default);
        await service.RefreshAsync();
        await provider.WaitForAssertionAsync(() => Assert.Contains("Your hosted panel is ready", provider.Markup));
        var dashboard = provider.FindAll("a").Single(a => a.TextContent.Contains("Open Quasar dashboard"));
        Assert.Equal("https://dash.se-servers.net/test-panel/", dashboard.GetAttribute("href"));
        Assert.Null(dashboard.GetAttribute("target"));
        Assert.Contains("mud-button-root", dashboard.ClassList);
        Assert.Equal("1", provider.Find("progress").GetAttribute("value"));
        Assert.DoesNotContain("Self-deploy", provider.Markup);
        Assert.Contains("Manage Nebula hosting and billing", provider.Markup);

        await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Done").ClickAsync();
        Assert.Equal("hosted", HostedPanelState.Read()!.Choice);
        provider.Dispose();
        provider = await OpenAsync(context);
        Assert.Contains("Your hosted panel is ready", provider.Markup);
        Assert.DoesNotContain("Self-deploy", provider.Markup);

        // Retained credentials and fresh health cannot override inactive hosting.
        handler.Active = false;
        await service.RefreshAsync();
        await provider.WaitForAssertionAsync(() => Assert.DoesNotContain("Open Quasar dashboard", provider.Markup));
        Assert.False(service.IsReady);
        Assert.NotNull(HostedPanelState.Read()!.Credential);
    }

    [Fact]
    public async Task FailedCreationShowsAnErrorAndPermitsRetry()
    {
        await using var context = Context();
        var provider = await HostedDialogAsync(context, new EnrollmentHandler { FailCreation = true });
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Start hosted enrollment")).ClickAsync();
        Assert.Contains("Nebula could not start enrollment", provider.Markup);
        Assert.Contains("Start hosted enrollment", provider.Markup);
        Assert.Empty(provider.FindAll("progress"));
    }

    [Fact]
    public async Task CancelingAnExistingConnectionRetainsItsPrivateIdentity()
    {
        await using var context = Context();
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var state = new HostedPanelState { Choice = "hosted", PanelId = "test-panel", EnrollmentId = "existing", Credential = "test-panel." + new string('a', 64) };
        await HostedPanelState.SaveAsync(state);
        var provider = await OpenAsync(context);
        await provider.Find(".access-setup-actions button").ClickAsync();
        var canceled = HostedPanelState.Read()!;
        Assert.Equal("self", canceled.Choice);
        Assert.Equal(state.EnrollmentId, canceled.EnrollmentId);
        Assert.Equal(state.InstallationSecret, canceled.InstallationSecret);
        Assert.Equal(state.Credential, canceled.Credential);
    }

    private sealed class EnrollmentHandler : HttpMessageHandler
    {
        public bool HoldCreation, FailCreation, Active, CreationCanceled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/administration", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { notices = Array.Empty<object>() }) };
            if (request.Method == HttpMethod.Post)
            {
                if (HoldCreation)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { CreationCanceled = true; throw; }
                }
                return FailCreation ? new(HttpStatusCode.Conflict) : new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { enrollmentId = "enrollment", browserUrl = HostedPanelState.Origin + "/account?enrollment=test" }),
                };
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { panelId = "test-panel", expired = false, active = Active,
                    credential = Active ? "test-panel." + new string('a', 64) : null, ownerSteamId = SteamId }),
            };
        }
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
