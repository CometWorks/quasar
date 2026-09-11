using Discord;
using Discord.WebSocket;
using Quasar.Models;

namespace Quasar.Services.Discord;

public sealed class DiscordStatusRelayService(
    AgentRegistry registry,
    DedicatedServerSupervisor supervisor,
    DiscordRateLimiter rateLimiter,
    ILogger<DiscordStatusRelayService> logger)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ServerStatus> _servers = new(StringComparer.OrdinalIgnoreCase);
    private Task _sending = Task.CompletedTask;

    public void Reset()
    {
        lock (_sync)
            _servers.Clear();
    }

    public Task HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            // Observe before returning to the event publisher: a short restart transition
            // can disappear before a background task gets to read the supervisor.
            var agents = registry.GetAgents();
            var messages = new List<(ulong ChannelId, string Text)>();
            foreach (var server in supervisor.GetSnapshots())
            {
                var settings = options.Servers.FirstOrDefault(item =>
                    string.Equals(item.UniqueName, server.UniqueName, StringComparison.OrdinalIgnoreCase));
                if (settings is null)
                    continue;

                var agent = agents.FirstOrDefault(item => item.IsConnected &&
                    string.Equals(item.UniqueNameKey, server.UniqueName, StringComparison.OrdinalIgnoreCase));
                var channelId = settings.StatusChannelId ?? settings.ChatRelayChannelId;
                foreach (var message in Observe(settings, server, agent))
                {
                    if (channelId.HasValue)
                        messages.Add((channelId.Value, message));
                }
            }

            // Keep lifecycle and player notifications in observation order.
            if (messages.Count == 0)
                return Task.CompletedTask;

            return _sending = SendAsync(_sending, client, messages, cancellationToken);
        }
    }

    internal IReadOnlyList<string> Observe(
        DiscordServerOptions settings, DedicatedServerRuntimeSnapshot server, AgentRuntimeState? agent)
    {
        lock (_sync)
        {
            if (!_servers.TryGetValue(server.UniqueName, out var previous))
            {
                _servers[server.UniqueName] = Capture(server, agent);
                return [];
            }

            var current = Capture(server, agent);
            var messages = new List<string>();
            var prefix = $"[{SafeName(server.UniqueName)}] ";
            if (settings.EnableServerNotifications)
            {
                if (server.LastRestart is { Outcome: DedicatedServerRestartOutcome.Pending } restart &&
                    restart.RequestedAtUtc != previous.RestartRequestedAtUtc)
                    messages.Add(prefix + "🔄 Server is going to Restart!");

                if (current.State == DedicatedServerProcessState.Running &&
                    previous.State != DedicatedServerProcessState.Running)
                    messages.Add(prefix + "✅ Server Started!");

                if (IsClosed(current.State) && !IsClosed(previous.State))
                    messages.Add(prefix + "❌ Server Closed!");
            }

            if (settings.EnablePlayerNotifications && current.Players is not null && previous.Players is not null &&
                current.ConnectionId == previous.ConnectionId && current.StartedAtUtc == previous.StartedAtUtc)
            {
                foreach (var (id, name) in current.Players)
                {
                    if (!previous.Players.ContainsKey(id))
                        messages.Add(prefix + $"🚀 **{SafeName(name)}** connected to the server!");
                }

                foreach (var (id, name) in previous.Players)
                {
                    if (!current.Players.ContainsKey(id))
                        messages.Add(prefix + $"☄️ **{SafeName(name)}** disconnected from server!");
                }
            }

            _servers[server.UniqueName] = current;
            return messages;
        }
    }

    private static ServerStatus Capture(DedicatedServerRuntimeSnapshot server, AgentRuntimeState? agent)
    {
        Dictionary<(long SteamId, int SerialId), string>? players = null;
        if (server.State == DedicatedServerProcessState.Running &&
            agent is { IsConnected: true, Snapshot.IsRunning: true })
        {
            players = new();
            foreach (var player in agent.Snapshot.Players.Where(player => player.SteamId != 0))
                players[(player.SteamId, player.SerialId)] = player.DisplayName;
        }

        return new ServerStatus(server.State, server.LastRestart?.RequestedAtUtc,
            server.StartedAtUtc, agent?.ConnectionId, players);
    }

    private static bool IsClosed(DedicatedServerProcessState state) => state is
        DedicatedServerProcessState.Stopped or DedicatedServerProcessState.Crashed or DedicatedServerProcessState.Faulted;

    private static string SafeName(string name)
    {
        var clean = TextSanitizer.CleanGameText(name);
        if (string.IsNullOrWhiteSpace(clean))
            return "Unknown";

        return Format.Sanitize(clean.Length > 100 ? clean[..100] : clean);
    }

    private async Task SendAsync(Task previous, DiscordSocketClient client,
        IReadOnlyList<(ulong ChannelId, string Text)> messages, CancellationToken cancellationToken)
    {
        await previous;
        foreach (var (channelId, message) in messages)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                if (client.GetChannel(channelId) is IMessageChannel channel)
                    await rateLimiter.RunAsync(channelId, () => channel.SendMessageAsync(
                        text: message, allowedMentions: AllowedMentions.None), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed sending Discord status notification to channel {ChannelId}", channelId);
            }
        }
    }

    private sealed record ServerStatus(DedicatedServerProcessState State, DateTimeOffset? RestartRequestedAtUtc,
        DateTimeOffset? StartedAtUtc, string? ConnectionId, Dictionary<(long SteamId, int SerialId), string>? Players);
}
