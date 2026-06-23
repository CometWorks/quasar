# Quasar.Bootstrap — Ensure-Running Helper

*Module `Quasar.Bootstrap` — 3 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

A small `net10.0` helper whose job is to make sure the Quasar web service is running. It exposes `ensure-running`, `serve` and `activate-release` commands, guards startup with a named mutex, and (in `serve` mode) manages the Quasar worker process lifecycle: start, health-wait, graceful drain via `/api/internal/drain`, hot reload when the active-release pointer changes, and auto-restart on unexpected exit. It can be invoked manually or by [Quasar.Agent](Quasar.Agent.md) when the supervisor is missing, and shares the [Magnetar.Protocol](Magnetar.Protocol.md) discovery/release contracts.

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar.Bootstrap/Program.cs](../files/Quasar.Bootstrap/Program.cs.md) | class | Entry point and core logic for the Quasar launcher. It implements three CLI commands (`ensure-running`, `serve`, `activate-release`), applies the install-root default data directory and legacy AppData migration before any path lookup, and includes the supporting types `BootstrapOptions` (host/port + update + preserve-servers policy from `appsettings.json`) and `LauncherCoordinator` (an `IHostedService` that supervises the Quasar worker process, watches the active-release pointer and Bootstrap update request files, downloads an initial UI worker from the UI release stream when needed, hot-reloads activated UI releases, and self-upgrades the launcher from the primary Quasar release stream). |
| [Quasar.Bootstrap/Properties/launchSettings.json](../files/Quasar.Bootstrap/Properties/launchSettings.json.md) | JSON config | Visual Studio / `dotnet run` launch settings for the Bootstrap project. Defines a single `Dev` profile that invokes `ensure-running --open-browser` under `ASPNETCORE_ENVIRONMENT=Development`, so a developer can start the full supervisor stack (and have the browser open automatically) with a single run. |
| [Quasar.Bootstrap/Quasar.Bootstrap.csproj](../files/Quasar.Bootstrap/Quasar.Bootstrap.csproj.md) | project file | MSBuild project file for `Quasar.Bootstrap`, a `net10.0` console executable that targets `linux-x64` and `win-x64`. RID-targeted publish restores and publishes the `Quasar` worker as a single-file sub-app into a `WebService/` subfolder. |

## Depends on

- [Magnetar.Protocol](Magnetar.Protocol.md)
- [Quasar.Host](Quasar.Host.md)
