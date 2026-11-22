using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Layout;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static class QuasarPowerOperations
{
    internal sealed record Restart(Guid? UpdateId);
    private static readonly string[] Policy = [QuasarPolicyNames.CanShutdownQuasar];
    private static readonly QuasarOperationMetadata Change = new()
    {
        Effect = QuasarOperationEffect.Change, AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true
    };

    public static void AddQuasarPowerOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<EmptyRequest, DashboardPowerState>(PluginId, "power.status", Policy,
            (caller, _, _) =>
            {
                var options = caller.Services.GetRequiredService<WebServiceOptions>();
                var service = caller.Services.GetRequiredService<QuasarShutdownService>();
                var capability = options.RestartCapability;
                var update = service.PendingRestartUpdate;
                return Task.FromResult(new DashboardPowerState(capability.CanRestart, capability.Detail + " " + capability.Repair,
                    options.AgentOfflineShutdownSeconds, update?.Id, update?.Version, service.DashboardStop));
            }, Change with { Effect = QuasarOperationEffect.Read, Description = "Shows current restart capability, pending update and fleet shutdown progress." });
        services.AddQuasarOperation<Restart, bool>(PluginId, "power.restart", Policy, async (caller, input, _) =>
        {
            if (input.UpdateId.HasValue && !(await caller.Services.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanManageSecurity)).Succeeded)
                throw new QuasarOperationException(403, "Installing a pending update requires permission to manage security and updates.");
            await caller.Services.GetRequiredService<QuasarShutdownService>().RestartWorkerAsync(confirmedUpdateId: input.UpdateId);
            return true;
        }, Change with { Description = "Restarts Quasar while preserving running servers. An update requires confirmation of its identity." });
        services.AddQuasarOperation<EmptyRequest, bool>(PluginId, "power.shutdown", Policy, async (caller, _, _) =>
        {
            await caller.Services.GetRequiredService<QuasarShutdownService>().ShutdownQuasarPreservingServersAsync();
            return true;
        }, Change with { Description = "Stops the Quasar worker while preserving the state of running servers." });
        services.AddQuasarOperation<EmptyRequest, DashboardServerStop>(PluginId, "power.stop-servers", Policy, (caller, _, _) =>
            Task.FromResult(caller.Services.GetRequiredService<QuasarShutdownService>().BeginDashboardStop()),
            Change with { Description = "Saves and stops managed servers and clusters. Returns the worker's active shutdown job." });
    }
}
