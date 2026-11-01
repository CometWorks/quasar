using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class RbacAuthorizationTests
{
    [Theory]
    [InlineData(QuasarRoles.Viewer, false)]
    [InlineData(QuasarRoles.Editor, false)]
    [InlineData(QuasarRoles.Admin, true)]
    [InlineData(null, false)]
    public async Task OfflineMaintenanceRequiresAdministrator(string? role, bool allowed)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddAuthorization(Program.ConfigureAuthorizationPolicies).BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity(role is null ? [] : [new Claim(ClaimTypes.Role, role)], "fixture"));
        Assert.Equal(allowed, (await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, null, QuasarPolicyNames.ClusterMaintenance)).Succeeded);
    }

    [Fact]
    public void PrivateLanAdminBypassIsOnByDefault()
    {
        var options = new TrustedNetworkBypassOptions();

        Assert.True(options.AllowLoopback);
        Assert.True(options.AllowSameSubnet);
        Assert.Contains(QuasarRoles.Admin, options.Roles);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void PrivateLanConfigurationSupportsOptOut(string? configured, bool expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null) values["Quasar:Auth:TrustedNetworkBypass:AllowSameSubnet"] = configured;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Equal(expected, QuasarAuthOptions.Create(config).TrustedNetworkBypass.AllowSameSubnet);
    }

    [Theory]
    [InlineData(null, QuasarRoles.Admin)]
    [InlineData(QuasarRoles.Viewer, QuasarRoles.Viewer)]
    [InlineData(QuasarRoles.Editor, QuasarRoles.Editor)]
    public void ConfiguredTrustedNetworkRoleReplacesDefaultAdministrator(string? configured, string expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null) values["Quasar:Auth:TrustedNetworkBypass:Roles:0"] = configured;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal([expected], QuasarAuthOptions.Create(config).TrustedNetworkBypass.Roles);
    }

    [Fact]
    public void RefreshingRolesRemovesStalePrivileges()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "76561198000000000"),
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam),
            new Claim(ClaimTypes.Role, QuasarRoles.Admin),
        ], QuasarAuthSchemes.Steam);

        var refreshed = QuasarRoleMapper.ReplaceRoles(new ClaimsPrincipal(identity), [QuasarRoles.Viewer]);

        Assert.True(refreshed.Identity?.IsAuthenticated);
        Assert.Equal("76561198000000000", refreshed.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(refreshed.IsInRole(QuasarRoles.Viewer));
        Assert.False(refreshed.IsInRole(QuasarRoles.Admin));
    }

    [Fact]
    public void LastAdministratorDetectionCoversSubjectAndClaimMappings()
    {
        Assert.False(RbacConfigCatalog.HasAdminMapping(new RbacConfig()));
        Assert.True(RbacConfigCatalog.HasAdminMapping(new RbacConfig
        {
            SubjectRoleMappings = [Mapping(QuasarRoles.Admin)],
        }));
        Assert.True(RbacConfigCatalog.HasAdminMapping(new RbacConfig
        {
            ClaimRoleMappings =
            [
                new ClaimRoleMapping
                {
                    Provider = "Oidc",
                    Claim = "groups",
                    Value = "operators",
                    Roles = [QuasarRoles.Admin],
                },
            ],
        }));
    }

    [Fact]
    public void InitialAdministratorUsesSteamIdEnvironmentValue()
    {
        var config = RbacConfigCatalog.CreateInitialAdminConfig(" 76561198000000000 ");

        var mapping = Assert.Single(config!.SubjectRoleMappings);
        Assert.Equal(QuasarAuthSchemes.Steam, mapping.Provider);
        Assert.Equal("76561198000000000", mapping.Subject);
        Assert.Equal([QuasarRoles.Admin], mapping.Roles);
    }

    [Fact]
    public void MissingInitialAdministratorDoesNotCreateConfig()
    {
        Assert.Null(RbacConfigCatalog.CreateInitialAdminConfig(null));
        Assert.Null(RbacConfigCatalog.CreateInitialAdminConfig(" "));
    }

    [Theory]
    [InlineData("not-a-steam-id")]
    [InlineData("7656119800000000")]
    [InlineData("7656119800000000a")]
    [InlineData("765611980000000000")]
    public void InitialAdministratorRejectsInvalidSteamId(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RbacConfigCatalog.CreateInitialAdminConfig(value));
        Assert.Contains("17-digit SteamID64", error.Message);
    }

    [Theory]
    [InlineData(typeof(Quasar.Components.Pages.Appearance), QuasarPolicyNames.CanManageAppearance)]
    [InlineData(typeof(Quasar.Components.Pages.Chat), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.ClusterDetail), QuasarPolicyNames.ClusterQuery)]
    [InlineData(typeof(Quasar.Components.Pages.Configs), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.Discord), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.Entities), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.Players), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.Plugins), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.Backup), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.Security), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.UiPlugins), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.Updates), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.WorldTemplates), QuasarPolicyNames.CanView)]
    [InlineData(typeof(Quasar.Components.Pages.ServerListing), QuasarPolicyNames.CanView)]
    public void PagesRequireTheirViewingPolicy(Type pageType, string policy)
    {
        var policies = pageType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy);

        Assert.Contains(policy, policies);
    }

    private static SubjectRoleMapping Mapping(string role) => new()
    {
        Provider = QuasarAuthSchemes.Steam,
        Subject = "76561198000000000",
        Roles = [role],
    };
}
