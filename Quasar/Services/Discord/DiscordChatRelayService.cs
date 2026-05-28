using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Magnetar.Protocol.Model;

namespace Quasar.Services.Discord;

public sealed class DiscordChatRelayService
{
    private static readonly TimeSpan ConsumerDelay = TimeSpan.FromMilliseconds(500);
    private readonly object _sync = new();
    private readonly AgentRegistry _registry;
    private readonly DiscordRateLimiter _rateLimiter;
    private readonly ILogger<DiscordChatRelayService> _logger;
    private readonly Dictionary<string, DedupState> _dedup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, RelayChannelState> _channelStates = new();

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

        foreach (var instanceOptions in options.Instances.Where(instance =>
                     instance.EnableChatRelay &&
                     instance.ChatRelayChannelId.HasValue))
        {
            var agent = agents.FirstOrDefault(item =>
                item.IsConnected &&
                item.Snapshot is not null &&
                string.Equals(item.InstanceKey, instanceOptions.InstanceId, StringComparison.OrdinalIgnoreCase));

            if (agent?.Snapshot is null)
                continue;

            var freshMessages = CollectFreshMessages(instanceOptions.InstanceId, agent.Snapshot.RecentChat);
            foreach (var freshMessage in freshMessages)
                Enqueue(client, instanceOptions.ChatRelayChannelId!.Value, freshMessage, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _dedup.Clear();
            _channelStates.Clear();
        }
    }

    private IReadOnlyList<string> CollectFreshMessages(string instanceId, IReadOnlyList<ChatMessageSnapshot> recentChat)
    {
        if (recentChat.Count == 0)
            return [];

        lock (_sync)
        {
            if (!_dedup.TryGetValue(instanceId, out var dedupState))
            {
                dedupState = new DedupState();
                _dedup[instanceId] = dedupState;
            }

            var fresh = new List<string>();
            foreach (var message in recentChat.OrderBy(item => item.TimestampTicksUtc))
            {
                if (!dedupState.Seen.Add(message.TimestampTicksUtc))
                    continue;

                dedupState.Order.Enqueue(message.TimestampTicksUtc);
                while (dedupState.Order.Count > 1000)
                {
                    var expired = dedupState.Order.Dequeue();
                    dedupState.Seen.Remove(expired);
                }

                var author = string.IsNullOrWhiteSpace(message.AuthorName) ? "Unknown" : message.AuthorName.Trim();
                var content = string.IsNullOrWhiteSpace(message.Content) ? string.Empty : message.Content.Trim();
                fresh.Add($"**{author}**: {content}");
            }

            return fresh;
        }
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
