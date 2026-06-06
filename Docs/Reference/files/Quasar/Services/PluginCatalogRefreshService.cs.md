# Quasar/Services/PluginCatalogRefreshService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Background hosted service that periodically refreshes the Quasar plugin catalog so its cached plugin/manifest data stays current without user action. After a short startup delay it refreshes once, then refreshes on a fixed interval. Refresh failures are logged and the previously cached catalog is kept as a fallback.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `PluginCatalogRefreshService` — sealed class deriving from `BackgroundService`. ctor injects `QuasarPluginCatalogService` and `ILogger`.

| Member | Description |
|---|---|
| `StartupDelay` (static) | 2 seconds before the first refresh. |
| `RefreshInterval` (static) | 8 hours between subsequent refreshes. |
| `ExecuteAsync(stoppingToken)` (override) | Delays `StartupDelay`, runs `RefreshOnceAsync`, then loops on a `PeriodicTimer(RefreshInterval)` calling `RefreshOnceAsync` per tick; swallows `OperationCanceledException` on shutdown. |
| `RefreshOnceAsync(ct)` (private) | Calls `_pluginCatalog.RefreshAsync`; rethrows `OperationCanceledException`, logs and suppresses any other exception (keeps the cached catalog). |

## Dependencies
- `Quasar/Services/QuasarPluginCatalogService.cs` — `RefreshAsync` (refresh target)
- `Microsoft.Extensions.Hosting.BackgroundService`

## Notes
`RefreshAsync` itself already records `LastError` and logs; this service only adds a warning when a scheduled refresh fails so the existing cached catalog remains in use. Cancellation is treated as a clean shutdown, not an error.
