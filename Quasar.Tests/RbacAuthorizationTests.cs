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
