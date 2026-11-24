using Microsoft.AspNetCore.Authorization;
using Quasar.Client;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class DashboardAccessEndpoints
{
    internal static void MapDashboardAccess(this RouteGroupBuilder sdk)
    {
        sdk.MapGet("/dashboard/access", async (HttpContext http, QuasarBrowserSessions sessions,
            DashboardAccessChanges changes, QuasarOperationService operations, QuasarRoleMapper roles,
            IAuthorizationService authorization, QuasarUiPluginCatalog plugins) =>
        {
            var session = await sessions.BindHttpSessionAsync(http);
            var generation = changes.Current;
            var user = roles.RefreshRoles(http.User);
            var policies = new HashSet<string>();
            var pages = new HashSet<string>();

            // Respect administrator overrides on the existing permission and plugin-page operations.
            if (await operations.CheckAccessAsync(user, QuasarCoreOperations.PluginId, "dashboard.permission", QuasarOperationKind.Json) is null)
            {
                foreach (var field in typeof(QuasarPolicyNames).GetFields())
                {
                    var name = (string)field.GetRawConstantValue()!;
                    if ((await authorization.AuthorizeAsync(user, name)).Succeeded) policies.Add(name);
                }
            }
            if (await operations.CheckAccessAsync(user, QuasarCoreOperations.PluginId, "dashboard.plugin.page", QuasarOperationKind.Json) is null)
            {
                var caller = new QuasarOperationContext(user, http.RequestServices);
                foreach (var page in plugins.BrowserPageTypes)
                    if (await plugins.CanOpenBrowserPageAsync(caller, page.Assembly.FullName!, page.FullName!))
                        pages.Add(page.AssemblyQualifiedName!);
            }

            return Results.Json(new { stamp = generation.Stamp(session), access = new DashboardAccess(policies, pages) });
        });

        sdk.MapGet("/dashboard/access/changes", async (string stamp, HttpContext http,
            DashboardAccessChanges changes, QuasarBrowserSessions sessions) =>
        {
            var session = await sessions.BindHttpSessionAsync(http);
            var generation = changes.Current;
            if (generation.Stamp(session) == stamp)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, session.Revoked);
                try { await generation.Changed.Task.WaitAsync(TimeSpan.FromSeconds(25), stop.Token); }
                catch (TimeoutException) { return Results.NoContent(); }
                catch (OperationCanceledException) when (session.Revoked.IsCancellationRequested) { }
            }
            return Results.Json("reload");
        });
    }
}
