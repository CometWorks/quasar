# Quasar/Services/Discord/DiscordAnalyticsExportService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Periodically exports per-server analytics as Discord embeds to configured analytics channels. One background loop runs per enabled Discord server entry; each loop reads 1-minute metric samples from the metrics store and posts a rich embed with simspeed, CPU, memory, player, PCU, grid, and entity counts.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordAnalyticsExportService`

Constructor: `(MetricsStoreService metricsStore, DedicatedServerSupervisor supervisor, DiscordRateLimiter rateLimiter, ILogger<DiscordAnalyticsExportService> logger)`

Public members:
- `StartAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken) : Task` — spawns one `Task.Run` loop per server entry where `EnableAnalyticsExport && AnalyticsChannelId.HasValue`; clears any prior tasks first
- `Reset()` — clears the task list (called on bot stop)

Private internals:
- `RunLoopAsync(client, serverOptions, ct)` — `PeriodicTimer` at `Max(1, AnalyticsExportIntervalMinutes)` minutes; calls `ExportAsync` on each tick
- `ExportAsync(client, serverOptions, ct)` — reads `store.OneMinute.ReadLatest(intervalMinutes)`, builds a `Discord.EmbedBuilder` with averages and latest values, sends via `DiscordRateLimiter`
- `Average(samples, selector)` — simple mean over a sample list
- `FormatUptime(snapshot)` — formats `StartedAtUtc` duration as `Xd Yh Zm` / `Xh Ym` / `Xm Ys` / `Xs`

## Dependencies
- [`Quasar/Services/Discord/DiscordRateLimiter.cs`](DiscordRateLimiter.cs.md) — rate-limited send
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`, `DiscordServerOptions`
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](../Analytics/MetricsStoreService.cs.md) — `MetricsStoreService`, `MetricSample`
- `Quasar/Models/DedicatedServerSupervisor.cs` — `DedicatedServerSupervisor`, `DedicatedServerRuntimeSnapshot`
- Discord.Net — `DiscordSocketClient`, `EmbedBuilder`, `IMessageChannel`

## Notes
The task list (`_tasks`) is guarded by `_sync` but individual loop tasks are fire-and-forget; the only cancellation mechanism is the `CancellationToken` passed from `DiscordBotService`. `Reset()` drops references to old tasks without awaiting them.
