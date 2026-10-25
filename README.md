# Quasar

**Urgent security upgrade:** DIS-0042 requires saving worlds, updating Hosts and restarting game servers. Read the [upgrade order and machine endpoint policy](Docs/SecurityUpgradeDIS0042.md) before activation.

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
Plugins that are not in MagnetarHub can come from your own [plugin hubs](Docs/Configuration.md#plugin-hubs).
Quasar UI plugins are discovered and managed through [QuasarHub](https://github.com/CometWorks/quasar-hub).

<!-- BEGIN packaged install instructions -->
## Getting started

See the [Quick Start](Docs/QuickStart.md) guide to download a release, run Quasar
from the terminal, install it as a background service, or run the GHCR image.
<!-- END packaged install instructions -->

Viewers have read-only access to every built-in Control Plane page. Editing,
server commands, and administration retain separate permissions; credentials and
raw plugin settings remain restricted. See [RBAC enforcement](Docs/Configuration.md#rbac-enforcement).

## Documentation

| Page | What it covers |
| --- | --- |
| [Quick Start](Docs/QuickStart.md) | Download, run from the terminal, and install as a background service (systemd / Scheduled Task). |
| [Docker Deployment](Docs/Docker.md) | Run the versioned GHCR image with Compose, persistent state, environment configuration, and upgrades. |
| [Architecture](Docs/QuasarArchitecture.md) | Supervisor design, runtime ownership, process supervision, configuration model, and self-update. |
| [Configuration](Docs/Configuration.md) | API/UI host and port, headless mode, browser behavior, and optional instance health notifications. |
| [Server directory uplink](Docs/ServerList.md) | Consent, diagnostics availability, public statistics, admin authority and directory rollout requirements. |
| [Analytics CPU Usage](Docs/AnalyticsCpuLoad.md) | Why CPU usage can exceed 100% and how it differs from Shift+F11 simulation CPU load. |
| [Quasar Plugin System](Docs/QuasarPluginSystem.md) | Planned UI plugin loader, hub manifest model, component replacement points, companion data channel, and UI migration compatibility. |
| [Entity Viewer](https://github.com/CometWorks/viewer/blob/main/Docs/EntityViewer.md) | Fullscreen metadata-only entity viewer, local Space Engineers `Content` folder requirement, and fallback behavior. |
| [Diagnostic Consent & Upload](Docs/DiagnosticsUplink.md) | Separate statistics/report/dump consent, configurable storage paths, central upload configuration, durable queues and host leases. |
| [CometWorks announcements](Docs/Announcements.md) | Consent-gated operational notices in the existing bell, page, browser push and headless service logs; delivery and withdrawal limits. |
| [Building & Development](Docs/BuildingAndDevelopment.md) | Project layout, build setup, managed-runtime selection, and developer utilities. |
| [UI Rewrite Design Study](Docs/Design/QuasarUiRewrite.md) | Migration progress, MudBlazor removal estimate, feature/theme compatibility, and interactive visual samples. |
| [Phase 4 Integration](Docs/Phase4IntegrationPlan.md) | Cluster release integration, verified package staging, upstream requirements, and acceptance plan. |
| [Cluster Integration Verification](Docs/ClusterIntegrationVerification.md) | Implementation checks, review package provenance and remaining live acceptance gates. |
| [Guided Cluster Setup](Docs/ClusterGuidedSetupPlan.md) | Machine enrollment, local/SSH/command installation, automatic provisioning and release prerequisites. |
| [Cluster Terminology](Docs/ClusterTerminology.md) | Terminology occurrences and continuity findings across cluster setup, hosts, controls and contracts. |
| [Cluster Health Overview](Docs/ClusterOverview.md) | Live cluster topology, resource readings, service addresses and grouped controls. |
| [Cluster Conversion](Docs/ClusterConversion.md) | Guided standalone/cluster conversion, backups, resume and plugin configuration limits. |
| [Cluster Plugins](Docs/ClusterPlugins.md) | One-server plugin/config consistency, Agent monitoring, existing SDK compatibility and remaining integration work. |
| [Cluster Content Updates Plan](https://git.cometworks.se/CometWorks/clustering-plan/src/branch/main/Plan/ClusterContentUpdatesPlan.md) | Proposed Workshop byte pinning, scheduled mod/plugin notices, explicit maintenance updates and compatible rollovers including World Authority. |
| [Linux Deployment & Updates](Docs/LinuxDeploymentAndUpdates.md) | systemd install, release assets, and the auto-updater flow. |
| [Windows Deployment & Updates](Docs/WindowsDeploymentAndUpdates.md) | Scheduled Task install, release assets, and the auto-updater flow. |
| [State Machine Diagrams](Docs/StateMachines/Index.md) | Object states and state machines (server lifecycle, agent connection, self-update, runtime provisioning, backups, …) as Mermaid + PNG. |
| [Admin handbook](skills/quasar-book/TOC.md) | Task-oriented handbook for server admins, written from the code: install, servers, plugins, updates, backups, clusters, troubleshooting. Also an AI skill (`quasar-book`), used by the Clangy support bot. |

Entity Viewer companion plugins can now discover connected servers through
`IQuasarCompanionChannel.GetConnectedServers()` and collect recording batches in
background hosted services without an open browser. Stable server IDs are paired
with current agent IDs. Cluster nodes use separate incarnation IDs, which can be
passed directly back to the companion channel. The Entity Viewer owns replay contracts, capture,
24-hour storage, admin-only history endpoints, and playback; Quasar core supplies
server discovery and the existing companion request transport. Install matching
Quasar and Entity Viewer revisions to enable continuous history collection.

Quasar and UI plugin automatic updates are separately opt-in on **Updates** and
**UI Plugins**. Automatic Quasar application coordinates UI and Bootstrap releases;
plugin updates can restart the worker after a successful build. Administrators can
opt into UI plugin notices on **Notifications**. See
[automatic update configuration](Docs/Configuration.md#automatic-quasar-and-ui-plugin-updates).
