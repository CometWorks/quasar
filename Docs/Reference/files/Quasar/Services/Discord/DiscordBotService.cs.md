# Quasar/Services/Discord/DiscordBotService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
`IHostedService` that owns the `DiscordSocketClient` lifecycle. It starts, stops, and restarts the bot in response to options changes or agent registry changes, orchestrating all relay, alert, and export sub-services. Also exposes a status snapshot for the Quasar UI.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordBotService : IHostedService, IDisposable`

Constructor: `(DiscordOptionsCatalog, AgentRegistry, DiscordCommandRouter, DiscordChatRelayService, DiscordDeathRelayService, DiscordSimSpeedAlertService, DiscordLogRelayService, DiscordAnalyticsExportService, ILogger<DiscordBotService>)`

Events:
- `Changed : Action?` — raised whenever bot state changes (Starting, Running, Faulted, Stopped, Disabled, NotConfigured)

Public members:
- `StartAsync(CancellationToken)` — subscribes to `DiscordOptionsCatalog.Changed` and `AgentRegistry.Changed`, calls `TryRestartBotAsync`
- `StopAsync(CancellationToken)` — unsubscribes, cancels `_shutdown`, calls `StopBotCoreAsync`
- `Dispose()` — idempotent via `Interlocked.Exchange`; cancels and disposes all owned tokens
- `GetStatus() : DiscordBotStatusSnapshot` — thread-safe snapshot of enabled/token/guild/state/error flags

Private internals:
- `TryRestartBotAsync` — serialised via `SemaphoreSlim _restartGate`; stops existing bot, checks prerequisites (Enabled, BotToken, GuildId), creates `DiscordSocketClient` with `Guilds | GuildMessages | MessageContent` intents, starts relay/export sub-services, sets state
- `StopBotCoreAsync` — cancels bot-lifetime token, resets all sub-services, calls `client.StopAsync/LogoutAsync`, disposes client
- `HandleOptionsChanged` / `HandleRegistryChanged` — fire-and-forget `Task.Run` wrappers
- `HandleClientLogAsync` — maps `Discord.LogSeverity` to `Microsoft.Extensions.Logging.LogLevel`
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
- Discord.Net — `DiscordSocketClient`, `DiscordSocketConfig`, `GatewayIntents`

## Notes
Restart is fully serialised via a `SemaphoreSlim(1,1)` gate to prevent concurrent restarts from options-changed and registry-changed events firing simultaneously. All relay sub-services must implement `Reset()` before being started on the new client. Bot-lifetime cancellation is a `CancellationTokenSource` linked to the service-level `_shutdown` token, so stopping the hosted service propagates to all loops.
