# Quasar/Services/QuasarLoggingConfigurator.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Static configurator that wires NLog into the ASP.NET Core host at startup. It builds an NLog `LoggingConfiguration` with the main Quasar file target, a dedicated Discord integration file target, and an optional console target (activated when `QUASAR_CONSOLE_LOGGING=true`), then replaces the default Microsoft logging providers with NLog via `UseNLog()`.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarLoggingConfigurator` (static class)

| Member | Description |
|---|---|
| `ResolveDiscordLogPath(options)` | Returns the configured `discord.log` path, falling back to the standard Quasar log directory when needed. |
| `Configure(builder, options)` | Entry point called from `Program.cs`; creates log directory, assigns `LogManager.Configuration`, clears default providers, hooks NLog. |
| `BuildConfiguration(options)` (private) | Constructs the `LoggingConfiguration` with main file, Discord file, and optional console targets. |
| `BuildLayout(loggingFormat)` (private) | Returns `JsonLayout` when `loggingFormat == "json"`, otherwise a plain `SimpleLayout` with timestamp/level/thread/logger/message+exception. |
| `ParseMinimumLevel(value)` (private) | Parses NLog level name string; falls back to `Info`. |
| `IsConsoleLoggingRequested()` (private) | Reads `QUASAR_CONSOLE_LOGGING` env var; accepts `"true"` or `"1"`. |

## Dependencies
- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md) (supplies `LoggingDirectory`, `LoggingFormat`, `LoggingMinimumLevel`)
- `Magnetar.Protocol.Runtime.MagnetarPaths` (fallback log directory)
- NLog, NLog.Web (external packages)

## Notes
- Console logging is intended for the interactive-terminal launch path; the Quasar.Bootstrap launcher sets `QUASAR_CONSOLE_LOGGING=true` so log output passes through to the user's terminal via pipe.
- Main log file is always named `quasar.log` inside the configured logging directory; Discord integration log file is `discord.log`.
- The `Quasar.Services.Discord.*` logger rule writes Discord-side service and Discord.Net client messages to the dedicated file while preserving the normal application log rule.
- JSON layout emits five fields: `timestamp`, `level`, `logger`, `message`, `exception`.
