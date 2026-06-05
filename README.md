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

Linux service install:

- `sudo ./install.sh` publishes Quasar to `/opt/quasar` and installs `quasar.service`.
- The service grants `CAP_SYS_NICE` with systemd ambient capabilities so Quasar can raise managed server priority through `renice`.
- The installer enables the service but does not start/restart it unless `--start` is passed.
- Start or restart it with `sudo systemctl restart quasar.service` when ready.
- `sudo ./uninstall.sh` removes the systemd service; add `--purge` to remove `/opt/quasar` too.

Agent workflow note:

- Do not launch the Quasar web service process (`dotnet run --project Quasar/Quasar.csproj`) unless the user explicitly asks for a smoketest.
