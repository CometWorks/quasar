# Quasar/Services/Discord/DiscordCommandDispatcher.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Executes Discord bot commands directed at a specific SE dedicated server. Routes the parsed verb to the appropriate action (chat, save, start, stop, restart, kick, ban, unban, promote, demote, status, help) and sends Discord replies. Also handles Discord-to-game chat relay and marks Discord-origin game chat so the outbound relay can suppress matching echoes.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordCommandDispatcher`

Constructor: `(AgentRegistry registry, DedicatedServerSupervisor supervisor, DedicatedServerCatalog serverCatalog, DiscordChatRelayService chatRelayService, ILogger<DiscordCommandDispatcher> logger)`

Public members:
- `DispatchAsync(DiscordServerOptions serverOptions, string verb, string args, SocketMessage message, CancellationToken) : Task` — `switch` on verb; handles: `chat`, `save`, `stop`, `start`, `restart`, `kick`, `ban`, `unban`, `promote`, `demote`, `status`, `help`; the `chat` verb formats text as `[Discord] <username>: <message>`, records echo suppression, then sends it; unknown verb replies with error + help embed
- `RelayChatAsync(DiscordServerOptions serverOptions, string text, SocketMessage message, CancellationToken) : Task` — sends Discord chat-channel messages as in-game chat via `ServerCommandType.SendChat` after formatting them with the Discord username and recording echo suppression

Private helpers:
- `DispatchSteamIdCommandAsync` — validates and parses `long steamId` from args, sends the given `ServerCommandType`
- `SendAgentCommandAsync` — resolves a connected agent and sends a `ServerCommandEnvelope` via `AgentRegistry.SendCommandAsync`
- `ResolveConnectedAgent(uniqueName)` — finds first connected agent matching the server name
- `BuildStatusEmbed(uniqueName) : EmbedBuilder` — assembles a rich status embed with state, agent connectivity, world name, metrics, and uptime
- `BuildHelpEmbed(serverOptions) : EmbedBuilder` — lists all supported `{prefix} <command>` forms
- `FormatDiscordGameMessage` / `ResolveDiscordAuthorName` / `NormalizeDiscordContent` — normalize whitespace and build the game-visible `[Discord] username: content` message
- `FormatUptime`, `FormatDuration` — human-readable duration formatting
- `ResolveCommandName(ServerCommandType)` — maps command type back to verb string for error messages

## Dependencies
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordServerOptions`
- [`Quasar/Services/Discord/DiscordChatRelayService.cs`](DiscordChatRelayService.cs.md) — Discord-origin echo suppression registration
- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md) — `AgentRegistry`, `AgentRuntimeState`
- `Quasar/Models/DedicatedServerSupervisor.cs` — `DedicatedServerSupervisor`, `DedicatedServerRuntimeSnapshot`
- `Quasar/Models/DedicatedServerCatalog.cs` — `DedicatedServerCatalog`
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](../Analytics/MetricsStoreService.cs.md) (indirect via `AgentRuntimeState.Snapshot.Metrics`)
- `Magnetar.Protocol.Model` — `ServerMetrics`
- `Magnetar.Protocol.Transport` — `ServerCommandEnvelope`, `ServerCommandType`
- Discord.Net — `SocketMessage`, `EmbedBuilder`, `Color`
