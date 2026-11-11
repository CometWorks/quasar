# Overview

Quasar runs and manages Space Engineers 1 (SE1) dedicated servers. This page says what it is made of, how the parts talk to each other, and which words the rest of the book uses.

## What Quasar does

Quasar is a supervisor with a web UI (Blazor Server) and a REST API. One Quasar install manages several dedicated servers on the same machine. It starts, stops, restarts and health-checks them, edits their configuration, downloads and updates the server software, keeps backups, and shows telemetry, chat and players. Admins open the UI in a browser, by default on port 8080.

The UI shows timestamps as `YYYY-MM-DD HH:MM:SS` in the browser's local time by default. To use a different zone, open **Settings → Display**, select a time zone such as `Europe/Stockholm`, and click **Save**. The preference is saved in that browser. **Use browser time** restores the default. Recent activity and dense tables put the date above the time. If Quasar can't determine the browser's time zone or offset, it labels timestamps `UTC`. Schedule settings, such as backup and restart times, use the Quasar machine's local time.

It runs on Linux and Windows as a native service: a systemd unit on Linux and a Windows Service on Windows, each with a second, small updater service next to it. It also runs from a terminal in the foreground, and as a Docker image on Linux AMD64. Installations from before 1.4.1 ran a Bootstrap launcher instead, as a systemd unit or a Windows Scheduled Task. They migrate to native services using the current complete package; no intermediate release is required. See [Updates](Updates.md#direct-installation-and-upgrades).

Source and releases: https://github.com/CometWorks/quasar

The current distribution uses `AGPL-3.0-only`. The previous MIT snapshot is preserved by the `archive/mit` tag. See [Licensing](Licensing.md) for source availability, modified hosted versions and redistribution.

## The parts

| Part | Where it runs | What it does |
| --- | --- | --- |
| Quasar | The server machine | The supervisor in one process: web UI, REST API, server process management, config editing, the WebSocket endpoint the agents connect to. It also implements the `Quasar update ...` and `Quasar cluster ...` command lines. |
| Quasar.Updater | The server machine | A separate small service that installs Quasar updates. It verifies the downloaded package, stops Quasar, replaces its files, starts it and checks its health, and restores the previous version on failure. See [Updates](Updates.md). |
| Quasar.Bootstrap | Old installations only | The launcher of installations before 1.4.1. It started a separate web worker and swapped it during self-update. Current packages don't use it. |
| Quasar.Agent | Inside each dedicated server | A server plugin. It connects back to Quasar over a WebSocket, reports state and telemetry, and runs commands on the game thread. If Quasar is not running, the agent waits and retries. It never starts Quasar. |
| Magnetar | Each dedicated server | The plugin loader and launcher of the SE1 dedicated server, built on Pulsar. Quasar downloads it and runs every server through it. See https://github.com/CometWorks/magnetar |
| Space Engineers Dedicated Server (DS) | Each dedicated server | The game server itself. Quasar downloads it with SteamCMD (app 298740) and keeps a managed copy. |
| Quasar.Host | Each cluster machine | The host executor of clusters. A separate background service on every machine that runs cluster processes. See [Cluster hosts](ClusterHosts.md). |

Each release has one complete package per platform, `quasar-linux-x64.tar.gz` and `quasar-win-x64.zip`. It holds Quasar, the updater, Quasar.Host and the installers.

Servers run detached from Quasar. By default restarting or updating Quasar does not stop them, and the agents reconnect when Quasar is back. The Docker image is the exception: there servers stop with the container. An agent that can't reach Quasar for an hour saves the world and stops its server. See [Servers](Servers.md#servers-survive-quasar-restarts) and [Updates](Updates.md).

## Server runtimes

Magnetar comes in two builds. `MagnetarInterim` runs on .NET 10 and is the default. `MagnetarLegacy` runs on .NET Framework 4.8 and is available on Windows only, where Quasar installs both builds side by side and each server picks one. On Linux every server runs the .NET 10 build. See [Servers](Servers.md).

## Plugins

Two kinds of plugins exist, from two hubs:

- Server plugins load into the dedicated servers through Magnetar. They come from MagnetarHub (https://github.com/CometWorks/magnetar-hub), from plugin hubs you register, or from local dev folders. See [Plugins](Plugins.md).
- UI plugins extend the Quasar web UI itself. They come from QuasarHub (https://github.com/CometWorks/quasar-hub). See [UI plugins](UiPlugins.md).

Torch plugins do not work with Magnetar.

## Clusters

A cluster splits one world across several server processes, possibly on several machines, behind a Cluster Gateway. Quasar sets up and manages clusters, and a Quasar.Host runs on each machine involved. Cluster support is marked as beta in the UI. See [Clusters](Clusters.md).

## Glossary

| Term | Meaning |
| --- | --- |
| Server | A dedicated server that Quasar manages: one Magnetar process with its own app-data folder, world save and config. |
| Goal state | What the admin wants a server to be (on or off). Quasar keeps reconciling the actual process state toward it. |
| Config profile | A reusable set of server and world settings (session settings, mods, password, access lists) that one or more servers use. See [Config profiles](ConfigProfiles.md). |
| World template | A stored world that new server saves are copied from. See [Config profiles](ConfigProfiles.md#world-templates). |
| Agent | Quasar.Agent, the plugin inside each server. "Connected agents" on the UI means servers whose agent is talking to Quasar. |
| Updater | The Quasar.Updater service that installs Quasar updates next to the main Quasar service. |
| Host machine | A computer that runs cluster processes. |
| Host executor | The Quasar.Host service on a host machine. |
| Cluster Gateway, Cluster Registry | The cluster's player entry point, and the service that coordinates the cluster's processes. |
| World Authority | The cluster role that owns world-wide state. |
| Public server port | The UDP port players connect to. |
| Headless mode | Quasar running with only its API and background jobs, without the web UI. See [Configuration](Configuration.md). |

## Sources
- `README.md`
- `Docs/QuickStart.md`
- `Docs/Installation.md`
- `Docs/QuasarArchitecture.md`
- `Docs/QuasarUpdates.md`
- `Docs/ReleaseNotes1.5.md`
- `Docs/ClusterTerminology.md`
- `Quasar/appsettings.json`
- `Quasar/Quasar.csproj`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar.Updater/QuasarUpdater.cs`
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Bootstrap/ClusterCli.cs`
- `Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`
- `Quasar/Services/ClusterBetaNoticeCatalog.cs`
- `Dockerfile`
- `Quasar.Agent/AgentConnection.cs`
- `Quasar.Agent/AgentOptions.cs`
