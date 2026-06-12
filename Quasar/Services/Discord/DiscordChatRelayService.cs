using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Magnetar.Protocol.Model;

namespace Quasar.Services.Discord;

public sealed class DiscordChatRelayService
{
    private static readonly TimeSpan ConsumerDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DiscordEchoSuppressionWindow = TimeSpan.FromMinutes(2);
    private readonly object _sync = new();
    private readonly AgentRegistry _registry;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordChatRelayService> _logger;
    private readonly Dictionary<string, DedupState> _dedup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SuppressedMessage>> _suppressedDiscordEchoes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, RelayChannelState> _channelStates = new();
    private long _relayStartedTicksUtc = DateTimeOffset.UtcNow.UtcTicks;

    public DiscordChatRelayService(
        AgentRegistry registry,
        DiscordRateLimiter rateLimiter,
        ILogger<DiscordChatRelayService> logger)
    {
        _registry = registry;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public Task HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var agents = _registry.GetAgents();

        foreach (var serverOptions in options.Servers.Where(server =>
                     server.EnableChatRelay &&
                     server.ChatRelayChannelId.HasValue))
        {
            var agent = agents.FirstOrDefault(item =>
                item.IsConnected &&
                item.Snapshot is not null &&
                string.Equals(item.UniqueNameKey, serverOptions.UniqueName, StringComparison.OrdinalIgnoreCase));

            if (agent?.Snapshot is null)
                continue;

            var freshMessages = CollectFreshMessages(serverOptions.UniqueName, agent.Snapshot.RecentChat);
            foreach (var freshMessage in freshMessages)
                Enqueue(client, serverOptions.ChatRelayChannelId!.Value, freshMessage, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _dedup.Clear();
            _suppressedDiscordEchoes.Clear();
            _channelStates.Clear();
            _relayStartedTicksUtc = DateTimeOffset.UtcNow.UtcTicks;
        }
    }

    public void TrackDiscordToGameMessage(string uniqueName, string content)
    {
        if (string.IsNullOrWhiteSpace(uniqueName) || string.IsNullOrWhiteSpace(content))
            return;

        var normalizedContent = NormalizeContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
            return;

        lock (_sync)
        {
            if (!_suppressedDiscordEchoes.TryGetValue(uniqueName, out var messages))
            {
                messages = [];
                _suppressedDiscordEchoes[uniqueName] = messages;
            }

            PruneSuppressedMessages(messages);
            messages.Add(new SuppressedMessage(normalizedContent, DateTimeOffset.UtcNow + DiscordEchoSuppressionWindow));

            if (messages.Count > 100)
                messages.RemoveRange(0, messages.Count - 100);
        }
    }

    private IReadOnlyList<string> CollectFreshMessages(string uniqueName, IReadOnlyList<ChatMessageSnapshot> recentChat)
    {
        lock (_sync)
        {
            if (!_dedup.TryGetValue(uniqueName, out var dedupState))
            {
                dedupState = new DedupState();
                _dedup[uniqueName] = dedupState;
            }

            if (recentChat.Count == 0)
                return [];

            var fresh = new List<string>();
            foreach (var message in recentChat.OrderBy(item => item.TimestampTicksUtc))
            {
                if (!AddSeen(dedupState, message.TimestampTicksUtc))
                    continue;

                if (message.TimestampTicksUtc <= _relayStartedTicksUtc)
                    continue;

                if (message.SteamId == 0 && TryConsumeSuppressedDiscordEcho(uniqueName, message.Content))
                    continue;

                var author = string.IsNullOrWhiteSpace(message.AuthorName) ? "Unknown" : message.AuthorName.Trim();
                var content = string.IsNullOrWhiteSpace(message.Content) ? string.Empty : message.Content.Trim();
                fresh.Add($"**{author}**: {content}");
            }

            return fresh;
        }
    }

    private static bool AddSeen(DedupState dedupState, long timestampTicksUtc)
    {
        if (!dedupState.Seen.Add(timestampTicksUtc))
            return false;

        dedupState.Order.Enqueue(timestampTicksUtc);
        while (dedupState.Order.Count > 1000)
        {
            var expired = dedupState.Order.Dequeue();
            dedupState.Seen.Remove(expired);
        }

        return true;
    }

    private bool TryConsumeSuppressedDiscordEcho(string uniqueName, string content)
    {
        if (!_suppressedDiscordEchoes.TryGetValue(uniqueName, out var messages))
            return false;

        PruneSuppressedMessages(messages);
        if (messages.Count == 0)
            return false;

        var normalizedContent = NormalizeContent(content);
        var index = messages.FindIndex(message =>
            string.Equals(message.Content, normalizedContent, StringComparison.Ordinal));
        if (index < 0)
            return false;

        messages.RemoveAt(index);
        return true;
    }

    private static void PruneSuppressedMessages(List<SuppressedMessage> messages)
    {
        var now = DateTimeOffset.UtcNow;
        messages.RemoveAll(message => message.ExpiresAtUtc <= now);
    }

    private static string NormalizeContent(string content)
    {
        return (content ?? string.Empty).Trim();
    }

    private void Enqueue(DiscordSocketClient client, ulong channelId, string message, CancellationToken cancellationToken)
    {
        RelayChannelState state;
        lock (_sync)
        {
            if (!_channelStates.TryGetValue(channelId, out state!))
            {
                state = new RelayChannelState();
                state.ConsumerTask = Task.Run(() => ConsumeAsync(client, channelId, state, cancellationToken), CancellationToken.None);
                _channelStates[channelId] = state;
            }
        }

        state.Queue.Writer.TryWrite(message);
    }

    private async Task ConsumeAsync(
        DiscordSocketClient client,
        ulong channelId,
        RelayChannelState state,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await state.Queue.Reader.WaitToReadAsync(cancellationToken))
            {
                while (state.Queue.Reader.TryRead(out var payload))
                {
                    try
                    {
                        if (client.GetChannel(channelId) is not IMessageChannel channel)
                            continue;

                        await _rateLimiter.RunAsync(channelId, () => channel.SendMessageAsync(text: payload), cancellationToken);
                        await Task.Delay(ConsumerDelay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed sending Discord chat relay to channel {ChannelId}", channelId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class DedupState
    {
        public HashSet<long> Seen { get; } = [];

        public Queue<long> Order { get; } = new();
    }

    private sealed record SuppressedMessage(string Content, DateTimeOffset ExpiresAtUtc);

    private sealed class RelayChannelState
    {
        public Channel<string> Queue { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        public Task? ConsumerTask { get; set; }
    }
}
