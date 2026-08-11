using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Quasar.Services.Auth;

public sealed class QuasarPermissionService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    QuasarRoleMapper roleMapper)
{
    public async Task<bool> IsAuthorizedAsync(string policyName)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var currentPrincipal = roleMapper.RefreshRoles(state.User);
        var result = await authorizationService.AuthorizeAsync(currentPrincipal, resource: null, policyName);
        return result.Succeeded;
    }
}
