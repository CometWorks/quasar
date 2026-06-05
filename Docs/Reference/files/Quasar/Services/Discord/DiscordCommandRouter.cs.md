# Quasar/Services/Discord/DiscordCommandRouter.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Handles the `MessageReceived` event from the Discord client and routes each incoming guild message to either the command dispatcher (if the message matches a configured command prefix) or the chat-relay dispatcher (if `RelayNonCommandMessages` is enabled for the channel). Acts as the entry-point gateway between Discord.Net events and Quasar's command/relay logic.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordCommandRouter`

Constructor: `(DiscordOptionsCatalog optionsCatalog, DiscordCommandDispatcher dispatcher, ILogger<DiscordCommandRouter> logger)`

Public members:
- `HandleAsync(SocketMessage message) : Task` — registered as `DiscordSocketClient.MessageReceived` handler; filters out bots, empty messages, DMs, wrong guild; then iterates configured servers to find a command-channel+prefix match and dispatches; if no command match, checks for chat-relay channels with `RelayNonCommandMessages`

Routing logic:
1. Ignore: bot authors, empty content, non-guild channels, wrong guild ID, `Enabled == false`
2. Command pass: first `DiscordServerOptions` where `CommandChannelId == channel.Id` and message starts with `CommandPrefix` (case-insensitive) — strips prefix, splits into verb+args, calls `DispatchAsync`; bare prefix → `help`
3. Chat relay pass: first `DiscordServerOptions` where `EnableChatRelay && RelayNonCommandMessages && ChatRelayChannelId == channel.Id` — calls `RelayChatAsync`

## Dependencies
- [`Quasar/Services/Discord/DiscordOptionsCatalog.cs`](DiscordOptionsCatalog.cs.md) — `DiscordOptionsCatalog`
- [`Quasar/Services/Discord/DiscordCommandDispatcher.cs`](DiscordCommandDispatcher.cs.md) — `DiscordCommandDispatcher`
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`, `DiscordServerOptions`
- Discord.Net — `SocketMessage`, `SocketGuildChannel`

## Notes
The router short-circuits at the first matching server entry for both command and relay passes. If the same channel is listed under multiple servers, only the first match is used. All exceptions are caught and logged without re-throwing, so a routing failure does not crash the Discord client event loop.
