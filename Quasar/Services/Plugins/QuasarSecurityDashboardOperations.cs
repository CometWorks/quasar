using Quasar.Client.Administration;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class QuasarSecurityDashboardOperations
{
    public sealed record MappingChange(string Provider, string Subject, string? Role = null);
    public sealed record DiscordChange(string DiscordUserId, string? Subject = null);
    public sealed record ConsentInput(bool Consent);
    public sealed record DiagnosticsInput(bool Diagnostics, bool Dumps);
    public sealed record NetworkInput(QuasarTrustedNetworkSettings Network, string Proxies);

    internal static void AddQuasarSecurityDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] manage = [QuasarPolicyNames.CanManageSecurity];
        var write = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, SecurityDashboard>(plugin, "security.dashboard", manage,
            (caller, _, _) => Task.FromResult(Snapshot(caller)), write with { Effect = QuasarOperationEffect.Read });
        services.AddQuasarOperation<NetworkInput, bool>(plugin, "security.network.save", manage,
            async (caller, input, token) =>
            {
                if (input.Network is null) throw new QuasarOperationException(400, "Missing trusted network settings.");
                input.Network.TrustedProxies = QuasarAuthSettingsService.ParseTrustedProxiesText(input.Proxies);
                await caller.Services.GetRequiredService<QuasarAuthSettingsService>().SaveTrustedNetworkSettingsAsync(input.Network, token);
                return true;
            }, write);
        services.AddQuasarOperation<ConsentInput, bool>(plugin, "security.consent.save", manage,
            async (caller, input, token) =>
            {
                await caller.Services.GetRequiredService<DataHandlingConsentCatalog>().SaveAsync(input.Consent, input.Consent, false, token);
                return true;
            }, write);
        services.AddQuasarOperation<DiagnosticsInput, bool>(plugin, "security.diagnostics.save", manage,
            async (caller, input, token) =>
            {
                var catalog = caller.Services.GetRequiredService<DataHandlingConsentCatalog>();
                await catalog.SaveAsync(catalog.GetSettings().ConsentGranted, input.Diagnostics, input.Dumps, token);
                return true;
            }, write);
        services.AddQuasarOperation<MappingChange, bool>(plugin, "security.mapping.add", manage,
            async (caller, input, token) =>
            {
                if (input.Provider is not ("Steam" or "Oidc") || string.IsNullOrWhiteSpace(input.Subject)
                    || string.IsNullOrWhiteSpace(input.Role) || !QuasarRoles.All.Contains(input.Role))
                    throw new QuasarOperationException(400, "Select a provider, subject and valid role.");
                var subject = input.Provider == QuasarAuthSchemes.Steam
                    ? await ResolveSteamAsync(caller, input.Subject, token) : input.Subject.Trim();
                return await UpdateRbacAsync(caller, config => config.SubjectRoleMappings.Add(new()
                    { Provider = input.Provider, Subject = subject, Roles = [input.Role] }), token);
            }, write);
        services.AddQuasarOperation<MappingChange, bool>(plugin, "security.mapping.remove", manage,
            (caller, input, token) => UpdateRbacAsync(caller, config => config.SubjectRoleMappings.RemoveAll(item =>
                item.Provider.Equals(input.Provider, StringComparison.OrdinalIgnoreCase) && item.Subject.Equals(input.Subject, StringComparison.OrdinalIgnoreCase)), token), write);
        services.AddQuasarOperation<DiscordChange, bool>(plugin, "security.discord.add", manage, async (caller, input, token) =>
        {
            var id = input.DiscordUserId?.Trim();
            if (!ulong.TryParse(id, out _)) throw new QuasarOperationException(400, "Enter the numeric Discord user ID.");
            var steam = await ResolveSteamAsync(caller, input.Subject, token);
            return await UpdateRbacAsync(caller, config =>
            {
                config.DiscordSubjects.RemoveAll(item => item.DiscordUserId == id);
                config.DiscordSubjects.Add(new() { DiscordUserId = id!, Subject = steam });
            }, token);
        }, write);
        services.AddQuasarOperation<DiscordChange, bool>(plugin, "security.discord.remove", manage,
            (caller, input, token) => UpdateRbacAsync(caller, config => config.DiscordSubjects.RemoveAll(item => item.DiscordUserId == input.DiscordUserId), token), write);
    }

    private static async Task<string> ResolveSteamAsync(QuasarOperationContext caller, string? input, CancellationToken token)
    {
        try { return await caller.Services.GetRequiredService<SteamProfileClient>().ResolveSteamIdAsync(input, token); }
        catch (InvalidOperationException error) { throw new QuasarOperationException(400, error.Message); }
    }

    private static async Task<bool> UpdateRbacAsync(QuasarOperationContext caller, Action<RbacConfig> change, CancellationToken token)
    {
        await caller.Services.GetRequiredService<RbacConfigCatalog>().UpdateAsync(async config =>
        {
            await caller.EnsureAuthorizedAsync(token);
            change(config);
        }, token);
        return true;
    }

    private static SecurityDashboard Snapshot(QuasarOperationContext caller)
    {
        var auth = caller.Services.GetRequiredService<QuasarAuthOptions>();
        var network = caller.Services.GetRequiredService<QuasarAuthSettingsService>();
        var consent = caller.Services.GetRequiredService<DataHandlingConsentCatalog>();
        var choices = consent.GetSettings();
        var catalog = caller.Services.GetRequiredService<RbacConfigCatalog>();
        var config = catalog.GetConfig();
        return new(auth.DefaultProvider, auth.Steam.Enabled, caller.Services.GetRequiredService<HostedPanelService>().State.EnrollmentId is not null,
            network.SettingsPath, consent.SettingsPath, network.GetTrustedNetworkSettings(),
            new(choices.ConsentGranted, choices.DiagnosticsGranted, choices.DumpsGranted),
            config.SubjectRoleMappings.Select(item => new RoleMapping(item.Provider, item.Subject, item.Roles.ToArray())).ToArray(),
            config.DiscordSubjects.Select(item => new DiscordMapping(item.DiscordUserId, item.Subject, catalog.GetSubjectRoles(item.Provider, item.Subject).ToArray())).ToArray(),
            QuasarRoles.All.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
