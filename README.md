# Quasar

Quasar is an API-first supervisor and management stack for **Space Engineers** (version 1)
dedicated servers, with an optional Blazor Server UI. It supervises multiple DS
processes on a single host — starting, stopping, health-checking, configuring, and
auto-updating them through goal-state reconciliation — while an in-process plugin
(`Quasar.Agent`) attaches to each server to report telemetry and execute commands.

It runs on **Linux** (systemd service) and **Windows** (Scheduled Task), in
foreground console or unattended background mode.

Each server uses the **[Magnetar](https://github.com/CometWorks/magnetar)** plugin loader and launcher.
Quasar deploys an agent plugin which connects back to Quasar.

Quasar downloads Magnetar and the Dedicated Server builds automatically and caches it locally until there is an update.

You can register new plugins by making PRs to the [MagnetarHub](https://github.com/CometWorks/magnetar-hub).
Quasar UI plugins are discovered and managed through [QuasarHub](https://github.com/CometWorks/quasar-hub).

<!-- BEGIN packaged install instructions -->
## Getting started

See the [Quick Start](Docs/QuickStart.md) guide to download a release, run Quasar
from the terminal, install it as a background service, or run the GHCR image.
<!-- END packaged install instructions -->

## Documentation

| Page | What it covers |
| --- | --- |
| [Quick Start](Docs/QuickStart.md) | Download, run from the terminal, and install as a background service (systemd / Scheduled Task). |
| [Docker Deployment](Docs/Docker.md) | Run the versioned GHCR image with Compose, persistent state, environment configuration, and upgrades. |
| [Architecture](Docs/QuasarArchitecture.md) | Supervisor design, runtime ownership, process supervision, configuration model, and self-update. |
| [Configuration](Docs/Configuration.md) | API/UI host and port, API-only headless mode, and browser auto-open behavior. |
| [Quasar Plugin System](Docs/QuasarPluginSystem.md) | Planned UI plugin loader, hub manifest model, component replacement points, companion data channel, and MudBlazor expectations. |
| [Entity Viewer](https://github.com/CometWorks/viewer/blob/main/Docs/EntityViewer.md) | Fullscreen metadata-only entity viewer, local Space Engineers `Content` folder requirement, and fallback behavior. |
| [Building & Development](Docs/BuildingAndDevelopment.md) | Project layout, build setup, managed-runtime selection, and developer utilities. |
| [Linux Deployment & Updates](Docs/LinuxDeploymentAndUpdates.md) | systemd install, release assets, and the auto-updater flow. |
| [Windows Deployment & Updates](Docs/WindowsDeploymentAndUpdates.md) | Scheduled Task install, release assets, and the auto-updater flow. |
| [State Machine Diagrams](Docs/StateMachines/Index.md) | Object states and state machines (server lifecycle, agent connection, self-update, runtime provisioning, backups, …) as Mermaid + PNG. |
