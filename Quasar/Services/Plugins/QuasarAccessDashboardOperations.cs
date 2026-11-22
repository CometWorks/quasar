using System.Text.Json;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Discord;
using Quasar.Services.Players;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static class QuasarAccessDashboardOperations
{
    internal sealed record AccessEdit(string Settings);
    internal sealed record Selection(string Key);
    internal sealed record Steam(string SteamId);

    internal static void AddAccessDashboardOperations(this IServiceCollection services)
    {
        string[] manage = [QuasarPolicyNames.CanView, QuasarPolicyNames.CanManageSecurity];
        var change = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        var read = change with { Effect = QuasarOperationEffect.Read };
        services.AddQuasarOperation<EmptyRequest, AccessDashboard>(PluginId, "access.dashboard", manage, (caller, _, _) =>
        {
            var scope = caller.Services;
            var settings = scope.GetRequiredService<PlayerAccessCatalog>().GetSettings();
            settings.DiscordClientSecret = ""; settings.DiscordClientId = "";
            settings.Policies.RemoveAll(p => !InScope(caller, p.Target));
            var bot = scope.GetRequiredService<DiscordOptionsCatalog>().GetOptions();
            var guild = scope.GetService<DiscordBotService>()?.CurrentClient?.GetGuild(bot.GuildId);
            var worker = scope.GetRequiredService<PlayerAccessWorker>();
            var browser = scope.GetRequiredService<PlayerAuthBrowser>();
            return Task.FromResult(new AccessDashboard(JsonSerializer.Serialize(settings), bot.GuildId.ToString(), bot.Enabled && bot.GuildId != 0, guild is not null,
                guild?.Name ?? (bot.GuildId == 0 ? "Not configured" : bot.GuildId.ToString()),
                guild?.Roles.Where(r => !r.IsEveryone).OrderByDescending(r => r.Position).Select(r => new AccessRole(r.Id.ToString(), r.Name + " (" + r.Id + ")")).ToArray() ?? [],
                scope.GetRequiredService<PlayerAccessService>().Targets().Where(t => InScope(caller, t.Key)).Select(t => new AccessTarget(t.Key, t.Name, worker.Status(t.Key))).ToArray(),
                browser.Ready, browser.PublicUrl));
        }, read with { Description = "Returns the server access settings and verification state." });
        services.AddQuasarOperation<AccessEdit, bool>(PluginId, "access.settings.save", manage, async (caller, input, ct) =>
        {
            var scope = caller.Services;
            var store = scope.GetRequiredService<PlayerAccessCatalog>();
            var current = store.GetSettings();
            var settings = JsonSerializer.Deserialize<PlayerAccessSettings>(input.Settings) ?? throw new QuasarOperationException(400, "Invalid settings.");
            if (settings.Policies.Any(p => !InScope(caller, p.Target))) throw new QuasarOperationException(403, "Cluster access denied.");
            settings.DiscordClientSecret = current.DiscordClientSecret; settings.DiscordClientId = current.DiscordClientId;
            var targets = scope.GetRequiredService<PlayerAccessService>().Targets();
            var bot = scope.GetRequiredService<DiscordOptionsCatalog>().GetOptions();
            foreach (var policy in settings.Policies.Where(p => p.Enabled))
            {
                if (scope.GetRequiredService<AgentRegistry>().GetAgents().Any(a => a.IsConnected
                    && (a.IsCluster ? "cluster:" + a.ClusterId : "server:" + a.UniqueNameKey) == policy.Target
                    && a.Snapshot?.Plugins.Any(PlayerAccessWorker.IsLegacyAuthentication) == true))
                    throw new InvalidOperationException("Remove the separate GameServerAuth plugin before enabling built-in access.");
                if (!bot.Enabled || bot.GuildId == 0 || policy.GuildId != bot.GuildId)
                    throw new InvalidOperationException("Enable Quasar's Discord bot and use its configured Discord server before enabling player authentication.");
                if (!targets.Any(t => t.Key == policy.Target)) throw new InvalidOperationException("A configured server no longer exists.");
                if (policy.Target.StartsWith("cluster:"))
                {
                    var cluster = scope.GetRequiredService<ClusterCatalog>().GetClusters().Single(c => "cluster:" + c.UniqueName == policy.Target);
                    await scope.GetRequiredService<ClusterHostClient>().RequirePlayerAccessHostAsync(cluster, ct);
                    var caps = (await scope.GetRequiredService<ClusterGatewayClient>().GetCapabilitiesAsync(cluster, ct)).Data;
                    if (!caps.Operations.Contains("PUT /admin/v1/players/access")) throw new InvalidOperationException("Update and connect the cluster package before enabling player access.");
                }
                else if (scope.GetRequiredService<AgentRegistry>().GetAgents().Any(a => !a.IsCluster && a.IsConnected
                    && "server:" + a.UniqueNameKey == policy.Target && a.Hello?.PlayerAccessVersion != 1))
                    throw new InvalidOperationException("Restart this server to load the updated Agent before enabling player authentication.");
            }
            settings.Policies.AddRange(current.Policies.Where(p => !InScope(caller, p.Target)));
            await store.SaveSettingsAsync(settings, ct);
            return true;
        }, change with { Description = "Saves the selected server's access settings." });
        services.AddQuasarOperation<EmptyRequest, bool>(PluginId, "access.website.verify", manage, async (caller, _, ct) =>
        {
            await caller.Services.GetRequiredService<PlayerAuthBrowser>().VerifyAsync(ct);
            return true;
        }, change with { Description = "Verifies the configured website connection." });
        services.AddQuasarOperation<Selection, List<AccessRow>>(PluginId, "access.players.check", manage, async (caller, input, ct) =>
        {
            if (!InScope(caller, input.Key)) throw new QuasarOperationException(403, "Cluster access denied.");
            var policy = caller.Services.GetRequiredService<PlayerAccessCatalog>().GetSettings().Policies.FirstOrDefault(p => p.Target == input.Key && p.Enabled)
                ?? throw new QuasarOperationException(409, "Save and enable this server policy before checking authorization.");
            var links = caller.Services.GetRequiredService<QuasarPlayerLinks>().GetLinks();
            var service = caller.Services.GetRequiredService<PlayerAccessService>();
            var rows = new List<AccessRow>();
            foreach (var steam in links.Select(l => l.SteamId).Concat(policy.AllowedSteamIds).Distinct().Order())
            {
                var decision = await service.EvaluateAsync(policy, steam, ct);
                rows.Add(new(steam, links.FirstOrDefault(l => l.SteamId == steam)?.DiscordUserId ?? "Not linked", decision.Reason,
                    decision.Grant.Promotion, decision.Grant.Priority, decision.Grant.ExpiresAt));
            }
            return rows;
        }, read with { Description = "Checks current player access against the server settings." });
        services.AddQuasarOperation<EmptyRequest, LinkedPlayer[]>(PluginId, "access.links", [QuasarPolicyNames.CanView], (caller, _, _) =>
        {
            var names = caller.Services.GetRequiredService<KnownPlayerCatalog>().GetPlayers().Where(p => !string.IsNullOrWhiteSpace(p.DisplayName) &&
                (!p.IsCluster || caller.User.CanQueryCluster(p.UniqueName))).OrderByDescending(p => p.LastSeenUtc)
                .DistinctBy(p => p.SteamId).ToDictionary(p => p.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture), p => p.DisplayName);
            return Task.FromResult(caller.Services.GetRequiredService<QuasarPlayerLinks>().GetLinks().Select(l => new LinkedPlayer(l.SteamId, l.DiscordUserId,
                l.Authenticated, l.LinkedAtUtc, names.GetValueOrDefault(l.SteamId, "Unknown"))).ToArray());
        }, read with { Description = "Lists linked game and website accounts visible to the caller." });
        services.AddQuasarOperation<Steam, bool>(PluginId, "access.links.remove", [QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers],
            async (caller, input, ct) => { await caller.Services.GetRequiredService<QuasarPlayerLinks>().RemoveAsync(input.SteamId, ct); return true; }, change with { Description = "Removes a selected player account link." });
    }

    private static bool InScope(QuasarOperationContext caller, string key) =>
        !key.StartsWith("cluster:", StringComparison.OrdinalIgnoreCase) || caller.User.CanQueryCluster(key[8..]);
}
