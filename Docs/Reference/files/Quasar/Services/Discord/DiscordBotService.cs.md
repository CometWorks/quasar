# Quasar/Services/Discord/DiscordBotService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
`IHostedService` that owns the `DiscordSocketClient` lifecycle. It starts, stops, and restarts the bot in response to options changes, tracks server/agent changes for relay dispatch, and publishes aggregate managed-server activity through the bot's Discord presence. Also exposes a status snapshot for the Quasar UI.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordBotService : IHostedService, IDisposable`

Constructor: `(DiscordOptionsCatalog, AgentRegistry, DedicatedServerSupervisor, DiscordCommandRouter, DiscordChatRelayService, DiscordDeathRelayService, DiscordSimSpeedAlertService, DiscordLogRelayService, DiscordAnalyticsExportService, ILogger<DiscordBotService>)`

Events:
- `Changed : Action?` — raised whenever bot state changes (Starting, Running, Faulted, Stopped, Disabled, NotConfigured)

Public members:
- `StartAsync(CancellationToken)` — subscribes to `DiscordOptionsCatalog.Changed`, `AgentRegistry.Changed`, and `DedicatedServerSupervisor.Changed`, calls `TryRestartBotAsync`
- `StopAsync(CancellationToken)` — unsubscribes, cancels `_shutdown`, calls `StopBotCoreAsync`
- `Dispose()` — idempotent via `Interlocked.Exchange`; cancels and disposes all owned tokens
- `GetStatus() : DiscordBotStatusSnapshot` — thread-safe snapshot of enabled/token/guild/state/error flags

Private internals:
- `TryRestartBotAsync` — serialised via `SemaphoreSlim _restartGate`; stops existing bot, checks prerequisites (Enabled, BotToken, GuildId), creates `DiscordSocketClient` with `Guilds | GuildMessages | MessageContent` intents, starts relay/export sub-services, updates Discord presence, sets state
- `StopBotCoreAsync` — cancels bot-lifetime token, resets all sub-services, calls `client.StopAsync/LogoutAsync`, disposes client
- `HandleOptionsChanged` / `HandleRegistryChanged` / `HandleSupervisorChanged` — fire-and-forget `Task.Run` wrappers; registry changes also drive chat/death/simspeed relays, supervisor changes update presence only
- `HandleClientLogAsync` — maps `Discord.LogSeverity` to `Microsoft.Extensions.Logging.LogLevel`
- `UpdatePresenceAsync` — serialised via `_presenceGate`; computes a `PresenceSnapshot`, skips unchanged status/activity pairs, and calls Discord.Net `SetStatusAsync` plus `SetGameAsync(..., ActivityType.Watching)`
- `BuildPresenceSnapshot` / `BuildActivityText` — aggregate supervisor snapshots and connected agent metrics into a presence state: DND for crashed/faulted/unhealthy instances, online when any instance is active, idle when none are active; activity text shows active/total servers, player count, and issue/warning counts
- `SetState(stateText, lastError)` — lock-guarded, fires `Changed`

**`DiscordBotStatusSnapshot`** — immutable record-like DTO:
- `Enabled`, `TokenConfigured`, `GuildConfigured`, `IsRunning`, `StateText`, `LastError`

## Dependencies
- [`Quasar/Services/Discord/DiscordOptionsCatalog.cs`](DiscordOptionsCatalog.cs.md)
- [`Quasar/Services/Discord/DiscordCommandRouter.cs`](DiscordCommandRouter.cs.md)
- [`Quasar/Services/Discord/DiscordChatRelayService.cs`](DiscordChatRelayService.cs.md)
- [`Quasar/Services/Discord/DiscordDeathRelayService.cs`](DiscordDeathRelayService.cs.md)
- [`Quasar/Services/Discord/DiscordSimSpeedAlertService.cs`](DiscordSimSpeedAlertService.cs.md)
- [`Quasar/Services/Discord/DiscordLogRelayService.cs`](DiscordLogRelayService.cs.md)
- [`Quasar/Services/Discord/DiscordAnalyticsExportService.cs`](DiscordAnalyticsExportService.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md) — `AgentRegistry`
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../DedicatedServerSupervisor.cs.md) — runtime snapshots and supervisor change events for Discord presence
- [`Quasar/Models/DedicatedServerRuntimeSnapshot.cs`](../../Models/DedicatedServerRuntimeSnapshot.cs.md), [`DedicatedServerProcessState`](../../Models/DedicatedServerProcessState.cs.md), [`DedicatedServerHealthState`](../../Models/DedicatedServerHealthState.cs.md)
- Discord.Net — `DiscordSocketClient`, `DiscordSocketConfig`, `GatewayIntents`

## Notes
Restart is fully serialised via a `SemaphoreSlim(1,1)` gate to prevent concurrent restarts from options-changed and registry-changed events firing simultaneously. Presence updates are separately serialised and content-deduplicated so frequent agent snapshots do not spam Discord presence API calls. All relay sub-services must implement `Reset()` before being started on the new client. Bot-lifetime cancellation is a `CancellationTokenSource` linked to the service-level `_shutdown` token, so stopping the hosted service propagates to all loops.
