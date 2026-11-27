using Quasar.Hosting;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class QuasarAccessSetupOperations
{
    public sealed record SetupInput(string Value);
    public sealed record DirectPortInput(int? Port, bool Skip);

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
                // Existing hosted panels are asked once for the direct connection port.
                return !HasAdmin(caller) || !caller.Services.GetRequiredService<HostedPanelService>().State.IsSetupComplete
                    || QuasarDirectPortService.Pending;
            }, read with { Description = "Returns the initial access setup prompt for the current user." });
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, SetupStatus>(plugin, "setup.status", manage,
            (caller, _, _) => Task.FromResult(Snapshot(caller)), read with { Description = "Returns the current access setup and enrollment state." });
        services.AddQuasarOperation<SetupInput, SetupStatus>(plugin, "setup.administrator", manage,
            async (caller, input, token) =>
            {
                var steam = await caller.Services.GetRequiredService<SteamProfileClient>().ResolveSteamIdAsync(input.Value, token);
                await caller.EnsureAuthorizedAsync(token);
                await caller.Services.GetRequiredService<RbacConfigCatalog>().AddFirstSteamAdminAsync(steam);
                return Snapshot(caller);
            }, write with { Description = "Configures the initial administrator during access setup." });
        services.AddQuasarOperation<SetupInput, SetupStatus>(plugin, "setup.choose", manage,
            async (caller, input, _) =>
            {
                if (input.Value == "self" && !HasAdmin(caller)) throw new QuasarOperationException(409, "Configure a Quasar administrator before choosing public access.");
                await caller.Services.GetRequiredService<HostedPanelService>().ChooseAsync(input.Value);
                return Snapshot(caller);
            }, write with { Description = "Records the user's access setup choice." });
        services.AddQuasarOperation<DirectPortInput, SetupStatus>(plugin, "setup.directport", manage,
            async (caller, input, token) =>
            {
                await caller.Services.GetRequiredService<QuasarDirectPortService>().ConfigureAsync(input.Port, input.Skip, token);
                return Snapshot(caller);
            }, write with { Description = "Saves or skips the hosted direct connection UDP port and checks it from the internet." });
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, SetupStatus>(plugin, "setup.directport.test", manage,
            async (caller, _, token) =>
            {
                await caller.Services.GetRequiredService<QuasarDirectPortService>().ProbeAsync(token);
                return Snapshot(caller);
            }, write with { Description = "Checks from the internet whether the hosted direct connection UDP port is open." });
        services.AddQuasarOperation<SetupInput, string>(plugin, "setup.enroll", manage, async (caller, input, token) =>
        {
            var status = Snapshot(caller);
            if (!status.HasAdmin || !status.SteamEnabled) throw new QuasarOperationException(409, "Configure a Quasar administrator and enable Steam authentication before enrolling.");
            return await caller.Services.GetRequiredService<HostedPanelService>().EnrollAsync(input.Value, token);
        }, write with { Description = "Starts the hosted panel enrollment workflow." });
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
            health?.Ready == true, hosted.Status, DirectPort());
    }

    private static DirectPortStatus DirectPort()
    {
        var port = QuasarDirectPortService.State;
        return new(QuasarDirectPortService.Pending, port.Port, port.Skipped, port.CheckedAt, port.Reachable,
            port.Address, port.AddressPublic, port.Error, HostedDirectPort.DefaultPort);
    }
}
