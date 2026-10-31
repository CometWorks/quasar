# Magnetar server plugins

This page covers the plugins that run inside each managed Dedicated Server (DS): where Quasar gets the plugin catalog, how to pick plugins for a server, how to edit their settings, extra plugin hubs, local dev folders, and the Quasar.Agent plugin that Quasar deploys into every server. Quasar UI plugins, which run inside the Quasar web app, are a different thing; see [UI plugins](UiPlugins.md).

## Server plugins and UI plugins

| | Magnetar server plugins (this page) | Quasar UI plugins |
| --- | --- | --- |
| Runs in | Each DS process, loaded by Magnetar | The Quasar web worker |
| Catalog | MagnetarHub, https://github.com/CometWorks/magnetar-hub, plus the plugin hubs you register | QuasarHub, https://github.com/CometWorks/quasar-hub |
| Where you pick them | **Configs** page, per config profile | **UI Plugins** page, for the whole Quasar install |
| API | Magnetar PluginSdk | `Quasar.Plugin.Abstractions` |

## Where the catalog comes from

Quasar downloads the `main` branch of MagnetarHub (`CometWorks/magnetar-hub`) as a zip archive from GitHub and reads every `.xml` file under `Plugins/`. Each file with an `<Id>` becomes one catalog entry. Registered [plugin hubs](#plugin-hubs) add their plugins to the same catalog. Quasar reads `Id`, `FriendlyName`, `Author`, `Description`, `Tooltip`, `Runtimes`, `RepoId`, `Commit` and `Hidden` from the manifest.

When Quasar refreshes the catalog:

- 2 seconds after Quasar starts, then every 8 hours.
- When you click **Refresh Catalog** in a config profile's **Plugins** tab, and each time you open the **Plugin catalog** panel there.
- Before a server starts, when the profile selects a plugin id that the cached catalog does not have.

Each hub refreshes on its own. Quasar sends the previous ETag, so an unchanged hub is not downloaded again. The catalog is cached in `{Quasar install directory}/Caches/plugin-catalog.json`. The install directory is `~/.local/share/Quasar` for the default Linux user service; on Windows it is the folder the installer was extracted to (see [Configuration](Configuration.md)).

If a hub can't be read, Quasar keeps that hub's cached plugins, marks them "Hub unreachable, cached entry", shows the error in the hub list under **Developer**, and the profile editor shows a **Catalog stale** chip. **Refresh Catalog** then warns "Some plugin hubs could not be read, their cached plugins are shown: ...". An empty catalog shows "Plugin catalog empty. Refresh to download the plugin hubs."

## Adding and removing plugins on a server

Plugins belong to a config profile, not to a single server. Every server assigned to the profile gets the same plugin list. See [Config profiles](ConfigProfiles.md) for profiles in general.

1. Open **Configs** in the navigation and select the profile.
2. Open the **Plugins** tab.
3. In **Plugin catalog**, tick **Use** next to each plugin you want. Search matches name, id, author, description and tooltip. Untick to remove, or use the delete button in **Plugins to load**.
4. To add a plugin by id without the catalog, open **Advanced/manual plugin setup**, type the id in **Add custom plugin ID** and click **Add Custom**. If no hub or dev folder lists that id, Quasar adds it anyway and warns: "No plugin hub or dev folder lists this plugin yet. Magnetar won't load it, and cluster setup refuses it, until one does." **Plugins to load** marks it "Not listed by any plugin hub".
5. Click **Save Profile**.

Quasar writes the plugin list into the server's Magnetar files each time it prepares a server start. A running server picks up plugin changes at its next start or restart.

Editing needs the `CanEditConfigs` permission. Viewers see a read-only summary of the profile, including its plugins. See [Access control](AccessControl.md).

### Default plugins

Every config profile Quasar creates selects the MagnetarHub server plugins **Bugfixes** and **Performance** with their default settings: **Create profile** on the Configs page and in the server editor, the profile the setup guide or a world template creates from a world, and the profile an existing-server import creates. Guided cluster setup adds the same two to the cluster's copy of its profile (see [Clusters](Clusters.md#bugfixes-and-performance-by-default)).

To turn one off, remove it from **Plugins to load** and click **Save Profile**. Quasar records in `profile.json` (`offeredDefaultPlugins`) that it offered them and never adds them to that profile again. Clones and converted profiles keep their source's plugins. Quasar does not add the defaults to existing profiles, so a new server that uses an existing profile runs that profile's plugins.

### Plugins you cannot select

- `dotnet-compat` and `error-reporting` (and the legacy `se-dotnet-compat`) are managed by Magnetar. Selecting them shows "This plugin is managed automatically by Magnetar and cannot be selected manually." Magnetar loads `dotnet-compat`, `error-reporting`, and `linux-compat` on Linux by itself, so they never need to be in a profile.
- Entries marked `Hidden` in their hub show "Hidden in hub" and cannot be ticked: "This plugin is hidden in {hub name} and cannot be selected manually."
- A plugin id that more than one hub lists shows "Listed by more than one hub: ..." and cannot be ticked. See [Plugin hubs](#plugin-hubs).

### Checking that plugins loaded

The server's detail panel on the Dashboard shows **Plugins** as loaded/selected, for example `3/4`. Quasar compares the profile's plugin list with the plugins the agent reports as loaded. The value turns to the warning color when the numbers differ.

## Files Quasar writes for Magnetar

Each server has its own Magnetar app-data folder, by default `{Quasar install directory}/Magnetars/{server unique name}/Magnetar`. Quasar rewrites these files on every server start:

| File | Content |
| --- | --- |
| `Sources/sources.xml` | The MagnetarHub hub source, every registered plugin hub (`LocalHub` or `RemoteHub`), per-plugin `RemotePlugin` entries pointing at MagnetarHub manifests, every registered dev folder as a `LocalPlugin`, and the profile's Workshop mods |
| `Profiles/Current.xml` | Profile named after the config profile: selected hub plugins of any hub under `GitHub`, selected dev folders under `DevFolder`, and Quasar.Agent plus UI plugin companions under `Local` |
| `Local/` | `Quasar.Agent.dll`, `Magnetar.Protocol.dll`, `0Harmony.dll` and built companions of enabled UI plugins |

If you saved a GitHub token on the **Updates** page, Quasar passes it to Magnetar in the `PULSAR_GITHUB_TOKEN` environment variable for plugin hub downloads. It never puts the token on the command line for current Magnetar builds. See [Updates](Updates.md).

## Plugin versions and updates

For a standalone server, Quasar does not pin plugin versions. The generated files point Magnetar at the MagnetarHub manifests on the `main` branch and at each registered hub's branch or folder, and Quasar has no per-plugin update button for standalone servers. A plugin update in a hub reaches a server through Magnetar when the server starts.

Clusters work differently. Staging a cluster profile freezes the exact plugin builds and source commits, Quasar watches each plugin's hub for new commits, and you queue and apply plugin updates from the cluster page. See [Clusters](Clusters.md).

## Plugin configuration

The **Plugins** page (navigation **Plugins**, title "Plugin configuration") edits live settings of plugins that expose a configuration to Quasar.

1. Pick the server in **Server**. Only connected servers that report at least one configurable plugin are listed. With none, the page shows "No connected server is exposing editable plugin configuration."
2. Expand a plugin. Quasar renders an editor from the plugin's own schema (tabs, sections, fields, lists, dictionaries). Values that have no dedicated field type are edited as raw JSON text.
3. Click the apply button. **Reset defaults** fills in the plugin's default values; you still have to apply them.

Quasar sends the new values to the server through the agent and the plugin applies them on the game thread. The success message ("Applied configuration to ...") means Quasar delivered the values to the server.

Who may see and edit configuration:

- Any user with `CanView` can open the **Plugins** page and see the plugin list and logs.
- Configuration values need `CanEditConfigs`, because plugin settings may contain credentials. Others see "Configuration values require editor access because they may contain credentials."
- Cluster nodes are not edited here. The page shows "Plugin configuration is managed for the whole cluster." with a link to the cluster's configuration, which needs the `ClusterManage` permission. See [Clusters](Clusters.md).

### Configuration snapshots

Each time a standalone server's agent reports its plugin configuration, Quasar stores the last known copy in `{Quasar install directory}/PluginConfigSnapshots/`, one JSON file per server, named by a hash of the server name. On Linux the folder and files are readable by the Quasar user only. Quasar uses these snapshots when converting a standalone server into a cluster; they are not live state and stay after the server stops. See [Clusters](Clusters.md).

## Plugin logs

The **Plugin logs** section on the **Plugins** page shows structured log lines that plugins write through the PluginSdk logger on managed servers. Filters: **Server**, **Plugin**, **Level**, **Range** (last 1h, 24h, 7d or all), **Limit** and **Filter text**. Times are UTC.

- Quasar keeps the newest 10,000 entries per server in memory. They are gone after a Quasar restart.
- While Quasar is unreachable, the agent buffers up to 10,000 lines and sends them after it reconnects.
- The agent also writes these lines into the server's Magnetar log (`info_*.log` in the server's Magnetar folder, `info.current` points at the active file).
- Lines from Magnetar itself are not shown in this panel.

Plugin logs and chat can contain player or server information, so think before sharing screenshots.

## Plugin hubs

A plugin hub is a list of plugin manifests: a GitHub repository and branch, or a local folder. MagnetarHub is always used. Register another hub to select plugins that aren't in MagnetarHub, such as a private plugin, a world's own plugin or an unpublished fork, on regular servers and clusters.

### Registering a hub

1. Open **Configs** and expand **Developer** at the bottom of the profile editor.
2. Under **Plugin hubs**, either type the **GitHub repository (owner/name)**, the **Branch** (default `main`) and an optional **Hub name**, then click **Add GitHub hub**; or click **Add local hub folder...** and pick the folder on the machine that runs Quasar.
3. Quasar reads the hub right away and reports "Hub added with N plugins.", or "Hub added, but it could not be read: ..." when it can't reach it.
4. The hub's plugins appear in every profile's **Plugin catalog**, marked "Hub: {hub name}". Tick them like MagnetarHub plugins.

Rules and errors:

- Each manifest needs an `<Id>`, plus `RepoId` and `Commit`, like a MagnetarHub manifest. Quasar and Magnetar read every `.xml` manifest in the hub, in subfolders too.
- A private repository needs a GitHub token with read access on the **Updates** page. Without one, the download fails with "Repository or branch not found. A private repository needs a GitHub token with read access in Updates.". See [Updates](Updates.md).
- A repository can be registered once, whatever the branch: "This repository is already a registered hub. Magnetar reads one branch per repository." MagnetarHub itself can't be added, and hub names must be unique.
- A local hub folder must be an absolute path that exists.

Hubs are global to the Quasar install and stored in `{Quasar install directory}/plugin-hubs.json`. Every server's `sources.xml` lists every hub, but Magnetar loads only the plugins its profile selects. Adding and removing hubs needs `CanEditConfigs`.

### Two hubs with the same plugin id

Magnetar would load the plugin from whichever hub it reads first, so Quasar refuses to choose. The catalog entry shows "Listed by more than one hub: MagnetarHub, {hub}", it can't be selected, and a server whose profile already selects it fails to start with "More than one plugin hub lists {id} ({hubs}). Remove the plugin from all but one hub, or remove a hub, then try again." The same applies to the compatibility plugins Magnetar loads by itself, such as `linux-compat`, even though no profile selects them. A dev folder with the same id is not a conflict; it overrides the hub plugin.

### Removing a hub

Click the delete button next to the hub. The confirmation names the config profiles that use plugins only that hub lists. After removal, regular servers start without those plugins (Quasar logs "Server {name} selects plugins that no plugin hub or dev folder lists"), and cluster setup and profile staging stop with "No plugin hub or dev folder lists the selected plugins ...". Remove the plugins from the profiles, or register a hub that lists them.

## Dev folders

A dev folder registers a local plugin source checkout so it shows up in the plugin catalog like a hub plugin. It is meant for plugin development.

### Registering a dev folder

1. Open **Configs** and expand **Developer** at the bottom of the profile editor.
2. Click **Add dev folder...** and pick the plugin's manifest XML in the file browser. The browser shows the file system of the machine that runs Quasar.
3. Quasar checks the file exists and is valid XML, then adds the folder. Errors: "Plugin manifest not found: {path}" or "Plugin manifest could not be parsed: {path} (...)".

The dev folder is the folder Magnetar compiles from: the nearest folder, from the manifest's own up to its git repository root, that holds every `<SourceDirectories>` entry of the manifest. A manifest in a subfolder of its repository, such as linux-compat's `ServerPlugin/ServerPlugin.xml` with `Shared` and `ServerPlugin` sources, registers the repository root, and the **Manifest** column shows the path relative to it. Asset paths in the manifest are relative to the same folder.
4. In the profile's **Plugins** tab, tick the dev folder entry in **Plugin catalog** (marked "Local dev folder") and save the profile.

Dev folders are global to the Quasar install and stored in `{Quasar install directory}/dev-folders.json`. Quasar lists every registered dev folder as a plugin source for every server, but Magnetar loads it only on servers whose profile selects it. Removing a dev folder needs `CanEditConfigs` and asks for confirmation.

### Plugin id

Quasar identifies a dev folder by the `<Id>` in its manifest, or by the folder name when the manifest has no `<Id>`. Magnetar uses the same id. Quasar re-reads the manifest id each time it loads the dev folder list, and maps profile selections saved by older Quasar releases (which used the folder name) to the manifest id.

When a dev folder has the same id as a hub plugin, the dev folder replaces the hub entry in the catalog, and servers load the dev folder build instead of the hub plugin.

### Build behavior

- For regular servers, Magnetar compiles the plugin from the folder when the server starts. The **Debug build** checkbox next to a selected dev folder in **Plugins to load** chooses a debug or release build. New dev folders default to debug.
- For clusters, Quasar builds each selected dev folder once, at cluster setup and whenever the profile is staged, always as a release build. The folder's git repository must have no uncommitted or untracked changes, the manifest needs a `RepoId`, and the cluster records the HEAD commit. Nodes keep running that build until the next staging, and the cluster content monitor never reports new dev folder commits. See [Clusters](Clusters.md).

### Dev overrides of hub plugins

A dev folder whose id a plugin hub lists too is a dev override: wherever that id loads, servers and clusters build the dev folder instead of the hub's pinned commit. The **Developer** list marks such a folder with a **Dev override** chip; its tooltip names the hub pin it replaces. Use it to run a branch build of a hub plugin on a test server or test cluster without changing the hub, which is a release.

- An ordinary plugin is replaced in every profile that selects its id.
- A core plugin that Magnetar loads by itself (`linux-compat`, `dotnet-compat`, `error-reporting`) is replaced on every server start and every cluster staging of this Quasar install, whatever the profile selects. Quasar logs "Server {name} loads dev overrides of Magnetar core plugins instead of their hub builds: ..." at each start. Cluster staging builds it as a Release build.
- Cluster setup and staging show "dev overrides, not the hub pins: {id} {commit} from {folder}" in their progress and log it as a warning. The cluster's content panel shows "Dev override: ..." for the plugin and offers no hub update for it.
- Dev folders belong to the whole Quasar install, not to one cluster. To test on one cluster only, run that cluster from its own Quasar install.

Native assets. Magnetar compiles the C# sources of a dev folder and resolves the manifest's `<Asset Path="...">` entries relative to the dev folder, so native libraries come from those paths. For linux-compat they are `../../linux-dependencies/dist/se1-dependencies.tar.gz` and `../../linux-native-wrappers/build`, outside the checkout. To run a branch build with the same native libraries as the hub pin, put the hub's pinned downloads there with `scripts/dev-override-assets.py` from the Quasar source:

```
python3 scripts/dev-override-assets.py ~/overrides/se1/linux-compat ServerPlugin/ServerPlugin.xml \
    https://raw.githubusercontent.com/CometWorks/magnetar-hub/main/Plugins/LinuxCompat.xml
```

It downloads each `<Asset Url="..." Sha256="...">` of the hub manifest, refuses a SHA-256 mismatch, and puts it at the dev manifest's path for the asset of the same name (extracted when that path is a folder). It refuses paths inside the checkout, because a cluster stages only a clean dev folder, and keeps existing files unless you pass `--force`. To test native changes instead, build them at those paths.

Running a branch build of linux-compat on a test cluster:

1. Clone the plugin repository at the commit to test, for example `git clone https://github.com/CometWorks/linux-compat ~/overrides/se1/linux-compat` and `git -C ~/overrides/se1/linux-compat checkout testing`. Keep the checkout clean.
2. Place the native assets with `scripts/dev-override-assets.py` as above.
3. **Configs → Developer → Add dev folder...**, pick `~/overrides/se1/linux-compat/ServerPlugin/ServerPlugin.xml`. The row shows the repository root, the manifest `ServerPlugin/ServerPlugin.xml` and the **Dev override** chip.
4. Stage the cluster's profile (**Stage profile changes**, or **Stage latest release**). The progress names the override with its commit.
5. Apply the pending update from **Maintenance** (full downtime).

To drop the override, remove the dev folder (one click in the **Developer** list), then stage and apply again. The next staging pins the hub build.

## Quasar.Agent

Quasar.Agent is a Magnetar plugin (`Quasar.Agent.dll`) that Quasar installs into every managed server. It connects back to Quasar, reports telemetry and runs commands sent from the web UI.

### How Quasar deploys it

On every server start Quasar copies `Quasar.Agent.dll` and `Magnetar.Protocol.dll` from the `Agent` folder of the Quasar install into the server's Magnetar `Local` folder, together with the matching `0Harmony.dll` (.NET 10 or .NET Framework 4.8 build). It only copies a file when its SHA-256 hash differs, and adds `Quasar.Agent.dll` to the `Local` list of `Profiles/Current.xml`. You never select the agent in a profile.

If the `Agent` folder is missing, Quasar logs "Quasar.Agent.dll could not be located on disk; the agent plugin will not be deployed." and the server runs without telemetry or remote commands. A missing `0Harmony.dll` only disables profiler telemetry.

### What it does

The agent connects to Quasar's `/ws/agent` WebSocket. It finds Quasar through `QUASAR_BASE_URL` (Quasar sets it when launching the server), `QUASAR_PUBLIC_BASE_URL`, `MAGNETAR_WEB_BASE_URL`, or the `service-manifest.json` in the Quasar install directory, and checks `/api/health` before connecting.

Quasar accepts the connection only with an agent token in the `X-Quasar-Agent-Token` header. The agent reads it from `QUASAR_AGENT_TOKEN`. For a standalone server, Quasar derives the token from the server's unique name and a key it keeps in `cluster-credentials.json` in the Quasar install directory, and sets it when it launches the server. For a cluster node, the Host executor derives the token from its enrollment credential (see [Cluster hosts](ClusterHosts.md)). The first message on the socket has to be the agent's Hello, and its server name, or its Host, cluster, slot, node, role and launch attempt for a cluster node, has to match the token. A cluster Host is accepted only while it belongs to that cluster's active deployment, and live telemetry must come from the Host the Gateway assigned to the node. Quasar also refuses a second connection for a server whose agent is still connected and sent data in the last 30 seconds. A server that Quasar did not launch has no token, so its agent can't connect.

Messages from Quasar to the agent must be text WebSocket messages and may be at most 64 MiB across all fragments. The agent closes the connection on a binary, mixed-type, or oversized message and reconnects normally.

Every 2 seconds it sends:

- a server snapshot: metrics, players, recent chat, deaths, chat commands, loaded and declared plugins, profiler samples and plugin statistics
- the plugin configuration snapshot, when it changed since the last one
- buffered plugin log lines

It runs commands from Quasar: refresh, send chat, whisper and faction chat, save world, stop server, kick, ban, unban, promote, demote, set promote level, clear kick cooldown, list and delete entities, change profiler mode, apply plugin configuration, and companion requests from UI plugins. Cluster nodes reject the agent-local save and stop commands.

Plugin statistics appear on **Analytics**; players, chat and entities on their own pages. See [Monitoring](Monitoring.md).

### In-game commands

On standalone servers the agent registers three chat commands:

| Command | Effect |
| --- | --- |
| `!stop` | Saves the world and shuts the server down. Quasar sets the server's goal state to Off, so it stays stopped. |
| `!restart [seconds]` | Saves and restarts after the delay (default 10, 0 to 3600), with countdown announcements. Quasar keeps the goal state On. "Restart already scheduled." if one is pending. |
| `!quit` | Quits immediately without saving. |

### When Quasar is unreachable

The agent keeps the server running and retries the connection. If it had connected at least once and Quasar stays unreachable for the offline window, the agent saves the world and stops the server. Cluster nodes never stop on their own.

| Setting (`Quasar` section of appsettings.json) | Environment variable | Default |
| --- | --- | --- |
| `AgentOfflineShutdownSeconds` | `QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS` | `3600` (0 or less stops promptly) |
| `AgentReconnectIntervalSeconds` | `QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS` | `10` |
| `AgentReconnectJitterSeconds` | `QUASAR_AGENT_RECONNECT_JITTER_SECONDS` | `3` |
| `AgentProfilerMode` | `QUASAR_AGENT_PROFILER_MODE` | `SafeContinuous` (also `Off`, `DeepContinuous`) |

Quasar passes these to each server at launch, so changes apply at the next server start. The profiler mode can also be set per server on **Analytics**, which overrides the default. See [Configuration](Configuration.md) and [Monitoring](Monitoring.md).

## Making a server plugin work with Quasar

Quasar integrates with plugins through Magnetar's PluginSdk. The code under `Quasar/Services/PluginSdk` is Quasar's receiving side; it is not a separate SDK.

- **Configuration editor.** A plugin whose public `PluginConfig` property uses PluginSdk config options appears on the **Plugins** page with an editor built from its schema. A plugin can also implement `IQuasarConfigProvider` from `Magnetar.Protocol` and exchange the config as JSON. Configs the SDK tracks but the plugin does not expose through a public property are read-only: "SDK-tracked configuration is read-only here. Edit its plugin-owned settings directly, or prepare cluster configuration."
- **Logs.** Quasar sets `QUASAR_AGENT` for every managed server, which makes the PluginSdk logger send structured lines to the **Plugin logs** panel.
- **Statistics.** Statistics published through PluginSdk `PluginStats` are forwarded by the agent and charted on **Analytics**.
- **UI plugin companions.** A server plugin that implements `IQuasarCompanionRequestHandler` can answer requests from a Quasar UI plugin. See [UI plugins](UiPlugins.md#companion-data-channel).

## Registering a new plugin

Quasar's shared catalog is MagnetarHub. To make a plugin selectable for everyone, open a pull request to https://github.com/CometWorks/magnetar-hub that adds an XML manifest under `Plugins/`. After it is merged, Quasar sees it at the next catalog refresh, or immediately after **Refresh Catalog**. Until then you can test the plugin locally through a dev folder, or serve it from your own [plugin hub](#plugin-hubs).

## Common problems

- **A selected plugin does not load.** The server panel shows fewer loaded than selected plugins. Check the server's Magnetar log (`info_*.log` in `Magnetars/{server}/Magnetar`) for build or load errors, then the **Plugin logs** panel. Quasar itself does not compile hub plugins; Magnetar does.
- **"Plugin catalog empty. Refresh to download the plugin hubs."** Quasar has not downloaded the hubs yet or cannot reach GitHub. Click **Refresh Catalog** and check the hub list under **Developer** and Quasar's log for "Failed to refresh plugin hub".
- **"The plugin hubs currently expose only hidden or automatic plugins. They are not selectable manually."** Every catalog entry is hidden or a Magnetar core plugin.
- **A server fails to start with "More than one plugin hub lists ..."** Two hubs list the same plugin id. Remove the manifest from one hub, or remove one hub.
- **A hub shows an error in the hub list.** GitHub is unreachable, the repository or branch is wrong, the repository is private and the token can't read it, or the local folder is missing. Its cached plugins stay selectable.
- **"Dev folder already registered."** The same folder and manifest file are already in the list.
- **The Plugins page lists no servers.** The server is stopped, its agent is not connected, or none of its plugins exposes a configuration.
- **"Fix the invalid JSON values before applying."** A raw JSON field in the config editor does not parse.
- **"Could not reach the server: ..."** The agent connection changed or dropped while applying; reopen the page and try again.
- **Plugin settings changed in the editor have no effect.** Quasar reports success once it delivers the values. If the plugin rejects them, the agent writes "Failed applying plugin config to '...'" to the server's console output.
- **A dev folder plugin builds on a server but cluster staging fails.** Commit or remove local changes in the dev folder's repository and add a `RepoId` to its manifest.

Agent/control traffic additionally requires explicit socket peer and Host restrictions and rejects any Origin header. Cluster node tokens bind Host, cluster, slot and node. Follow the [DIS-0042 save, Host update and cluster re-pin instructions](https://github.com/CometWorks/quasar/blob/main/Docs/SecurityUpgradeDIS0042.md) when upgrading older agents.

## Sources
- `Docs/SecurityUpgradeDIS0042.md`
- `Quasar/Services/QuasarDefaultPlugins.cs`
- `Quasar/Services/ExistingServerImportService.cs`
- `README.md`
- `Docs/ClusterPlugins.md`
- `Docs/BuildingAndDevelopment.md`
- `Docs/Configuration.md`
- `Docs/QuickStart.md`
- `Docs/WindowsDeploymentAndUpdates.md`
- `Quasar/Services/QuasarPluginCatalogService.cs`
- `Quasar/Services/PluginCatalogRefreshService.cs`
- `Quasar/Services/PluginManifestReader.cs`
- `Quasar/Services/QuasarDevFolderCatalog.cs`
- `Quasar/Services/QuasarPluginHubCatalog.cs`
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/AgentSocketHandler.cs`
- `Quasar/Services/AgentSocketAuthenticator.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/PluginSdk/PluginConfigService.cs`
- `Quasar/Services/PluginSdk/PluginConfigDtos.cs`
- `Quasar/Services/PluginSdk/PluginLogStream.cs`
- `Quasar/Models/QuasarConfigProfile.cs`
- `Quasar/Models/DedicatedServerPathResolver.cs`
- `Quasar/Components/Pages/Plugins.razor`
- `Quasar/Components/Pages/Configs.razor`
- `Quasar/Components/Pages/PluginManifestPickerDialog.razor`
- `Quasar/Components/PluginConfigEditor.razor`
- `Quasar/Components/PluginLogPanel.razor`
- `Quasar/Components/Dashboard/ServerDetailPanel.razor`
- `Quasar/Components/Pages/Analytics.razor`
- `Quasar/Components/Dashboard/ClusterPluginConfigurationPanel.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar.Agent/AdminPlugin.cs`
- `Quasar.Agent/AgentConnection.cs`
- `Quasar.Agent/AgentOptions.cs`
- `Quasar.Agent/GameBridge.cs`
- `Quasar.Agent/PluginLogOutbox.cs`
- `Quasar.Agent/StopCommand.cs`
- `Quasar.Agent/WebServiceLocator.cs`
- `Magnetar.Protocol/Bridge/IQuasarConfigProvider.cs`
- `Magnetar.Protocol/Bridge/IQuasarCompanionRequestHandler.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Magnetar.Protocol/Transport/ServerCommandType.cs`
- `Quasar.Tests/CorePluginManifestTests.cs`
- `Quasar.Tests/DevFolderRuntimePreparationTests.cs`
- `Quasar.Tests/PluginHubTests.cs`
- `Quasar.Tests/PluginConfigSnapshotPersistenceTests.cs`
