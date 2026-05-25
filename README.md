# Quasar

Supervisor and management stack for Space Engineers dedicated server instances.

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

Current architecture reference:

- [Docs/QuasarArchitecture.md](Docs/QuasarArchitecture.md)

Build notes:

- `Quasar.Agent` depends on a local `DS64` path for Space Engineers Dedicated Server assemblies.
- A local-only override can live at `Quasar.Agent/Directory.Build.props`.
- This repo keeps the machine-specific override out of source control.
