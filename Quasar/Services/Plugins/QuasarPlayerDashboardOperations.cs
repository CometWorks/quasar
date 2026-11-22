using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Operations;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static class QuasarPlayerDashboardOperations
{
    internal sealed record CleanInput(string? Key);
    internal sealed record Retention(int Days);
    internal sealed record ClusterInput(string UniqueName, string SteamId, string Action, string? Level, string? Revision);
    private static readonly string[] ClusterActions = ["kick", "ban", "unban", "clear-kick-cooldown", "set-player-promotion"];
    private static async Task<bool> Has(QuasarOperationContext c, string policy) =>
        (await c.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(c.User, null, policy)).Succeeded;

    internal static void AddPlayerDashboardOperations(this IServiceCollection services)
    {
        var change = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        var read = change with { Effect = QuasarOperationEffect.Read };
        string[] manage = [QuasarPolicyNames.CanView, QuasarPolicyNames.CanControlServers];
        services.AddQuasarRead<EmptyRequest>(PluginId, "players.table", [QuasarPolicyNames.CanView], async (c, _, ct) =>
        {
            var clusters = c.Services.GetRequiredService<ClusterCatalog>().GetClusters().ToDictionary(k => k.UniqueName, StringComparer.OrdinalIgnoreCase);
            var names = c.Services.GetRequiredService<DedicatedServerCatalog>().GetServers().ToDictionary(s => s.UniqueName,
                s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.UniqueName : s.DisplayName, StringComparer.OrdinalIgnoreCase);
            var query = await Has(c, QuasarPolicyNames.ClusterQuery);
            var agents = c.Services.GetRequiredService<AgentRegistry>().GetAgents().Where(a => a.IsConnected && !a.IsCluster)
                .DistinctBy(a => a.UniqueNameKey, StringComparer.OrdinalIgnoreCase).ToDictionary(a => a.UniqueNameKey, StringComparer.OrdinalIgnoreCase);
            var known = c.Services.GetRequiredService<KnownPlayerCatalog>();
            var snapshots = c.Services.GetRequiredService<ClusterPlayerService>();
            var filters = new Dictionary<string,string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (var cluster in clusters.Values.Where(v => query && c.User.CanQueryCluster(v.UniqueName)))
                filters["cluster::" + cluster.UniqueName] = cluster.DisplayName;
            var rows = new List<KnownPlayerView>();
            foreach (var p in known.GetPlayers().Where(p => !p.IsCluster || query && c.User.CanQueryCluster(p.UniqueName)))
            {
                agents.TryGetValue(p.UniqueName, out var agent);
                clusters.TryGetValue(p.UniqueName, out var cluster);
                var snap = p.IsCluster && cluster is not null ? snapshots.GetSnapshot(p.UniqueName) : null;
                var client = snap?.Available == true ? snap.Clients.FirstOrDefault(x => x.ClientId == (ulong)p.SteamId) : null;
                var kick = agent?.Snapshot?.KickedPlayers.FirstOrDefault(k => k.SteamId == p.SteamId);
                var name = p.IsCluster ? cluster?.DisplayName : names.GetValueOrDefault(p.UniqueName);
                if (string.IsNullOrWhiteSpace(name)) name = (!p.IsCluster ? agent?.ServerDisplayName : null) ?? p.ServerName;
                if (string.IsNullOrWhiteSpace(name)) name = p.UniqueName;
                var available = p.IsCluster ? snap?.Available == true : agent is not null;
                var row = new KnownPlayerView
                {
                    Record = new(p.PlayerKey, p.UniqueName, p.IsCluster, p.ServerName, p.SteamId.ToString(CultureInfo.InvariantCulture),
                        TextSanitizer.CleanGameText(p.DisplayName), TextSanitizer.CleanGameText(p.PlatformDisplayName), p.ServiceName, p.FactionTag, p.PromoteLevel, p.IsAdmin, p.IsBanned, p.LastSeenUtc),
                    ServerDisplayName = name,
                    ServerDetail = client is null ? p.UniqueName : $"{p.UniqueName} · {System.Text.RegularExpressions.Regex.Replace(client.AdmissionState, "(?<=[a-z0-9])([A-Z])", m => " " + char.ToLowerInvariant(m.Value[0]))} · {client.Node} / {client.Partition}",
                    IsOnline = p.IsCluster ? client is not null : agent?.Snapshot?.Players.Any(x => x.SteamId == p.SteamId) == true,
                    ConnectionAvailable = available,
                    ModerationUnavailableReason = p.IsCluster ? cluster is null ? "This cluster is no longer configured." : ClusterPlayerSnapshot.ModerationUnavailableReason(snap)
                        : available ? null : $"Server '{p.UniqueName}' is not connected.",
                    KickCooldownExpiresUtc = p.IsCluster ? snap?.Available == true ? snap.Moderation?.KickCooldowns.FirstOrDefault(k => k.UserId == (ulong)p.SteamId)?.ExpiresAt : null
                        : kick is null ? null : DateTimeOffset.UtcNow.AddMilliseconds(kick.RemainingCooldownMs),
                    ClusterActions = ClusterActions.Where(a => snap?.CanRun(ClusterPlayerSnapshot.GatewayOperation(a)) == true
                        && (a != "set-player-promotion" || snap.Moderation?.Promotions is not null)).ToArray(),
                    GatewaySupportsModeration = new[]{"set-player-promotion","clear-kick-cooldown"}.All(a => snap?.Capabilities?.Operations.Contains(ClusterPlayerSnapshot.GatewayOperation(a)) == true),
                    PromotionWarning = snap?.Moderation?.Promotions is null ? snap?.Moderation?.PromotionError ?? "Waiting for World Authority promotion state." : null,
                    PromotionRevision = (snap?.Moderation?.Promotions?.FirstOrDefault(x => x.UserId == (ulong)p.SteamId)?.Revision ?? 0).ToString(CultureInfo.InvariantCulture)
                };
                rows.Add(row);
                filters.TryAdd(row.ServerFilterKey, name);
            }
            var table = new PlayerTable(rows.OrderByDescending(r => r.IsOnline).ThenBy(r => r.ServerDisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Record.DisplayName,
                StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Record.SteamId).ToArray(), filters.Select(f => new PlayerFilterOption(f.Key, f.Value))
                .OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToArray(), known.RetentionDays, KnownPlayerCatalog.MinRetentionDays, KnownPlayerCatalog.MaxRetentionDays,
                await Has(c, QuasarPolicyNames.CanControlServers), query && await Has(c, QuasarPolicyNames.ClusterManage));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(table, JsonSerializerOptions.Web);
            return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length);
        }, read);
        services.AddQuasarOperation<Retention, int>(PluginId, "players.retention.save", manage, (c, input, ct) =>
            c.Services.GetRequiredService<KnownPlayerCatalog>().SetRetentionDaysAsync(input.Days, ct), change);
        services.AddQuasarOperation<CleanInput, int>(PluginId, "players.history.clean", manage, async (c,input,ct) =>
        {
            var known = c.Services.GetRequiredService<KnownPlayerCatalog>();
            var clusters = await Has(c, QuasarPolicyNames.ClusterQuery) && await Has(c, QuasarPolicyNames.ClusterManage);
            if (input.Key is null) return await known.CleanAllAsync(ct, known.GetPlayers().Where(p => !p.IsCluster || clusters && c.User.CanQueryCluster(p.UniqueName))
                .Select(p => p.PlayerKey).ToHashSet(StringComparer.OrdinalIgnoreCase));
            bool cluster = input.Key.StartsWith("cluster::", StringComparison.OrdinalIgnoreCase);
            string name = cluster ? input.Key[9..] : input.Key;
            if (cluster && (!clusters || !c.User.CanQueryCluster(name))) throw new QuasarOperationException(403, "Cluster access denied.");
            return await known.CleanServerAsync(name, ct, cluster);
        },change);
        services.AddQuasarOperation<ClusterInput, PlayerMutationResult>(PluginId, "cluster.players.moderate",
            [QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage], async (c, input, ct) =>
        {
            if (!c.User.CanQueryCluster(input.UniqueName)) throw new QuasarOperationException(403,"Cluster access denied.");
            if (!ulong.TryParse(input.SteamId, out var id) || id == 0 || !ClusterActions.Contains(input.Action))
                throw new QuasarOperationException(400,"Invalid player or moderation action.");
            var snap = c.Services.GetRequiredService<ClusterPlayerService>().GetSnapshot(input.UniqueName);
            if (snap?.CanRun(ClusterPlayerSnapshot.GatewayOperation(input.Action)) != true || input.Action == "kick" && !snap.Clients.Any(x => x.ClientId == id)
                || input.Action == "clear-kick-cooldown" && snap.Moderation?.KickCooldowns.Any(k => k.UserId == id && k.ExpiresAt > DateTimeOffset.UtcNow) != true)
                throw new QuasarOperationException(409, ClusterPlayerSnapshot.ModerationUnavailableReason(snap) ?? "This action is unavailable. Refresh the player list.");
            Admin.SetPlayerPromotionRequest? promotion = null;
            if (input.Action == "set-player-promotion")
            {
                if (snap.Moderation?.Promotions is null || !Enum.TryParse<Admin.PlayerPromotionLevel>(input.Level, out var level) || !Enum.IsDefined(level))
                    throw new QuasarOperationException(400, "Invalid or unavailable promotion.");
                var revision = snap.Moderation.Promotions.FirstOrDefault(p => p.UserId == id)?.Revision ?? 0;
                if (revision.ToString(CultureInfo.InvariantCulture) != input.Revision) throw new QuasarOperationException(409, "Player promotion changed. Refresh first.");
                promotion = new(level, revision);
            }
            var operation = await c.Services.GetRequiredService<ClusterCommandService>().SubmitAsync(input.UniqueName,
                ClusterAdminCommand.Create(input.Action, promotion, target: input.SteamId), Guid.NewGuid().ToString("N"), c.User.Identity?.Name ?? "web-ui", ct);
            return new(operation.Error?.Message ?? $"Cluster player {input.Action}: {operation.State}.", operation.State == ClusterOperationState.Failed);
        }, change);
    }
}
