using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class RbacAuthorizationTests
{
    [Fact]
    public void SameSubnetAdminBypassIsOffByDefault()
    {
        var options = new TrustedNetworkBypassOptions();

        Assert.True(options.AllowLoopback);
        Assert.False(options.AllowSameSubnet);
        Assert.Contains(QuasarRoles.Admin, options.Roles);
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
    [InlineData(typeof(Quasar.Components.Pages.Chat), QuasarPolicyNames.CanControlServers)]
    [InlineData(typeof(Quasar.Components.Pages.Configs), QuasarPolicyNames.CanEditConfigs)]
    [InlineData(typeof(Quasar.Components.Pages.Discord), QuasarPolicyNames.CanManageDiscord)]
    [InlineData(typeof(Quasar.Components.Pages.Entities), QuasarPolicyNames.CanControlServers)]
    [InlineData(typeof(Quasar.Components.Pages.Players), QuasarPolicyNames.CanControlServers)]
    [InlineData(typeof(Quasar.Components.Pages.Plugins), QuasarPolicyNames.CanEditConfigs)]
    [InlineData(typeof(Quasar.Components.Pages.Backup), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.Security), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.UiPlugins), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.Updates), QuasarPolicyNames.CanManageSecurity)]
    [InlineData(typeof(Quasar.Components.Pages.WorldTemplates), QuasarPolicyNames.CanEditConfigs)]
    public void MutatingPagesRequireTheirPolicy(Type pageType, string policy)
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
