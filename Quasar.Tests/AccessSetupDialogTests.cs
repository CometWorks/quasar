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

    private BunitContext Context(ProfileLookupHandler? profiles = null)
    {
        var context = new QuasarBunitContext();
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
        context.Services.AddHttpClient<SteamProfileClient>()
            .ConfigurePrimaryHttpMessageHandler(() => profiles ?? new ProfileLookupHandler());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileUrlSetupSavesOnlyResolvedIdAndRechecksPermission(bool revokePermission)
    {
        var profiles = new ProfileLookupHandler();
        await using var context = Context(profiles);
        if (revokePermission)
            profiles.OnRequest = () => context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies();
        var provider = await OpenAsync(context);
        provider.Find("input").Input("https://steamcommunity.com/id/rex00991/");
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Save administrator")).ClickAsync();

        var mappings = context.Services.GetRequiredService<RbacConfigCatalog>().GetConfig().SubjectRoleMappings;
        if (revokePermission)
        {
            Assert.Empty(mappings);
            Assert.Contains("Administrator permission is required", provider.Markup);
        }
        else
            Assert.Equal(SteamId, Assert.Single(mappings).Subject);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SecurityResolvesProfileUrlBeforeSavingAndRetainsFailedInput(bool lookupFails, bool revokePermission)
    {
        var profiles = new ProfileLookupHandler { Fail = lookupFails };
        await using var context = Context(profiles);
        if (revokePermission)
            profiles.OnRequest = () => context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies();
        context.Services.AddSingleton<QuasarAuthSettingsService>();
        context.Services.AddSingleton(new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance));
        context.ComponentFactories.AddStub<QuasarSdkAuthorization>();
        context.Render<MudPopoverProvider>();
        var page = context.Render<Quasar.Components.Pages.Security>();
        var input = page.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "SteamID64 or profile URL").Find("input");
        input.Input("arbitrary data");
        Assert.True(page.Find("button[aria-label='Add role mapping']").HasAttribute("disabled"));
        input.Input("https://steamcommunity.com/id/rex00991/");
        await page.Find("button[aria-label='Add role mapping']").ClickAsync();

        var mappings = context.Services.GetRequiredService<RbacConfigCatalog>().GetConfig().SubjectRoleMappings;
        if (lookupFails || revokePermission)
        {
            Assert.Empty(mappings);
            Assert.Equal("https://steamcommunity.com/id/rex00991/", input.GetAttribute("value"));
            Assert.Contains(context.Services.GetRequiredService<ISnackbar>().ShownSnackbars,
                snackbar => snackbar.Message.Contains(lookupFails ? "Could not resolve the Steam profile" : "Access denied"));
        }
        else
        {
            Assert.Equal(SteamId, Assert.Single(mappings).Subject);
            Assert.Equal(string.Empty, input.GetAttribute("value"));
        }
    }

    [Fact]
    public async Task CatalogRejectsInvalidSteamSubjectsWithoutChangingSavedRoles()
    {
        await using var context = Context();
        var rbac = context.Services.GetRequiredService<RbacConfigCatalog>();
        await rbac.SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        await Assert.ThrowsAsync<ArgumentException>(() => rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings.Add(new() { Subject = "https://steamcommunity.com/id/rex00991/", Roles = [QuasarRoles.Viewer] });
            return Task.CompletedTask;
        }));
        using var reloaded = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        Assert.Equal(SteamId, Assert.Single(reloaded.GetConfig().SubjectRoleMappings).Subject);

        await rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings.Add(new() { Provider = "Oidc", Subject = "external-subject", Roles = [QuasarRoles.Viewer] });
            return Task.CompletedTask;
        });
        Assert.Equal([QuasarRoles.Viewer], rbac.GetSubjectRoles("Oidc", "external-subject"));
    }

    [Fact]
    public async Task LegacyInvalidAdministratorCanBeReplacedWithoutLockout()
    {
        var legacy = new RbacConfig
        {
            SubjectRoleMappings = [new() { Subject = "https://steamcommunity.com/id/rex00991/", Roles = [QuasarRoles.Admin] }],
        };
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "rbac.json"), System.Text.Json.JsonSerializer.Serialize(legacy));
        await using var context = Context();
        var rbac = context.Services.GetRequiredService<RbacConfigCatalog>();
        await rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings.Add(new() { Subject = SteamId, Roles = [QuasarRoles.Admin] });
            return Task.CompletedTask;
        });
        await rbac.UpdateAsync(config =>
        {
            config.SubjectRoleMappings.RemoveAll(mapping => mapping.Subject != SteamId);
            return Task.CompletedTask;
        });
        using var reloaded = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        Assert.Equal(SteamId, Assert.Single(reloaded.GetConfig().SubjectRoleMappings).Subject);
        Assert.Equal([QuasarRoles.Admin], reloaded.GetSubjectRoles(QuasarAuthSchemes.Steam, SteamId));
    }

    private sealed class ProfileLookupHandler : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public Action? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://steamcommunity.com/id/rex00991?xml=1", request.RequestUri?.AbsoluteUri);
            OnRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent($"<profile><steamID64>{SteamId}</steamID64></profile>"),
            });
        }
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
        Assert.True(HostedPanelState.Read()!.IsSetupComplete);
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
        Assert.Equal("dismissed", HostedPanelState.Read()!.Choice);
        Assert.False(HostedPanelState.Read()!.IsSetupComplete);
    }

    [Fact]
    public async Task LiveUpdatesWaitForMatchingTunnelAndWorkerBeforeShowingDashboardButton()
    {
        await using var context = Context();
        var handler = new EnrollmentHandler();
        var provider = await HostedDialogAsync(context, handler);
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Start hosted enrollment")).ClickAsync();
        Assert.Contains("Continue enrollment with Steam", provider.Markup);
        Assert.False(HostedPanelState.Read()!.IsSetupComplete);
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
        Assert.True(HostedPanelState.Read()!.IsSetupComplete);
        Assert.True(new HostedPanelService(context.Services.GetRequiredService<IHttpClientFactory>(), NullLogger<HostedPanelService>.Instance).State.IsSetupComplete);
        Assert.DoesNotContain("Self-deploy", provider.Markup);
        Assert.Contains("Manage Nebula hosting and billing", provider.Markup);

        await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Done").ClickAsync();
        Assert.Equal("hosted", HostedPanelState.Read()!.Choice);
        provider.Dispose();
        provider = await OpenAsync(context);
        Assert.Contains("Your hosted panel is ready", provider.Markup);
        Assert.DoesNotContain("Self-deploy", provider.Markup);

        // Revoke connector authority without losing a completed enrollment.
        var linked = HostedPanelState.Read()!;
        handler.Active = false;
        await service.RefreshAsync();
        await provider.WaitForAssertionAsync(() => Assert.DoesNotContain("Open Quasar dashboard", provider.Markup));
        Assert.False(service.IsReady);
        Assert.True(HostedPanelState.Read()!.IsSetupComplete);
        Assert.Null(HostedPanelState.Read()!.Credential);
        Assert.Equal(linked.EnrollmentId, HostedPanelState.Read()!.EnrollmentId);
        Assert.Equal(linked.InstallationSecret, HostedPanelState.Read()!.InstallationSecret);
        Assert.Contains("Hosted access inactive", service.Status);

        handler.Active = true;
        await service.RefreshAsync();
        Assert.Equal(linked.Credential, HostedPanelState.Read()!.Credential);
        Assert.False(service.IsReady);
        Assert.DoesNotContain("Open Quasar dashboard", provider.Markup);
        await new HostedConnectorHealth("test-panel", true, true, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(default);
        await service.RefreshAsync();
        await provider.WaitForAssertionAsync(() => Assert.Contains("Open Quasar dashboard", provider.Markup));
    }

    [Fact]
    public async Task FailedCreationShowsAnErrorAndPermitsRetry()
    {
        await using var context = Context();
        var handler = new EnrollmentHandler { FailCreation = true };
        var provider = await HostedDialogAsync(context, handler);
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Start hosted enrollment")).ClickAsync();
        Assert.Contains("Nebula could not start enrollment", provider.Markup);
        Assert.Contains("Start hosted enrollment", provider.Markup);
        Assert.Empty(provider.FindAll("progress"));
        var failed = HostedPanelState.Read()!;
        Assert.False(failed.IsSetupComplete);
        handler.FailCreation = false;
        await provider.FindAll("button").Single(b => b.TextContent.Contains("Start hosted enrollment")).ClickAsync();
        Assert.Contains("Continue enrollment with Steam", provider.Markup);
        Assert.Equal(failed.InstallationSecret, HostedPanelState.Read()!.InstallationSecret);
        Assert.Equal(failed.LocalProxySecret, HostedPanelState.Read()!.LocalProxySecret);
        Assert.False(HostedPanelState.Read()!.IsSetupComplete);
    }

    [Fact]
    public async Task CancelingAnExistingConnectionRetainsItsPrivateIdentity()
    {
        await using var context = Context();
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var state = new HostedPanelState { Choice = "hosted", SetupCompleted = false, PanelId = "test-panel", EnrollmentId = "existing", Credential = "test-panel." + new string('a', 64) };
        await HostedPanelState.SaveAsync(state);
        var provider = await OpenAsync(context);
        await provider.Find(".access-setup-actions button").ClickAsync();
        var canceled = HostedPanelState.Read()!;
        Assert.Equal("dismissed", canceled.Choice);
        Assert.Equal(state.EnrollmentId, canceled.EnrollmentId);
        Assert.Equal(state.InstallationSecret, canceled.InstallationSecret);
        Assert.Equal(state.LocalProxySecret, canceled.LocalProxySecret);
        Assert.Equal(state.Credential, canceled.Credential);
        Assert.False(canceled.IsSetupComplete);
        await context.Services.GetRequiredService<HostedPanelService>().EnrollAsync(state.PanelId);
        Assert.Equal(state.Credential, HostedPanelState.Read()!.Credential);
        Assert.Equal(state.InstallationSecret, HostedPanelState.Read()!.InstallationSecret);
        Assert.False(HostedPanelState.Read()!.IsSetupComplete);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("hosted")]
    public async Task CompletedPostInstallSetupSurvivesNewSessionButCanBeReopened(string choice)
    {
        await HostedPanelState.SaveAsync(new() { Choice = choice, PanelId = "test-panel", SetupCompleted = true });
        await using var context = Context();
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var provider = context.Render<MudDialogProvider>();
        context.Render<AccessSetupPrompt>();
        await provider.InvokeAsync(() => Task.CompletedTask);
        Assert.DoesNotContain("access-setup-header", provider.Markup);
        var page = context.Render<Quasar.Components.Pages.PostInstallSetup>();
        Assert.Contains("Access setup is saved", page.Markup);
        await page.FindAll("button").Single(b => b.TextContent.Contains("Manage access setup")).ClickAsync();
        Assert.Contains("access-setup-header", provider.Markup);
    }

    [Theory]
    [InlineData("hosted")]
    [InlineData("dismissed")]
    public async Task IncompleteSetupPromptsAgainAfterReload(string choice)
    {
        await HostedPanelState.SaveAsync(new() { Choice = choice, PanelId = "test-panel", SetupCompleted = false });
        await using var context = Context();
        await context.Services.GetRequiredService<RbacConfigCatalog>().SaveAsync(RbacConfigCatalog.CreateInitialAdminConfig(SteamId)!);
        var provider = context.Render<MudDialogProvider>();
        context.Render<AccessSetupPrompt>();
        await provider.WaitForAssertionAsync(() => Assert.Contains("access-setup-header", provider.Markup));
        Assert.False(HostedPanelState.Read()!.IsSetupComplete);
    }

    [Fact]
    public async Task DismissingFreshSetupDoesNotCompleteItOrGrantPermissions()
    {
        await using var context = Context();
        var provider = await OpenAsync(context);
        await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync();
        Assert.False(HostedPanelState.Read()!.IsSetupComplete);
        Assert.False(RbacConfigCatalog.HasAdminMapping(context.Services.GetRequiredService<RbacConfigCatalog>().GetConfig()));
        context.Services.GetRequiredService<BunitAuthorizationContext>().SetPolicies();
        context.Render<AccessSetupPrompt>();
        await provider.InvokeAsync(() => Task.CompletedTask);
        Assert.DoesNotContain("access-setup-header", provider.Markup);
    }

    [Theory]
    [InlineData("self", false, true)]
    [InlineData("self", true, true)]
    [InlineData("dismissed", false, false)]
    [InlineData("hosted", true, true)]
    [InlineData("hosted", false, false)]
    public async Task ExistingInstallationsRetainCompletedChoices(string choice, bool credential, bool complete)
    {
        await HostedPanelState.SaveAsync(new() { Choice = choice, PanelId = choice == "self" && !credential ? "" : "test-panel", Credential = credential ? "test-panel." + new string('a', 64) : null });
        Assert.Null(HostedPanelState.Read()!.SetupCompleted);
        Assert.Equal(complete, HostedPanelState.Read()!.IsSetupComplete);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pending")]
    public async Task LegacyCanceledEnrollmentIsUnfinishedAndKeepsItsIdentity(string? enrollmentId)
    {
        var state = new HostedPanelState { Choice = "self", PanelId = "test-panel", EnrollmentId = enrollmentId, BrowserUrl = HostedPanelState.Origin + "/account" };
        await HostedPanelState.SaveAsync(state);
        await using var context = Context();
        context.Services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => new EnrollmentHandler());
        Assert.False(context.Services.GetRequiredService<HostedPanelService>().State.IsSetupComplete);
        await context.Services.GetRequiredService<HostedPanelService>().EnrollAsync(state.PanelId);
        var resumed = HostedPanelState.Read()!;
        Assert.Equal(state.InstallationSecret, resumed.InstallationSecret);
        Assert.Equal(state.EnrollmentId ?? "enrollment", resumed.EnrollmentId);
        Assert.False(resumed.IsSetupComplete);
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
