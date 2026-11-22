using System.Security.Claims;
using Quasar.Client.Administration;
using Quasar.Services.Discord;
using Quasar.Services.Players;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class QuasarSecurityDashboardOperations
{
    public sealed record MappingChange(string Provider, string Subject, string? Role = null);
    public sealed record SteamAccount(string SteamId);
    public sealed record ConsentInput(bool Consent);
    public sealed record DiagnosticsInput(bool Diagnostics, bool Dumps);
    public sealed record NetworkInput(QuasarTrustedNetworkSettings Network, string Proxies);

    internal static void AddQuasarSecurityDashboard(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] manage = [QuasarPolicyNames.CanManageSecurity];
        var write = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, SecurityDashboard>(plugin, "security.dashboard", manage,
            (caller, _, _) => Task.FromResult(Snapshot(caller)), write with { Description = "Returns security configuration, providers and role mappings.", Effect = QuasarOperationEffect.Read });
        services.AddQuasarOperation<NetworkInput, bool>(plugin, "security.network.save", manage,
            async (caller, input, token) =>
            {
                if (input.Network is null) throw new QuasarOperationException(400, "Missing trusted network settings.");
                input.Network.TrustedProxies = QuasarAuthSettingsService.ParseTrustedProxiesText(input.Proxies);
                await caller.Services.GetRequiredService<QuasarAuthSettingsService>().SaveTrustedNetworkSettingsAsync(input.Network, token);
                return true;
            }, write with { Description = "Saves trusted network settings." });
        services.AddQuasarOperation<ConsentInput, bool>(plugin, "security.consent.save", manage,
            async (caller, input, token) =>
            {
                await caller.Services.GetRequiredService<DataHandlingConsentCatalog>().SaveAsync(input.Consent, input.Consent, false, token);
                return true;
            }, write with { Description = "Saves the panel's consent settings." });
        services.AddQuasarOperation<DiagnosticsInput, bool>(plugin, "security.diagnostics.save", manage,
            async (caller, input, token) =>
            {
                var catalog = caller.Services.GetRequiredService<DataHandlingConsentCatalog>();
                await catalog.SaveAsync(catalog.GetSettings().ConsentGranted, input.Diagnostics, input.Dumps, token);
                return true;
            }, write with { Description = "Saves diagnostic reporting preferences." });
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
            }, write with { Description = "Adds a subject role mapping." });
        services.AddQuasarOperation<MappingChange, bool>(plugin, "security.mapping.remove", manage,
            (caller, input, token) => UpdateRbacAsync(caller, config => config.SubjectRoleMappings.RemoveAll(item =>
                item.Provider.Equals(input.Provider, StringComparison.OrdinalIgnoreCase) && item.Subject.Equals(input.Subject, StringComparison.OrdinalIgnoreCase)), token), write with { Description = "Removes a subject role mapping." });
        services.AddQuasarOperation<SteamAccount, MappingNames>(plugin, "security.mapping.names", manage, async (caller, input, token) =>
        {
            var services = caller.Services;
            var id = input.SteamId;
            if (!RbacConfigCatalog.IsSteamId64(id) || services.GetRequiredService<RbacConfigCatalog>().GetSubjectRoles(QuasarAuthSchemes.Steam, id).Count == 0)
                throw new QuasarOperationException(404, "Steam role mapping no longer exists.");

            var discordId = services.GetRequiredService<QuasarPlayerLinks>().FindBySteamId(id)?.DiscordUserId;
            var ticketName = caller.User.FindFirstValue(QuasarClaimTypes.Provider) == QuasarAuthSchemes.Steam &&
                caller.User.FindFirstValue(QuasarClaimTypes.SteamId) == id ? caller.User.FindFirstValue(QuasarClaimTypes.SteamDisplayName) : null;
            var steamName = ticketName ?? await services.GetRequiredService<SteamProfileClient>().GetDisplayNameAsync(id, token);
            var discordName = ulong.TryParse(discordId, out var userId) && services.GetService<DiscordBotService>() is { } bot
                ? await bot.GetUsernameAsync(userId, token) : null;
            return new MappingNames(steamName, discordId, discordName);
        }, write with { Description = "Looks up display names for a mapped Steam account and its verified Discord link.", Effect = QuasarOperationEffect.Read });
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
        var links = caller.Services.GetRequiredService<QuasarPlayerLinks>().GetLinks().Where(link => link.Authenticated).ToLookup(link => link.SteamId);
        return new(auth.DefaultProvider, auth.Steam.Enabled, caller.Services.GetRequiredService<HostedPanelService>().State.EnrollmentId is not null,
            network.SettingsPath, consent.SettingsPath, network.GetTrustedNetworkSettings(),
            new(choices.ConsentGranted, choices.DiagnosticsGranted, choices.DumpsGranted),
            config.SubjectRoleMappings.Select(item => new RoleMapping(item.Provider, item.Subject, item.Roles.ToArray(),
                item.Provider.Equals(QuasarAuthSchemes.Steam, StringComparison.OrdinalIgnoreCase) ? links[item.Subject].FirstOrDefault()?.DiscordUserId : null)).ToArray(),
            QuasarRoles.All.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
