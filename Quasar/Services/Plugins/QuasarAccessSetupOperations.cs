using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class QuasarAccessSetupOperations
{
    public sealed record SetupInput(string Value);

    internal static void AddQuasarSetupDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] manage = [QuasarPolicyNames.CanManageSecurity];
        var write = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        var read = write with { Effect = QuasarOperationEffect.Read };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, bool>(plugin, "setup.prompt", [QuasarPolicyNames.CanView],
            async (caller, _, _) =>
            {
                var auth = caller.Services.GetRequiredService<IAuthorizationService>();
                if (!(await auth.AuthorizeAsync(caller.User, QuasarPolicyNames.CanManageSecurity)).Succeeded) return false;
                return !HasAdmin(caller) || !caller.Services.GetRequiredService<HostedPanelService>().State.IsSetupComplete;
            }, read);
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, SetupStatus>(plugin, "setup.status", manage,
            (caller, _, _) => Task.FromResult(Snapshot(caller)), read);
        services.AddQuasarOperation<SetupInput, SetupStatus>(plugin, "setup.administrator", manage,
            async (caller, input, token) =>
            {
                var steam = await caller.Services.GetRequiredService<SteamProfileClient>().ResolveSteamIdAsync(input.Value, token);
                await caller.EnsureAuthorizedAsync(token);
                await caller.Services.GetRequiredService<RbacConfigCatalog>().AddFirstSteamAdminAsync(steam);
                return Snapshot(caller);
            }, write);
        services.AddQuasarOperation<SetupInput, SetupStatus>(plugin, "setup.choose", manage,
            async (caller, input, _) =>
            {
                if (input.Value == "self" && !HasAdmin(caller)) throw new QuasarOperationException(409, "Configure a Quasar administrator before choosing public access.");
                await caller.Services.GetRequiredService<HostedPanelService>().ChooseAsync(input.Value);
                return Snapshot(caller);
            }, write);
        services.AddQuasarOperation<SetupInput, string>(plugin, "setup.enroll", manage, async (caller, input, token) =>
        {
            var status = Snapshot(caller);
            if (!status.HasAdmin || !status.SteamEnabled) throw new QuasarOperationException(409, "Configure a Quasar administrator and enable Steam authentication before enrolling.");
            return await caller.Services.GetRequiredService<HostedPanelService>().EnrollAsync(input.Value, token);
        }, write);
    }

    private static bool HasAdmin(QuasarOperationContext caller) =>
        RbacConfigCatalog.HasAdminMapping(caller.Services.GetRequiredService<RbacConfigCatalog>().GetConfig());

    private static SetupStatus Snapshot(QuasarOperationContext caller)
    {
        var hosted = caller.Services.GetRequiredService<HostedPanelService>();
        var state = hosted.State;
        var auth = caller.Services.GetRequiredService<QuasarAuthOptions>();
        var health = hosted.ConnectorHealth;
        return new(HasAdmin(caller), state.IsSetupComplete, auth.Enabled && auth.Steam.Enabled, state.Choice, state.PanelId,
            state.EnrollmentId is not null, state.BrowserUrl, state.DashboardUrl, hosted.AccessActive, health?.TunnelRegistered == true,
            health?.Ready == true, hosted.Status);
    }
}
