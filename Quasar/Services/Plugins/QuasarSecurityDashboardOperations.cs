using System.Security.Claims;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
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
    private sealed record LinkKey(string Provider, string Operator, string SteamId);
    private sealed record PendingLink(SharedAccountVerification.Request Request, string FinishToken);
    public sealed record CompleteLink(string SteamId, string FinishToken);
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
        services.AddQuasarOperation<SteamAccount, DiscordLinkStart>(plugin, "security.mapping.link.start", manage,
            async (caller, input, token) =>
            {
                var key = LinkOwner(caller, input.SteamId);
                var services = caller.Services;
                if (services.GetRequiredService<QuasarPlayerLinks>().FindBySteamId(input.SteamId) is not null)
                    throw new QuasarOperationException(409, "This Steam account already has a verified Discord link.");
                var verification = services.GetRequiredService<SharedAccountVerification>();
                var name = services.GetRequiredService<BrandingService>().Settings.AppName;
                var destination = new string(($"{name} · Quasar dashboard").Where(c => !char.IsControl(c)).Take(200).ToArray());
                SharedAccountVerification.Request request;
                try { request = await verification.StartAsync(input.SteamId, discordId: null, destination, "", token); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
                { throw new QuasarOperationException(502, "The shared login could not start dashboard verification. Try again shortly."); }
                var pending = new PendingLink(request, Guid.NewGuid().ToString("N"));
                services.GetRequiredService<IMemoryCache>().Set(key, pending, TimeSpan.FromMinutes(5));
                return new DiscordLinkStart(request.Link.AbsoluteUri, pending.FinishToken);
            }, write with { Description = "Starts Steam and Discord verification for a dashboard account." });
        services.AddQuasarOperation<CompleteLink, bool>(plugin, "security.mapping.link.finish", manage,
            async (caller, input, token) =>
            {
                var key = LinkOwner(caller, input.SteamId);
                var cache = caller.Services.GetRequiredService<IMemoryCache>();
                var pending = cache.Get<PendingLink>(key);
                if (pending is null || pending.FinishToken != input.FinishToken)
                    throw new QuasarOperationException(410, "This verification request expired or was replaced. Start again.");
                var verification = caller.Services.GetRequiredService<SharedAccountVerification>();
                SharedAccountVerification.Proof? proof;
                try { proof = await verification.RedeemAsync(pending.Request, token); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                { throw new QuasarOperationException(410, "This verification request has expired or has already been used. Start again."); }
                if (proof is null) return false;
                if (proof.SteamId != input.SteamId || !ulong.TryParse(proof.DiscordId, out var discordId) || discordId == 0
                    || discordId.ToString(CultureInfo.InvariantCulture) != proof.DiscordId)
                    throw new QuasarOperationException(409, "The verified accounts did not match this request.");
                await caller.EnsureAuthorizedAsync(token);
                CheckLinkSubject(caller, input.SteamId);
                if (!ReferenceEquals(cache.Get<PendingLink>(key), pending))
                    throw new QuasarOperationException(410, "This verification request expired or was replaced. Start again.");
                try { await caller.Services.GetRequiredService<QuasarPlayerLinks>().AuthenticateAsync(proof.SteamId, proof.DiscordId, token); }
                catch (InvalidOperationException ex) { throw new QuasarOperationException(409, ex.Message); }
                return true;
            }, write with { Description = "Checks the identity service and saves a verified Steam and Discord account link when ready." });
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

    private static LinkKey LinkOwner(QuasarOperationContext caller, string steamId)
    {
        CheckLinkSubject(caller, steamId);
        var subject = caller.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new QuasarOperationException(403, "Sign in to link your Discord account.");
        return new(caller.User.FindFirstValue(QuasarClaimTypes.Provider) ?? caller.User.Identity?.AuthenticationType ?? "", subject, steamId);
    }

    private static void CheckLinkSubject(QuasarOperationContext caller, string steamId)
    {
        if (!RbacConfigCatalog.IsSteamId64(steamId) || caller.Services.GetRequiredService<RbacConfigCatalog>().GetSubjectRoles(QuasarAuthSchemes.Steam, steamId).Count == 0)
            throw new QuasarOperationException(404, "This Steam role mapping no longer exists.");
        if (caller.User.FindFirstValue(QuasarClaimTypes.SteamId) is { } signedIn && signedIn != steamId)
            throw new QuasarOperationException(403, "Sign in with the Steam account you want to link.");
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
            QuasarRoles.All.Order(StringComparer.OrdinalIgnoreCase).ToArray(), caller.User.FindFirstValue(QuasarClaimTypes.SteamId));
    }
}
