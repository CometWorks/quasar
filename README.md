# Quasar

Supervisor and management stack for Space Engineers dedicated servers.

Projects:

- `Quasar`  
  Blazor Server supervisor host, DS process manager, config/runtime preparation, and WebSocket endpoint for agents.
- `Quasar.Agent`  
  Dedicated Server plugin that attaches to Quasar and exposes telemetry/commands.
- `Quasar.Bootstrap`  
  Ensure-running helper used for Quasar startup/bootstrap flow.
- `Magnetar.Protocol`  
  Shared transport and discovery contracts currently used by Quasar and Quasar.Agent.

Solution:

- `Quasar.sln`

Documentation:

- [Docs/Reference/TOC.md](Docs/Reference/TOC.md) — generated code handbook (per-file and per-module reference, with a flat [Index](Docs/Reference/Index.md))
- [Docs/QuasarArchitecture.md](Docs/QuasarArchitecture.md) — architecture narrative and design rationale

Build notes:

- `Quasar.Agent` depends on a local `DS64` path for Space Engineers Dedicated Server assemblies.
- A local-only override can live at `Quasar.Agent/Directory.Build.props`.
- This repo keeps the machine-specific override out of source control.
- On Windows the solution builds out-of-the-box: `Directory.Build.props` auto-resolves `DS64` from the Steam registry `InstallLocation` (falling back to the default `C:\Program Files (x86)\Steam\...\DedicatedServer64` library) and `MagnetarBin` to `$(Magnetar)\Libraries\MagnetarLegacy`. On Linux `MagnetarBin` resolves to `$(Magnetar)/Bin`.

Managed runtime notes:

- On Windows, managed servers can run on either Magnetar build — .NET 10 (the "Interim" build, default) or .NET Framework 4.8 (the "Legacy" build). Pick the build per server with the `.NET runtime` field in the server editor; Quasar downloads both builds together from `MagnetarForWindows.7z` so switching never re-downloads.
- On Linux only the .NET 10 (Interim) build ships (`MagnetarForLinux.7z`); a `NetFramework48` selection carried over from a Windows `server.json` is silently downgraded to .NET 10.

Linux service install:

- `sudo ./install.sh` publishes Quasar to `/opt/quasar` and installs `quasar.service`.
- The service grants `CAP_SYS_NICE` with systemd ambient capabilities so Quasar can raise managed server priority through `renice`.
- The installer enables the service but does not start/restart it unless `--start` is passed.
- Start or restart it with `sudo systemctl restart quasar.service` when ready.
- `sudo ./uninstall.sh` removes the systemd service; add `--purge` to remove `/opt/quasar` too.

Linux release packaging and updates:

- `scripts/package-linux-release.sh` creates two release assets under `artifacts/linux/`:
  - `quasar-bootstrap-linux-x64.tar.gz` — stable launcher plus Linux install/uninstall scripts.
  - `quasar-web-linux-x64.tar.gz` — replaceable Quasar UI worker plus bundled `Quasar.Agent` DLLs.
- `SHA256SUMS` is published with those assets and is verified before Bootstrap or Quasar extracts a downloaded web artifact.
- A Bootstrap-only Linux install can start without a packaged `WebService/` folder; Bootstrap downloads the latest web asset from GitHub on startup and writes the active-release pointer.
- The Quasar UI checks GitHub releases every 5 minutes by default. New Linux UI assets are downloaded into `~/.config/Quasar/Updates/Staged/<version>` and queued for activation on the Updates page.
- Activating a staged UI update causes a short web listener disconnect: Bootstrap drains the old worker first, starts the staged worker on the same port, and managed Magnetar servers stay alive because they run detached.
- Bootstrap update availability is shown in the Updates page, but installing a new Bootstrap still uses the Linux installer path because service replacement may require root/systemd access.
- The release workflow is `.github/workflows/release-linux.yml`; tag pushes attach the Linux assets and `SHA256SUMS` to the GitHub release.

Agent workflow note:

- Do not launch the Quasar web service process (`dotnet run --project Quasar/Quasar.csproj`) unless the user explicitly asks for a smoketest.

Utilities:

- Generate synthetic analytics data for local testing:
  - `python3 scripts/generate-analytics-data.py`
  - Optional `--server <name>` to target one server, `--days <n>`, `--seed <n>`, `--raw-hours <hours>`, `--raw-interval <seconds>`.
  - Uses `QUASAR_DATA_DIR` automatically if set, otherwise defaults to the local Quasar data root.
