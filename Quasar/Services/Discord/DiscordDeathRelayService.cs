using Discord;
using Discord.WebSocket;
using Magnetar.Protocol.Model;

namespace Quasar.Services.Discord;

public sealed class DiscordDeathRelayService
{
    private readonly object _sync = new();
    private readonly AgentRegistry _registry;
    private readonly DeathMessagesCatalog _deathMessagesCatalog;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordDeathRelayService> _logger;
    private readonly Dictionary<string, DedupState> _dedup = new(StringComparer.OrdinalIgnoreCase);

    public DiscordDeathRelayService(
        AgentRegistry registry,
        DeathMessagesCatalog deathMessagesCatalog,
        DiscordRateLimiter rateLimiter,
        ILogger<DiscordDeathRelayService> logger)
    {
        _registry = registry;
        _deathMessagesCatalog = deathMessagesCatalog;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var config = _deathMessagesCatalog.GetConfig();
        var agents = _registry.GetAgents();

        foreach (var serverOptions in options.Servers.Where(server =>
                     server.EnableDeathMessages &&
                     server.DeathChannelId.HasValue))
        {
            var deathChannelId = serverOptions.DeathChannelId;
            if (!deathChannelId.HasValue)
                continue;

            var agent = agents.FirstOrDefault(item =>
                item.IsConnected &&
                item.Snapshot is not null &&
                string.Equals(item.UniqueNameKey, serverOptions.UniqueName, StringComparison.OrdinalIgnoreCase));

            if (agent?.Snapshot is null)
                continue;

            var deaths = CollectFreshDeaths(serverOptions.UniqueName, agent.Snapshot.RecentDeaths);
            if (deaths.Count == 0)
                continue;

            if (client.GetChannel(deathChannelId.Value) is not IMessageChannel channel)
                continue;

            foreach (var death in deaths)
            {
                var message = BuildMessage(config, serverOptions, death);
                await _rateLimiter.RunAsync(deathChannelId.Value, () => channel.SendMessageAsync(text: message), cancellationToken);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _dedup.Clear();
        }
    }

    private IReadOnlyList<DeathEventSnapshot> CollectFreshDeaths(string uniqueName, IReadOnlyList<DeathEventSnapshot> recentDeaths)
    {
        if (recentDeaths.Count == 0)
            return [];

        lock (_sync)
        {
            if (!_dedup.TryGetValue(uniqueName, out var dedupState))
            {
                dedupState = new DedupState();
                _dedup[uniqueName] = dedupState;
            }

            var fresh = new List<DeathEventSnapshot>();
            foreach (var death in recentDeaths.OrderBy(item => item.TimestampTicksUtc))
            {
                if (!dedupState.Seen.Add(death.TimestampTicksUtc))
                    continue;

                dedupState.Order.Enqueue(death.TimestampTicksUtc);
                while (dedupState.Order.Count > 500)
                {
                    var expired = dedupState.Order.Dequeue();
                    dedupState.Seen.Remove(expired);
                }

                fresh.Add(death);
            }

            return fresh;
        }
    }

    private string BuildMessage(DeathMessagesConfig config, DiscordServerOptions serverOptions, DeathEventSnapshot death)
    {
        var template = config.GetRandomMessage(death.DeathType);
        var killer = string.IsNullOrWhiteSpace(death.KillerName) ? "Unknown" : death.KillerName.Trim();
        var weapon = string.IsNullOrWhiteSpace(death.WeaponName) ? "Unknown" : death.WeaponName.Trim();
        var victim = string.IsNullOrWhiteSpace(death.VictimName) ? "Unknown" : death.VictimName.Trim();

        var emotes = ParseEmotes(serverOptions.DeathMessageEmotes);
        var emote = emotes.Count == 0 ? "💀" : emotes[Random.Shared.Next(emotes.Count)];

        var message = template
            .Replace("{victim}", victim, StringComparison.Ordinal)
            .Replace("{killer}", killer, StringComparison.Ordinal)
            .Replace("{weapon}", weapon, StringComparison.Ordinal);

        return $"{emote} {message}";
    }

    private static List<string> ParseEmotes(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private sealed class DedupState
    {
        public HashSet<long> Seen { get; } = [];

        public Queue<long> Order { get; } = new();
    }
}
