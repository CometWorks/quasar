# Quasar

Quasar is a supervisor and management stack for **Space Engineers** (version 1)
dedicated servers. It runs as a Blazor Server web app that supervises multiple DS
processes on a single host — starting, stopping, health-checking, configuring, and
auto-updating them through goal-state reconciliation — while an in-process plugin
(`Quasar.Agent`) attaches to each server to report telemetry and execute commands.

It runs on **Linux** (systemd service) and **Windows** (Scheduled Task), in
foreground console or unattended background mode.

Each server uses the **[Magnetar](https://github.com/viktor-ferenczi/Magnetar)** plugin loader and launcher.
Quasar deploys an agent plugin which connects back to Quasar.

Quasar downloads Magnetar and the Dedicated Server builds automatically and caches it locally until there is an update.

You can register new plugins by making PRs to the [MagnetarHub](https://github.com/viktor-ferenczi/MagnetarHub).

<!-- BEGIN packaged install instructions -->
## Getting started

See the [Quick Start](Docs/QuickStart.md) guide to download a release, run Quasar
from the terminal, and install it as a background service.
<!-- END packaged install instructions -->

## Documentation

| Page | What it covers |
| --- | --- |
| [Quick Start](Docs/QuickStart.md) | Download, run from the terminal, and install as a background service (systemd / Scheduled Task). |
| [Architecture](Docs/QuasarArchitecture.md) | Supervisor design, runtime ownership, process supervision, configuration model, and self-update. |
| [Configuration](Docs/Configuration.md) | Web UI host/port (how to change the listening port) and browser auto-open behavior. |
| [Building & Development](Docs/BuildingAndDevelopment.md) | Project layout, build setup, managed-runtime selection, and developer utilities. |
| [Linux Deployment & Updates](Docs/LinuxDeploymentAndUpdates.md) | systemd install, release assets, and the auto-updater flow. |
| [Windows Deployment & Updates](Docs/WindowsDeploymentAndUpdates.md) | Scheduled Task install, release assets, and the auto-updater flow. |

For the full per-file and per-module code reference, see the generated
[code handbook (`Docs/Reference/TOC.md`)](Docs/Reference/TOC.md) — auto-generated
and maintained from source, with a flat [Index](Docs/Reference/Index.md).
