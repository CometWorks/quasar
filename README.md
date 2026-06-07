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
- [Docs/LinuxDeploymentAndUpdates.md](Docs/LinuxDeploymentAndUpdates.md) — Linux release assets, systemd install, and the auto-updater flow
- [Docs/WindowsDeploymentAndUpdates.md](Docs/WindowsDeploymentAndUpdates.md) — Windows release assets, Scheduled Task install, and the auto-updater flow

Build notes:

- `Quasar.Agent` depends on a local `DS64` path for Space Engineers Dedicated Server assemblies.
- A local-only override can live at `Quasar.Agent/Directory.Build.props`.
- This repo keeps the machine-specific override out of source control.
- On Windows the solution builds out-of-the-box: `Directory.Build.props` auto-resolves `DS64` from the Steam registry `InstallLocation` (falling back to the default `C:\Program Files (x86)\Steam\...\DedicatedServer64` library) and `MagnetarBin` to `$(Magnetar)\Libraries\MagnetarLegacy`. On Linux `MagnetarBin` resolves to `$(Magnetar)/Bin`.
- The Linux release workflow probes the Space Engineers Dedicated Server public build id, restores/caches only `DedicatedServer64/` by that id, and feeds the cached path to the build through `DS64`. On a cache miss it downloads the Windows depot with SteamCMD and retries the install to work around transient missing-configuration failures.

Managed runtime notes:

- On Windows, managed servers can run on either Magnetar build — .NET 10 (the "Interim" build, default) or .NET Framework 4.8 (the "Legacy" build). Pick the build per server with the `.NET runtime` field in the server editor; Quasar downloads both builds together from the latest full GitHub Magnetar release asset matching `MagnetarForWindows-*.7z` so switching never re-downloads.
- On Linux only the .NET 10 (Interim) build ships in the latest full GitHub Magnetar release asset matching `MagnetarForLinux-*.7z`; a `NetFramework48` selection carried over from a Windows `server.json` is silently downgraded to .NET 10.

Linux service install:

- `sudo ./install.sh` publishes Quasar to `/opt/quasar` and installs `quasar.service`.
- The service grants `CAP_SYS_NICE` with systemd ambient capabilities so Quasar can raise managed server priority through `renice`.
- The installer enables the service but does not start/restart it unless `--start` is passed.
- Start or restart it with `sudo systemctl restart quasar.service` when ready.
- `sudo ./uninstall.sh` removes the systemd service; add `--purge` to remove `/opt/quasar` too.

Windows service install:

- From an extracted `quasar-win-x64.zip`, run `./install.ps1` in an elevated PowerShell to install Quasar to `%ProgramFiles%\Quasar` and register a `Quasar` Scheduled Task.
- The task starts the launcher at boot (`Quasar.exe serve --quiet`) and restarts it on failure (keep-alive); a Windows Service is intentionally out of scope.
- The installer registers the task but does not start it unless `-Start` is passed.
- It runs as `SYSTEM` by default; pass `-User <name>` for a specific service account.
- `./uninstall.ps1` removes the Scheduled Task; add `-Purge` to remove the install directory too.

Linux release packaging and updates:

- `scripts/package-linux-release.sh` creates two release assets under `artifacts/linux/`:
  - `quasar-linux-x64.tar.gz` — stable launcher plus Linux install/uninstall scripts.
  - `quasar-web-linux-x64.tar.gz` — replaceable Quasar UI worker plus bundled `Quasar.Agent` DLLs.
- `SHA256SUMS` is published with those assets and is verified before Bootstrap or Quasar extracts a downloaded web artifact.
- A Bootstrap-only Linux install can start without a packaged `WebService/` folder; Bootstrap downloads the latest web asset from GitHub on startup and writes the active-release pointer.
- The Quasar UI checks GitHub releases every 5 minutes by default. New Linux UI assets are downloaded into `~/.config/Quasar/Updates/Staged/<version>` and queued for activation on the Updates page.
- Activating a staged UI update causes a short web listener disconnect: Bootstrap drains the old worker first, starts the staged worker on the same port, and managed Magnetar servers stay alive because they run detached.
- Bootstrap checks the primary Quasar release stream every 5 minutes. When `quasar-linux-x64.tar.gz` has a newer version, Bootstrap verifies `SHA256SUMS`, replaces its installed launcher files, drains the UI worker, and exits so systemd restarts the updated launcher.
- The release workflow is `.github/workflows/release-linux.yml`; tag pushes publish both Quasar UI and primary Quasar releases (`quasar-ui/v<version>` and `v<version>`). Pushes to `main` publish both streams as full releases (`quasar-ui/v0.1.0-main.<run-number>` and `v0.1.0-main.<run-number>`). Pull requests publish both streams as draft prereleases (`quasar-ui/pr-<number>/v0.1.0-pr.<number>.<run-number>` and `pr-<number>/v0.1.0-pr.<number>.<run-number>`). Manual runs can choose `all`, `ui`, or `bootstrap`. Assembly/file metadata is normalized to `major.minor.build`.

Windows release packaging and updates:

- `scripts/package-windows-release.ps1` creates the equivalent `win-x64` assets under `artifacts/windows/`:
  - `quasar-win-x64.zip` — stable `Quasar.exe` launcher plus Windows `install.ps1`/`uninstall.ps1`.
  - `quasar-web-win-x64.zip` — replaceable Quasar UI worker (`Quasar.exe`) plus bundled `Quasar.Agent` DLLs.
- `SHA256SUMS` is published with those assets and verified before Bootstrap or Quasar extracts a downloaded web artifact, exactly as on Linux.
- The auto-updater is platform-aware: it resolves the `.zip` Windows asset names on Windows and the `.tar.gz` Linux names elsewhere. On a Bootstrap self-update Windows spawns a detached replacement `Quasar.exe serve --quiet` and exits `0` (Linux still exits `75` for systemd).
- The release workflow is `.github/workflows/release-windows.yml` on `windows-latest`; it publishes to Windows-specific tags (`win/v<version>` and `quasar-ui-win/v<version>`) so each OS keeps a distinct `SHA256SUMS`. Triggers, metadata, and draft/prerelease rules mirror the Linux workflow.
- See [Docs/WindowsDeploymentAndUpdates.md](Docs/WindowsDeploymentAndUpdates.md) for details.

Agent workflow note:

- Do not launch the Quasar web service process (`dotnet run --project Quasar/Quasar.csproj`) unless the user explicitly asks for a smoketest.
- Managed agents collect default low-duty profiler telemetry for Analytics: per-grid, per-script, per-entity, physics, network/replication/session, and game-loop timing buckets.

Utilities:

- Generate synthetic analytics data for local testing:
  - `python3 scripts/generate-analytics-data.py`
  - Optional `--server <name>` to target one server, `--days <n>`, `--seed <n>`, `--raw-hours <hours>`, `--raw-interval <seconds>`.
  - Uses `QUASAR_DATA_DIR` automatically if set, otherwise defaults to the local Quasar data root.
