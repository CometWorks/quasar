# Quasar UI plugins

Quasar UI plugins add pages, navigation links and panels to the Quasar web interface itself. This page covers QuasarHub, the **UI Plugins** page, installing, enabling, updating and removing UI plugins, the .NET 10 SDK they need, safe boot, headless mode, and the companion channel that connects a UI plugin to a plugin inside the game server. For plugins that run inside the Dedicated Server, see [Magnetar server plugins](Plugins.md).

## What a UI plugin is

A UI plugin is a .NET assembly that Quasar loads at startup. It runs with the same rights as Quasar and is not sandboxed. Install only plugins you trust; QuasarHub lists reviewed plugins pinned to a commit.

A UI plugin can:

- register its own services and HTTP endpoints
- add routable pages
- add navigation links
- render components in the extension points Quasar provides
- add Discord slash commands, context-menu commands, buttons, select menus and modals through Quasar's [Discord bot](Monitoring.md#discord-integration)
- ship static files (JavaScript, CSS, images), served under `/_quasar/plugins/{plugin id}/`
- ship one or more companion Magnetar plugins that Quasar deploys into managed servers

Starting in Quasar 1.7.0, Razor pages and extension panels run in the browser.
An installed plugin that still supplies worker-only Razor UI appears under load
errors on **UI Plugins**. Upgrade the plugin to a version that declares a
`browserAssembly`, then restart Quasar. Its worker operations and game companions
can run independently of its UI.

UI plugins are loaded once per worker start. Install, update, enable, disable and remove all change files on disk only, and take effect after a Quasar restart.

Browser plugins can opt in to Quasar's native dialog and toast hosts through the
worker's `Quasar.Ui.Abstractions.dll`. The plugin browser project references that
DLL (QuasarHub passes `QuasarUiAbstractionsAssembly`), injects
`IQuasarDialogService` or `IQuasarToastService`, and gets focus-managed native
dialogs, accessible toasts, and the active palette without a MudBlazor or
`Quasar.Client` reference. The same assembly provides the `QuasarInlineNotice`
component; add `@using Quasar.Ui.Components` for the component and
`@using Quasar.Ui` for `QuasarSeverity`, then use it for inline page notices in
core pages or plugin pages. `AdditionalAttributes` forwards `id`, `data-*`, and
extra `aria-*`; `Class`, `Style`, `role`, and `aria-atomic` stay component-owned.
`Role` overrides the severity default; use `Role="status"` for a static warning
that should not interrupt. The component renders its live region before the
content, so a dynamically inserted notice can be announced.

`Modal = false` gives a modeless dialog that leaves
the page interactive and shows a keyboard-operable drag handle by default;
`Resizable = false` removes its standard bottom-right resize grip.
A cancellation token supplied to `Show`/`ShowAsync` is observed for the dialog's
lifetime: canceling it before the dialog opens returns a canceled result, and
canceling it while the dialog is open closes it with a canceled result.
Per-call `Class`/`Style`, the documented
`--quasar-dialog-*`, `--quasar-toast-*` and `--quasar-notice-accent` tokens, and
the public `.quasar-dialog`/`.quasar-toast`/`.quasar-notice` classes cover
plugin-specific styling. Plugins that do not use the shared hosts keep the
native HTML/CSS path and need no new reference.

A native modal makes the page behind it inert. MudBlazor selects, menus,
autocomplete lists, tooltips and date pickers render their popovers at shell
level, so they cannot appear above a native modal. Keep those Mud controls in a
modeless dialog or a legacy Mud dialog until Quasar provides a dialog-local
popover provider. Native controls work inside either dialog mode.

For simple confirmations, `IQuasarDialogService.ShowMessageBoxAsync` uses the
same native host and returns `bool?`: `true` for Yes, `false` for No, and
`null` for Cancel, the header close button, or dismissal. The header close
button is always shown. Line breaks in the message are preserved. A cancellation
closes the dialog only while it is still open; a button pressed before the
cancellation wins. A call made before a host is mounted is queued and completes
when a host renders and closes it; if no host ever mounts, the task stays pending
until the token is canceled, which completes it with `null`.

A modeless panel with `CloseOnNavigation = false` stays open while the admin
moves between pages. A plugin can feed it live data from its worker half through
the browser operation SDK, so a performance or status panel keeps updating on
the Logs page and elsewhere. The dialog belongs to the shell, not to the page
that opened it.

## Browser operation SDK

Plugins can register protected operations with Quasar browser SDK v1. Quasar
handles authentication, requests, streaming limits and cancellation. Plugins
retain their feature rules, such as asset consent and ownership. Entity Viewer
uses the SDK for scenes, asset setup/downloads and recording history.

On **UI Plugins**, expand **Plugin endpoint authorization** to list plugin and adopted core
operations. This section is collapsed by default. Administrators can disable an operation or change its required
policies. Missing or invalid rules deny access. The same rule applies to adopted
legacy URLs. See [Access control](AccessControl.md#plugin-endpoint-authorization).

Entity Viewer 0.1.13 requires Quasar 1.6.0 or later. Upgrade Quasar first, then
update the viewer adapter, assets and companion together.

A plugin can require `browserSdkVersion: 1`. Quasar rejects unsupported versions
before loading its assembly. An update error in a viewer means Quasar and the
plugin need matching versions. Browser UI plugins provide a separate browser assembly. The SDK uses HTTP(S)
locally and the tunnel or a confirmed direct WebRTC peer on hosted panels. Developers can use [the SDK guide](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarBrowserSdk.md).

## The UI Plugins page

Open **UI Plugins** in the **Infrastructure & tools** section of the navigation (`/settings/ui-plugins`). The page needs the `CanManageSecurity` permission; see [Access control](AccessControl.md).

The page has:

- Header chips: **Dynamic plugins enabled** or **Safe boot active**, and the .NET SDK state (**.NET 10 SDK global**, **.NET 10 SDK managed** or **.NET SDK on demand**).
- **Restart Quasar**: beside **Refresh Hub** in the QuasarHub Catalog header. It stays grayed out until an installed, removed, enabled or disabled plugin, or a safe-boot marker change, needs a worker restart. A hub update being available alone does not enable it. It also needs a verified native restart capability for this Quasar process (1.4.1 and later) or the legacy Bootstrap launcher, and remains disabled during plugin operations or SDK installation. Pending changes survive leaving the page and clear when the worker restarts.
- The enabled **Restart Quasar** button gently pulses to draw attention to pending plugin changes, including a newly installed plugin. Browsers set to reduced motion show a steady highlight instead. The cue stops while the button is disabled or a restart is in progress.
- Counters for **Loaded** plugins, **Load Errors**, **Extensions** and **Nav Items**.
- **Safe Boot**: create or clear the safe boot marker file.
- **QuasarHub Catalog**: browse, install, update, enable, disable and remove plugins, and the automatic update checkbox.
- **Load Errors**: one entry per plugin that failed to load at startup.
- **Loaded Plugins**: name, id, version, entry assembly, static assets and stylesheets of each loaded plugin. The paths menu shows the package folder, the shadow cache folder and file details. **Disable** turns a plugin off for the next restart.
- **Plugin endpoint authorization**: collapsed by default. Edit operation rules, shared policy role grants and the roles of plugins' AI agents here.
- **Plugin Nav Items**, **Extension Contributions** and **Razor Assemblies**: what each loaded plugin contributes.

## QuasarHub

QuasarHub is https://github.com/CometWorks/quasar-hub. Quasar downloads the `main` branch as a zip archive and reads every `.xml` file under `Plugins/` whose `<PluginKind>` is `QuasarUiPlugin`. Each file needs an `<Id>`, which is the catalog id. Quasar also reads `RepoId`, `Commit`, `FriendlyName`, `Author`, `Tooltip`, `Description`, `ProjectPath`, `PackageManifest`, `QuasarVersion`, `Hidden`, `Recommended` and `ImplicitLoading`.

Quasar refreshes QuasarHub when the worker starts and every 15 minutes, and when you click **Refresh Hub**. The catalog is cached in `{Quasar install directory}/Caches/ui-plugin-hub-catalog.json`. If a refresh fails, Quasar keeps the cached catalog, shows **Refresh failed** and the error text.

Recommended entries sort near the top and show a **Recommended** badge. Hidden entries sort last.

Catalog descriptions wrap across the full column instead of being cut off. Use
**Show full description** for the longer text, plugin GUID, repository link and
full pinned commit hash. That button is available for every catalog entry. Rows
keep status, author and version compact to leave room for the description. The
dialog wraps text to its width and collapses the source XML's indentation and
line breaks into spaces.

### Status column

| Status | Meaning |
| --- | --- |
| Not installed | Available, not on disk |
| Recommended | Not installed, the hub recommends it |
| Implicit | Not installed, the hub marks it for automatic install |
| Hidden | Not installed, hidden in the hub |
| Removed | You removed an implicit plugin; Quasar will not reinstall it on its own |
| Installed | On disk, enabled, at the commit the hub pins |
| Update available | On disk and enabled, the hub pins a different commit |
| Disabled | On disk, turned off |
| Invalid | The installed `quasar-plugin.json` cannot be read; the error is shown below the status |

## Installing and updating

Plugin changes and private .NET SDK installation show an activity panel with
elapsed time and a note after 30 seconds for longer waits. Restart feedback shows
a progress bar for completed stages, the current status and elapsed time, and
continues working while Quasar is offline.

1. On **UI Plugins**, find the plugin in **QuasarHub Catalog**.
2. Click **Install** (or **Update**, or **Reinstall** when the installed commit already matches).
3. If no .NET 10 SDK is available, answer the SDK prompt (see [.NET 10 SDK](#net-10-sdk)).
4. Wait for "{name} installed. Restart Quasar to load it."
5. Click **Restart Quasar**. A native service install hands the restart to its updater service; a legacy install hands it to Bootstrap. The browser shows restart progress in the current dashboard theme and reloads the page when `/api/health` answers again. Running game servers stay online.

What the install does:

1. Downloads `https://github.com/{RepoId}/archive/{Commit}.zip`. Git is not needed.
2. Reads `quasar-plugin.json` from the repository root. Only a root `quasar-plugin.json` is supported.
3. Runs `dotnet build` on the plugin's `projectPath`, passing the running worker's `Quasar.Plugin.Abstractions.dll` as `QuasarPluginAbstractionsAssembly`.
4. Builds each owned companion Magnetar plugin (see [Companion plugins](#companion-plugins)).
5. Only after every build succeeds, replaces `{Quasar install directory}/Plugins/{catalog id}` with the new files. On any failure the previous package stays in place.
6. Writes `quasar-ui-plugin-install.json` (catalog id, repository, commit, install time) into the package folder and enables the plugin.

Build configuration is `Release`, unless `QUASAR_UI_PLUGIN_BUILD_CONFIGURATION` or `Quasar:Plugins:BuildConfiguration` says otherwise (`Debug` in a development environment). Installs, plugin builds and Quasar self-updates share one maintenance lock, so only one runs at a time.

The **Install** button is disabled with a tooltip when another QuasarHub operation, an SDK download or a hub refresh is running, or when the hub entry has no repository or no pinned commit.

## Enabling, disabling and removing

- **Disable** / **Enable** on a catalog row, or **Disable** on a loaded plugin, records the choice in `{Quasar install directory}/ui-plugins.state.json` and applies at the next restart. A plugin with no entry in that file is enabled.
- **Remove** (the delete icon) deletes the package folder after confirmation and applies at the next restart. For an implicit plugin, Quasar records the catalog id in `ui-plugins.state.json` so it stays removed. A later manual **Install** clears that record.

A disabled plugin is not loaded and its companions are no longer added to servers.

## Implicit loading

A hub entry with `<ImplicitLoading>true</ImplicitLoading>` installs itself: on each hub refresh, Quasar installs it if it is not on disk and you have not removed it. A first implicit install is enabled. An implicit plugin you disabled stays disabled. Once installed, it is updated only under the automatic update policy below. Safe boot pauses implicit installs.

## Automatic updates

Automatic UI plugin updates are off by default. Turn them on with **Automatically update installed UI plugins and restart Quasar** on the **UI Plugins** page. The setting is `Quasar:Updates:AutoUpdateUiPlugins` in the install-root `appsettings.json`; the `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS` environment variable overrides it at startup. See [Updates](Updates.md).

With the policy on, each 15-minute hub check updates a plugin when all of these hold:

- Quasar has verified native restart capability for the current process, or runs under the legacy Bootstrap launcher
- the plugin is installed, enabled and not invalid
- the hub pins a different commit than the installed one
- the hub entry's `QuasarVersion` is empty, an exact version equal to the running Quasar, or a `>=` minimum the running Quasar meets (other forms need a manual update)
- safe boot is off

After a successful automatic update, Quasar restarts itself the same way as **Restart Quasar**. If capability becomes unavailable, the restart stays pending until verification succeeds again. If a Quasar application update is pending, automatic plugin restarts wait so they cannot activate it silently. An admin can confirm the combined update and restart from either restart control. Running game servers stay online. Failed builds keep the old package and are logged as "Scheduled Quasar UI plugin {name} install or update failed."

To get a notice instead, enable **Quasar UI plugin updates** on the **Notifications** page. That notice lists installed plugins whose hub commit changed. See [Monitoring](Monitoring.md).

That preference also enables bell, list and subscribed browser push reminders when a successful update to a plugin with a Magnetar companion affects an online server. Open **UI Plugins** to see the restart guidance and clear those reminders for your account. A reminder also clears when all affected servers are offline or all affected managed standalone servers have restarted since the update. Restart Quasar to load the update, then restart affected standalone servers. Clusters use companions pinned in their deployment. First installs, failed updates, UI-only plugins and same-revision reinstalls do not create new reminders. Quasar remembers which revisions you reviewed across restarts. Browser push is sent once per installed revision per subscription.

## .NET 10 SDK

Running Quasar needs only the ASP.NET Core runtime, but building UI plugins from source needs a .NET 10 SDK. Quasar looks for one in this order:

1. `dotnet` on `PATH` with any 10.x SDK. If found, Quasar uses it and never downloads its own.
2. Quasar's private SDK in `{Quasar install directory}/ManagedRuntime/Tools/DotNetSdk/10.0.111`.

If neither exists, the page shows ".NET SDK not currently available." and **Install** asks what to do:

- **Download private SDK**: Quasar downloads the pinned .NET SDK 10.0.111 from `builds.dotnet.microsoft.com`, checks its SHA-512, extracts it into the managed folder and continues the install. It does not change system packages or `PATH`. Downloads are staged under `{Quasar install directory}/ManagedRuntime/Cache/DotNetSdk`. Supported on Linux and Windows, x64 and Arm64.
- **Install manually**: cancels the plugin install. Install the .NET 10 SDK with your package manager (winget on Windows, your distribution's package manager on Linux), then click **Refresh SDK check**.
- **Cancel**: nothing changes.

Quasar never downloads the SDK without that confirmation. The Docker image is built on the .NET 10 SDK image, so containers always use the SDK on `PATH`. See [Docker](Docker.md).

## Safe boot

Safe boot starts Quasar without loading any UI plugin assemblies, static files, endpoints or contributions. Core pages keep working. Use it when a plugin breaks the web UI.

Safe boot is on when any of these is set:

| Trigger | How |
| --- | --- |
| Marker file | `{Quasar install directory}/ui-plugins.safe-mode` exists. **Enable marker** and **Clear marker** on the UI Plugins page create and delete it. |
| Environment | `QUASAR_SAFE_MODE` or `QUASAR_DISABLE_UI_PLUGINS` set to `1`, `true`, `yes` or `on` |
| Configuration | `Quasar:Plugins:SafeMode` = `true` |
| Command line | `--safe-mode=true` |
| Bootstrap | Legacy installs only: Bootstrap creates the marker file after 3 worker startup failures within 5 minutes (the worker does not become healthy, or exits with an error soon after start) and starts the worker again. Native service installs (1.4.1 and later) have no Bootstrap, so nothing creates the marker for you. |

The page lists the active reasons. Environment and configuration triggers must be removed outside the page. Every change needs a Quasar restart. While safe boot is on, automatic and implicit plugin installs pause.

To recover from a plugin that keeps Quasar from starting: create the marker file by hand if the UI is down, restart Quasar, disable or remove the plugin on **UI Plugins**, delete the marker with **Clear marker**, and restart again.

## Headless mode

In headless mode (`Quasar serve --headless`, `Quasar:Headless=true` or `QUASAR_HEADLESS=true`) Quasar has no web UI, so it loads no UI plugin assemblies and has no **UI Plugins** page. It does not refresh QuasarHub, so no implicit installs or automatic updates happen. Quasar still reads the manifests of enabled installed plugins, so their companion plugins keep being deployed to servers. See [Configuration](Configuration.md).

## Companion plugins

A UI plugin's `quasar-plugin.json` can list companion Magnetar plugins in `companionPlugins`:

- A string names an existing Magnetar plugin the UI plugin talks to. Quasar does not install it; select it in a config profile like any other server plugin ([Magnetar server plugins](Plugins.md)).
- An object with `projectPath` is owned by the UI plugin. Quasar builds it during the UI plugin install into `{package}/.quasar/companions/{companion id}`, for `x64`, against the running worker's `Magnetar.Protocol.dll` and, when installed, Quasar's managed Dedicated Server (`DS64`) and Magnetar (`Magnetar`) folders. `entryAssembly` is optional and defaults to `{project file name}.dll`.

When a server starts, Quasar copies the built companions of every enabled UI plugin that loaded without error into the server's Magnetar `Local` folder and adds their entry assemblies to the server's Magnetar profile. Every managed server gets them; there is no per-server choice. Quasar skips `Quasar.Agent.dll`, `Magnetar.Protocol.dll`, `0Harmony.dll`, `PluginSdk.dll` and `Quasar.Plugin.Abstractions.dll` from the companion output because it deploys its own copies. Disabling the UI plugin drops the companion from the profile at the next server start; the copied DLL files stay in `Local` unused.

Because the package list is fixed at worker start, a newly installed UI plugin's companion reaches servers after a Quasar restart and then a server start.

For clusters, guided setup includes owned companions only of UI plugins installed from QuasarHub and pins them to the installed commit. Setup leaves out companions of other UI plugins and names them. See [Clusters](Clusters.md).

### Companion data channel

UI plugins get live data from their companions through `IQuasarCompanionChannel`, which Quasar registers as a service. It rides on the existing Quasar.Agent connection:

- `GetConnectedServers()` lists connected servers with a `ServerId`, the current `AgentId` and a display name. For standalone servers the `ServerId` is the server's unique name, which stays the same across restarts; for cluster nodes it identifies one node incarnation. Cluster entries also include an explicit `ClusterId`, configured `ClusterDisplayName` and stable `ClusterSlot`. Entity Viewer uses these fields to show one server per cluster, combine retained node histories and apply recording settings to current and future nodes. Node commands still use exact agent IDs. Install matching Quasar and Entity Viewer versions for this grouping.
- `SendAsync(serverId, companionPluginId, operation, request, ...)` sends a JSON request to the companion and returns its JSON response. Pass a `ServerId` from `GetConnectedServers()` or a live `AgentId`. Standalone servers also accept their server key or unique name. Unknown, disconnected or ambiguous ids fail; Quasar never picks a different server.
- On the server, the agent hands the request to the loaded Magnetar plugin whose `IQuasarCompanionRequestHandler.PluginId` matches. Requests time out after 30 seconds. The agent runs up to 4 companion requests at once, beside its other commands, so a slow companion doesn't delay saves or stops. A handler that throws, including its own timeout or cancellation, fails only that request; the agent stays connected.
- A hub plugin, which Magnetar compiles without Magnetar.Protocol, can't implement that interface. It exposes two public members instead: `string QuasarCompanionPluginId { get; }` and `Task<string> HandleQuasarCompanionRequestAsync(string operation, string payloadJson, CancellationToken cancellationToken)`, which returns the response JSON. An exception it throws, or a null result, comes back as a failed response. The interface wins when a plugin has both.

Errors a plugin author or admin may see:

| Error | Cause |
| --- | --- |
| "No connected agent matched server id '...'." | Server not connected, or the id is ambiguous |
| "Companion plugin '...' is not loaded or does not expose a Quasar handler." | The companion is not in the server, failed to load, or does not implement the handler |
| "The companion plugin returned a mismatched correlation id." / "...an invalid response." | Protocol mismatch between UI plugin and companion versions |
| "Agent command response is too large (N bytes; limit M bytes)." | The companion's reply is over the 64 MiB agent message limit. The agent sends this failure instead and stays connected |

A navigation item's policy only hides the link. Each plugin's pages and endpoints must enforce their own authorization.

## Files and settings

| Path or key | Purpose |
| --- | --- |
| `{install}/Plugins/{catalog id}/` | Installed UI plugin packages |
| `{install}/ui-plugins.state.json` | Enabled/disabled state, removed implicit plugins |
| `{install}/ui-plugins.safe-mode` | Safe boot marker |
| `{install}/Caches/ui-plugin-hub-catalog.json` | QuasarHub catalog cache |
| `{install}/Caches/ui-plugin-installer/` | Download and build staging |
| `{install}/Caches/ui-plugins/{plugin id}/{hash}/` | Shadow copy Quasar actually loads |
| `{install}/ManagedRuntime/Tools/DotNetSdk/10.0.111/` | Private .NET SDK |
| `QUASAR_UI_PLUGIN_DIRS`, `QUASAR_UI_PLUGIN_DIR`, `Quasar:Plugins:Directory` | Folders to scan for plugin packages (first one set wins; `DIRS` takes a path-separator list). Default `{install}/Plugins` |
| `QUASAR_UI_PLUGIN_BUILD_CONFIGURATION`, `Quasar:Plugins:BuildConfiguration` | Build configuration for installs and for finding the entry assembly |

Quasar loads a plugin from a shadow copy, so rebuilding files in the package folder does not affect the running worker. A package folder is either a folder with `quasar-plugin.json`, or a direct child of a scanned folder.

The QuasarHub installer always writes to `{install}/Plugins`. If you point `Quasar:Plugins:Directory` or the environment variables somewhere else, include `{install}/Plugins` in the list or hub-installed plugins are not loaded.

## Documented plans versus the current code

`Docs/QuasarPluginSystem.md` mixes plans with implemented behavior. The current code differs from it in these points:

- Extension points: Quasar renders outlets for `quasar.page.entities`, `quasar.component.entity-actions`, `quasar.entities.viewer-column-header`, `quasar.entities.viewer-column-cell` and `quasar.page.plugins` (bottom of **UI Plugins**). The constants for dashboard panels, entity detail tabs, server detail actions and the analytics page exist but nothing renders them. The finer outlets listed in the plan (toolbar actions, summary chips and so on) do not exist.
- Conflict rules: when several plugins `Replace` the same target, Quasar renders all of them in priority order. There is no single winner and no startup error.
- Navigation zones: plugins can use `nav.main`, `nav.tools`, `nav.settings` and `nav.admin`. Admin links default to `CanManageSecurity`. `nav.hidden` is never rendered.
- Trust checks: Quasar does not show a plugin's contributions before enabling it, does not ask for extra approval for plugins that replace pages, and does not show update diffs. It records the installed commit, not a package hash.
- Last-known-good activation: Quasar does not mark a plugin set as good after a healthy start. The only automatic fallback is the Bootstrap crash-loop safe boot marker, which native service installs don't have.
- Hub fields `DependencyIds`, `CompanionPluginIds` and `Platforms`, and the package manifest's `dependencies`, are read but not acted on. `QuasarVersion` only gates automatic updates; a manual install does not check it.

## Native UI kit for plugins

The dashboard's native controls ship in `Quasar.Ui.Abstractions.dll`, so a browser plugin can use the same buttons, fields, tables, tabs and menus as the dashboard without MudBlazor, and the same notices from `Quasar.Ui.Components`. Controls are in `Quasar.Ui.Components`; shared vocabulary (`QuasarTone`, `QuasarVariant`, `QuasarSize`) is in `Quasar.Ui`. Text and number fields commit on change; set `Immediate` to commit while typing, after a short pause. Field validation is asynchronous and can return several messages; `QuasarForm` aggregates validity and dirty state. Tables take in-memory `Items` or a `LoadData` callback, scroll sideways on narrow screens, and can stack rows into labelled cards instead. Styles come from the host's `quasar-ui.css`, and its `quasar-ui.js` adds keyboard handling for tabs, menus and tooltips. See the "Native controls" section of `Docs/QuasarPluginSystem.md`.

## Common problems

- **".NET 10 SDK was not found globally or in Quasar managed storage."** Install the SDK from the prompt or with your package manager, then **Refresh SDK check**.
- **"Managed .NET SDK installation is not supported on ..."** Only Linux and Windows on x64 or Arm64 can use the private SDK. Install the .NET 10 SDK yourself.
- **"Downloaded .NET SDK archive failed SHA-512 verification."** The download was corrupted or altered. Try again; Quasar does not install an unverified SDK.
- **"Quasar.Plugin.Abstractions.dll was not found on disk."** The worker's install is incomplete; the DLL must sit beside the worker. Reinstall or update Quasar.
- **"Magnetar.Protocol.dll was not found on disk."** Same for companion builds; it is looked up beside the worker and in its `Agent` folder.
- **"Only root quasar-plugin.json package manifests are supported in this installer."** The hub entry's `PackageManifest` points elsewhere. The plugin author must fix it.
- **"Quasar plugin manifest ... is missing '...'."** `quasar-plugin.json` lacks a required field: `id`, `displayName`, `version`, `entryAssembly`, `entryType`, `projectPath`, and `companionPlugins[].id` (plus `projectPath` for owned companions).
- **Build output in the error message.** `dotnet build` failed; the message is the compiler output. The old package stays installed.
- **Restart is disabled or rejected.** Read the restart capability and repair instructions on Updates. Native service verification must belong to this Quasar process. Missing, stale, unreadable or denied verification leaves Quasar running; repair the existing services with the current installer, or restart a foreground process by hand. With a pending or staged native update, the restart control warns which version will be installed and asks you to confirm **Update and Restart Quasar**. Cancel leaves the update unchanged. Active preparation, cutover, recovery or another queued restart still prevents a second restart.
- **A plugin is installed but not loaded.** Restart Quasar, then check **Load Errors** and the Quasar log for "Quasar UI plugin safe mode is enabled" or the plugin's load error. Typical load errors: "Quasar plugin entry assembly was not found", "Plugin entry type not found", "Plugin entry type does not implement IQuasarPlugin".
- **An update loaded but the browser still runs old plugin JavaScript.** Quasar sends plugin files with `Cache-Control: no-store`. A reverse proxy that caches anyway serves stale files: turn off **Cache Assets** in Nginx Proxy Manager, or add a Cloudflare cache rule to bypass `/_quasar/plugins/`, purge the cache once, and reload the page with the browser cache disabled.
- **Automatic updates never happen.** Check that the checkbox is on, Quasar runs as its installed service (or under Bootstrap), safe boot is off, the plugin is enabled, and its hub `QuasarVersion` is empty, exact or `>=`.

## Sources
- `README.md`
- `Dockerfile`
- `Docs/QuasarPluginSystem.md`
- `Docs/Configuration.md`
- `Docs/Docker.md`
- `Quasar/Program.cs`
- `Quasar/Services/Plugins/QuasarUiPluginCatalog.cs`
- `Quasar/Services/Plugins/QuasarUiPluginHubCatalogService.cs`
- `Quasar/Services/Plugins/QuasarUiPluginHubRefreshService.cs`
- `Quasar/Services/Plugins/QuasarUiPluginStateStore.cs`
- `Quasar/Services/Plugins/QuasarPluginPackageManifestReader.cs`
- `Quasar/Services/Plugins/QuasarManagedDotNetSdkService.cs`
- `Quasar/Services/Plugins/QuasarLoadedUiPlugin.cs`
- `Quasar/Services/Discord/DiscordPluginInteractions.cs`
- `Quasar/Services/QuasarCompanionChannel.cs`
- `Quasar/Services/AgentRegistry.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/Updates/QuasarUpdateOptions.cs`
- `Quasar/Services/Updates/QuasarUpdateService.cs`
- `Quasar/Services/Updates/UpdateNotice.cs`
- `Quasar/Services/Updates/PushNotificationService.cs`
- `Quasar/Services/Plugins/QuasarUiPluginDashboardOperations.cs`
- `Quasar/Components/Layout/WasmPageLayout.razor`
- `Quasar/wwwroot/quasar-configs.js`
- `Quasar/wwwroot/app.css`
- `Quasar/wwwroot/quasar-ui.css`
- `Quasar/wwwroot/quasar-ui.js`
- `Quasar.Ui.Abstractions/Dialogs/IQuasarDialogService.cs`
- `Quasar.Ui.Abstractions/Dialogs/QuasarDialogOptions.cs`
- `Quasar.Ui.Abstractions/Toasts/IQuasarToastService.cs`
- `Quasar.Ui.Abstractions/Toasts/QuasarToastOptions.cs`
- `Quasar.Ui.Abstractions/Components/Feedback/QuasarInlineNotice.razor`
- `Quasar.Ui.Abstractions/Components/Feedback/QuasarInlineNoticeVariant.cs`
- `Quasar.Ui.Abstractions/Components/Forms/QuasarFieldBase.cs`
- `Quasar.Ui.Abstractions/Components/Tables/QuasarTable.razor`
- `Quasar.Ui.Abstractions/Components/Patterns/QuasarCopyablePath.razor`
- `Quasar.Client/Components/Shared/QuasarDialogHost.razor`
- `Quasar.Client/Components/Shared/QuasarDialogInstanceHost.razor`
- `Quasar.Client/Components/Shared/QuasarDialogContentHost.razor`
- `Quasar.Client/Components/Shared/QuasarDialogFrame.razor`
- `Quasar.Client/Components/Shared/QuasarToastHost.razor`
- `Quasar.Client/Components/Shared/QuasarToastItem.razor`
- `Quasar.Client/Ui/ServiceCollectionExtensions.cs`
- `Quasar.Client/Ui/Dialogs/QuasarDialogService.cs`
- `Quasar.Client/Ui/Dialogs/QuasarDialogEntry.cs`
- `Quasar.Client/Ui/Dialogs/QuasarDialogCancellation.cs`
- `Quasar.Client/Ui/Dialogs/QuasarMessageBoxDialog.razor`
- `Quasar.Client/Ui/Toasts/QuasarToastService.cs`
- `Quasar/Components/Layout/PluginNavItems.razor`
- `Quasar/Services/Plugins/QuasarNavIconSanitizer.cs`
- `Quasar.Client/UiPlugins/UiPluginsView.razor`
- `Quasar.Client/UiPlugins/PluginCatalogDescriptionDialog.razor`
- `Quasar.Client/UiPlugins/PluginCatalogDescriptionDialog.razor.css`
- `Quasar.Client/UiPlugins/BrowserSdkAuthorization.razor`
- `Quasar.Client/Administration/NotificationPage.razor`
- `Quasar.Client/Components/EntitiesView.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Components/Layout/PluginNavItems.razor`
- `Quasar.Client/Components/BrowserExtensionOutlet.razor`
- `Quasar.Bootstrap/Program.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Docs/QuasarUpdates.md`
- `Magnetar.Protocol/Transport/AgentMessageLimits.cs`
- `Quasar.Agent/GameBridge.cs`
- `Quasar.Plugin.Abstractions/IQuasarPlugin.cs`
- `Quasar.Plugin.Abstractions/QuasarPluginContext.cs`
- `Quasar.Plugin.Abstractions/Companion/IQuasarCompanionChannel.cs`
- `Quasar.Plugin.Abstractions/Manifests/QuasarPluginManifest.cs`
- `Quasar.Plugin.Abstractions/Manifests/QuasarCompanionPluginManifest.cs`
- `Quasar.Plugin.Abstractions/Navigation/QuasarNavItem.cs`
- `Quasar.Plugin.Abstractions/Navigation/QuasarNavZones.cs`
- `Quasar.Plugin.Abstractions/Extensions/QuasarExtensionContribution.cs`
- `Quasar.Plugin.Abstractions/Extensions/QuasarExtensionTargets.cs`
- `Quasar.Plugin.Abstractions/Extensions/QuasarPatchMode.cs`
- `Quasar.Plugin.Abstractions/Security/QuasarPolicyNames.cs`
- `Magnetar.Protocol/Bridge/IQuasarCompanionRequestHandler.cs`
- `Quasar.Agent/CompanionHandlers.cs`
- `Magnetar.Protocol/Model/CompanionPluginRequest.cs`
- `Magnetar.Protocol/Model/CompanionPluginResponse.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar.Tests/QuasarManagedDotNetSdkServiceTests.cs`
- `Quasar.Tests/QuasarUiPluginStateStoreTests.cs`
- `Quasar.Client/UiPlugins/QuasarOperationLoader.razor`
- `Quasar/wwwroot/quasar-ui.css`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar.Tests/QuasarShutdownTests.cs`
- `Quasar/Services/Plugins/QuasarCoreOperations.cs`
- `Docs/BrowserSdkRollout.md`
