# Quasar/Services/Discord/DiscordDeathRelayService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Relays player death events from connected SE server agents to configured Discord death channels. Deduplicates deaths by UTC-tick timestamp per server, selects a random templated message from the death-messages catalog, and prepends a random emote from the configured emote list.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordDeathRelayService`

Constructor: `(AgentRegistry registry, DeathMessagesCatalog deathMessagesCatalog, DiscordRateLimiter rateLimiter, ILogger<DiscordDeathRelayService> logger)`

Public members:
- `HandleChangedAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken) : Task` — called on agent-registry changes; for each enabled server with a death channel, collects fresh deaths via dedup, then sends each as a formatted message via `DiscordRateLimiter`
- `Reset()` — clears dedup state; called on bot stop/restart

Private internals:
- `CollectFreshDeaths(uniqueName, recentDeaths)` — lock-protected sliding-window dedup (up to 500 ticks retained per server)
- `BuildMessage(config, serverOptions, death)` — picks a random template from the catalog for `death.DeathType`, substitutes `{victim}`, `{killer}`, `{weapon}` placeholders, prepends a random emote (defaults to `"💀"` if `DeathMessageEmotes` is empty/blank)
- `ParseEmotes(string?)` — splits `DeathMessageEmotes` on commas, trims whitespace

Inner types:
- `DedupState` — `HashSet<long> Seen` + `Queue<long> Order` (bounded sliding window, max 500 entries)

## Dependencies
- [`Quasar/Services/Discord/DeathMessagesCatalog.cs`](DeathMessagesCatalog.cs.md) — `DeathMessagesCatalog`
- [`Quasar/Services/Discord/DeathMessagesConfig.cs`](DeathMessagesConfig.cs.md) — `DeathMessagesConfig`
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`, `DiscordServerOptions`
- [`Quasar/Services/Discord/DiscordRateLimiter.cs`](DiscordRateLimiter.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md) — `AgentRegistry`
- `Magnetar.Protocol.Model` — `DeathEventSnapshot`
- Discord.Net — `DiscordSocketClient`, `IMessageChannel`

## Notes
Deaths are ordered by `TimestampTicksUtc` before deduplication so that out-of-order arrivals are handled correctly. `Random.Shared` is used for both template and emote selection. The dedup window (500) is smaller than the chat-relay dedup (1000) since death events are less frequent.
