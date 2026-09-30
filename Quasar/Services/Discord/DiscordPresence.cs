using Discord;
using Quasar.Models;

namespace Quasar.Services.Discord;

internal sealed record DiscordPresenceServer(string Key, string Name, bool Online, int? Players,
    string State, bool Issue = false, bool Warning = false);
internal readonly record struct DiscordPresenceSnapshot(UserStatus Status, string? Activity, ActivityType ActivityType);

internal static class DiscordPresence
{
    internal static DiscordPresenceSnapshot Build(DiscordPresenceOptions options, IEnumerable<DiscordPresenceServer> servers)
    {
        var selected = servers.Where(s => options.Includes(s.Key)).DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var issues = selected.Count(s => s.Issue);
        var warnings = selected.Count(s => s.Warning);
        var online = selected.Count(s => s.Online);
        var status = options.Status switch
        {
            DiscordPresenceStatus.Online => UserStatus.Online,
            DiscordPresenceStatus.Idle => UserStatus.Idle,
            DiscordPresenceStatus.DoNotDisturb => UserStatus.DoNotDisturb,
            DiscordPresenceStatus.Invisible => UserStatus.Invisible,
            _ => issues > 0 ? UserStatus.DoNotDisturb : online > 0 ? UserStatus.Online : UserStatus.Idle,
        };
        var parts = new List<string>();
        if (options.ShowActivity && selected.Length > 0)
        {
            if (options.ShowServerCount) parts.Add($"{online}/{selected.Length} servers online");
            if (options.ShowPlayerCount)
                parts.Add(selected.All(s => s.Players == null) ? "player count unavailable"
                    : $"{selected.Sum(s => s.Players ?? 0)} players" + (selected.Any(s => s.Players == null) ? " (partial)" : ""));
            if (options.ShowHealth)
            {
                if (issues > 0) parts.Add($"{issues} issues");
                if (warnings > 0) parts.Add($"{warnings} warnings");
            }
            if (options.ShowServerStates) parts.AddRange(selected.Select(s => $"{s.Name}: {s.State}"));
            else if (options.ShowServerNames) parts.AddRange(selected.Select(s => s.Name));
        }
        var activity = string.Join(", ", parts);
        // Discord's activity limit is 128 UTF-16 code units; don't split a surrogate pair.
        if (activity.Length > 128)
            activity = activity[..(char.IsHighSurrogate(activity[126]) ? 126 : 127)] + "…";
        var type = options.ActivityType switch
        {
            DiscordPresenceActivity.Playing => ActivityType.Playing,
            DiscordPresenceActivity.Listening => ActivityType.Listening,
            DiscordPresenceActivity.Competing => ActivityType.Competing,
            _ => ActivityType.Watching,
        };
        return new(status, activity.Length == 0 ? null : activity, type);
    }

    internal static IEnumerable<DiscordPresenceServer> Standalone(
        IEnumerable<DedicatedServerRuntimeSnapshot> servers, IReadOnlyList<AgentRuntimeState> agents)
    {
        foreach (var server in servers)
        {
            var matches = agents.Where(a => !a.IsCluster && a.IsConnected && a.Snapshot != null
                && a.UniqueNameKey.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.UtcNow - a.LastSnapshotReceivedUtc <= TimeSpan.FromSeconds(15)).ToArray();
            var online = server.State == DedicatedServerProcessState.Running;
            int? players = matches.Length == 1 ? matches[0].Snapshot!.Metrics.PlayersOnline
                : server.State == DedicatedServerProcessState.Stopped ? 0 : null;
            yield return new(server.UniqueName, server.UniqueName, online, players, server.State.ToString(),
                server.State is DedicatedServerProcessState.Crashed or DedicatedServerProcessState.Faulted
                    || server.HealthState == DedicatedServerHealthState.Unhealthy,
                server.HealthState == DedicatedServerHealthState.Warning);
        }
    }
}
