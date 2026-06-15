# Quasar/Services/Discord/DiscordChatRelayService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Relays player-authored in-game chat messages from connected SE server agents to configured Discord channels. Deduplicates messages by UTC-tick timestamp per server, seeds each server's dedup state from the first observed agent snapshot so existing `GlobalChatHistory` is not replayed on attach, suppresses Discord-origin server echoes, skips dedicated-server/Good.bot messages so bot broadcasts do not loop back into Discord, and delivers new messages through a per-channel bounded queue with a 500 ms inter-message delay to avoid flooding.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordChatRelayService`

Constructor: `(AgentRegistry registry, DiscordRateLimiter rateLimiter, ILogger<DiscordChatRelayService> logger)`

Public members:
- `HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken) : Task` — called on agent-registry changes; for each enabled server with a chat-relay channel, collects fresh messages via dedup and enqueues them
- `Reset()` — clears dedup state and per-channel queue/consumer state; called on bot stop/restart
- `TrackDiscordToGameMessage(uniqueName, content)` — records Discord-origin text sent into the game so the outbound relay can suppress the matching server-authored echo when it appears in the next agent snapshot

Private internals:
- `CollectFreshMessages(uniqueName, recentChat)` — lock-protected sliding-window dedup (up to 1000 ticks retained per server); on the first observed snapshot for a server, marks the current chat history seen and returns no relay messages; after that, skips messages at or before the latest relay reset timestamp, skips `IsServerMessage`, SteamId 0, `Good.bot`, and `Server` authors, suppresses tracked Discord-origin echoes by normalized content regardless of the in-game author/SteamId, and formats remaining player messages as `"**AuthorName**: content"`
- `AddSeen(dedupState, timestampTicksUtc)` — records one chat timestamp in the bounded dedup window
- `IsServerAuthoredMessage` / `IsServerAuthorName` — identify server/Good.bot messages that must not be relayed to Discord
- `TryConsumeSuppressedDiscordEcho(uniqueName, content)` / `PruneSuppressedMessages(...)` / `NormalizeContent(...)` — maintain a short-lived list of Discord-origin chat content used for echo suppression
- `Enqueue(client, channelId, message, ct)` — lazily creates a `RelayChannelState` per channel and starts a consumer task if needed
- `ConsumeAsync(client, channelId, state, ct)` — reads from the bounded channel, sends via `DiscordRateLimiter`, then delays 500 ms between messages

Inner types:
- `DedupState` — `HashSet<long> Seen` + `Queue<long> Order` for bounded dedup window, plus `HasObservedSnapshot` to suppress first-snapshot history replay
- `SuppressedMessage` — normalized content plus UTC expiry used for Discord-to-game echo suppression
- `RelayChannelState` — `Channel<string>` (bounded 20, `DropOldest`, single-reader) + `Task? ConsumerTask`

## Dependencies
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`, `DiscordServerOptions`
- [`Quasar/Services/Discord/DiscordRateLimiter.cs`](DiscordRateLimiter.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md) — `AgentRegistry`, `AgentRuntimeState`
- `Magnetar.Protocol.Model` — `ChatMessageSnapshot`
- Discord.Net — `DiscordSocketClient`, `IMessageChannel`
- `System.Threading.Channels`

## Notes
The dedup window is per-server, keyed by `UniqueName`. First-snapshot seeding prevents a delayed or reconnecting agent from replaying all `GlobalChatHistory` rows created while the agent was not attached. `Reset()` also advances the relay-start timestamp, so old rows already present in agent snapshots are marked seen but not posted to Discord during bot startup. If a consumer task is never cancelled (e.g. bot is restarted), `Reset()` drops the `RelayChannelState` reference and the old consumer naturally exits when the bot-lifetime token is cancelled. The bounded channel with `DropOldest` silently discards the oldest undelivered message if the queue of 20 fills up.
