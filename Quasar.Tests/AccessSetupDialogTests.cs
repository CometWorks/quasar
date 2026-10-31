using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Shared;
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

    public void Dispose()
    {
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
