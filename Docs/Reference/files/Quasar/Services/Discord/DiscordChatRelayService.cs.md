# Quasar/Services/Discord/DiscordChatRelayService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Relays in-game chat messages from connected SE server agents to configured Discord channels. Deduplicates messages by UTC-tick timestamp per server and delivers them through a per-channel bounded queue with a 500 ms inter-message delay to avoid flooding.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordChatRelayService`

Constructor: `(AgentRegistry registry, DiscordRateLimiter rateLimiter, ILogger<DiscordChatRelayService> logger)`

Public members:
- `HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken) : Task` — called on agent-registry changes; for each enabled server with a chat-relay channel, collects fresh messages via dedup and enqueues them
- `Reset()` — clears dedup state and per-channel queue/consumer state; called on bot stop/restart

Private internals:
- `CollectFreshMessages(uniqueName, recentChat)` — lock-protected sliding-window dedup (up to 1000 ticks retained per server); formats messages as `"**AuthorName**: content"`
- `Enqueue(client, channelId, message, ct)` — lazily creates a `RelayChannelState` per channel and starts a consumer task if needed
- `ConsumeAsync(client, channelId, state, ct)` — reads from the bounded channel, sends via `DiscordRateLimiter`, then delays 500 ms between messages

Inner types:
- `DedupState` — `HashSet<long> Seen` + `Queue<long> Order` for bounded dedup window
- `RelayChannelState` — `Channel<string>` (bounded 20, `DropOldest`, single-reader) + `Task? ConsumerTask`

## Dependencies
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`, `DiscordServerOptions`
- [`Quasar/Services/Discord/DiscordRateLimiter.cs`](DiscordRateLimiter.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md) — `AgentRegistry`, `AgentRuntimeState`
- `Magnetar.Protocol.Model` — `ChatMessageSnapshot`
- Discord.Net — `DiscordSocketClient`, `IMessageChannel`
- `System.Threading.Channels`

## Notes
The dedup window is per-server, keyed by `UniqueName`. If a consumer task is never cancelled (e.g. bot is restarted), `Reset()` drops the `RelayChannelState` reference and the old consumer naturally exits when the bot-lifetime token is cancelled. The bounded channel with `DropOldest` silently discards the oldest undelivered message if the queue of 20 fills up.
