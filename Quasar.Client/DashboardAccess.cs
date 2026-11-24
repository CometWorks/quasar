using Microsoft.AspNetCore.Authorization;
using Quasar.Services.Auth;

namespace Quasar.Client;

public sealed record DashboardAccess(HashSet<string> Policies, HashSet<string> PluginPages)
{
    public bool CanOpen(Type page)
    {
        if (page.Assembly != typeof(DashboardAccess).Assembly)
            return PluginPages.Contains(page.AssemblyQualifiedName!);

        return Policies.Contains(QuasarPolicyNames.CanView) &&
            page.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()
                .All(attribute => attribute.Policy is { } policy && Policies.Contains(policy));
    }
}
