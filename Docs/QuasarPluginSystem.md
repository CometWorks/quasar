# Quasar Plugin System Plan

Quasar plugins are trusted UI extensions loaded by the Quasar web host. They are
separate from Magnetar Dedicated Server plugins. The first target plugin is the
Entity Viewer: a heavy, mostly independent UI surface that should live outside the
Quasar core repository while still feeling native inside Quasar.

## Goals

- Keep Quasar core small while allowing large feature surfaces to ship
  independently.
- Let plugins add nav items, routable Razor pages, dialogs, dashboard panels, and
  entity/server actions.
- Let plugins replace, wrap, or inline selected built-in Quasar pages,
  components, and smaller named UI regions through explicit extension targets.
- Give UI plugins a generic request/event channel to companion Magnetar plugins
  running inside managed Dedicated Servers.
- Keep the UI visually consistent by making MudBlazor the default component
  library and theme surface for all Quasar plugins.

## Repository Roles

- `Quasar`
  - Loads trusted Quasar UI plugins.
  - Owns auth, layout, routing, MudBlazor theme, and the agent WebSocket.
  - Exposes `Quasar.Plugin.Abstractions`.
- `quasar-hub`
  - Holds reviewed XML manifests, similar to MagnetarHub.
  - Manifests point to the individual plugin repositories and pinned commits.
  - It does not host plugin source code.
- `quasar-plugin-template`
  - Starting point for new Quasar UI plugin repositories.
  - Shows manifest, project layout, MudBlazor usage, nav contributions, page
    contributions, and companion-channel usage.
- Plugin repositories
  - Host source for one Quasar UI plugin.
  - May also host shared DTO/contracts used by companion Magnetar plugins.

## Runtime Model

Quasar UI plugins are loaded into the Quasar process during startup. They are
trusted code and have the same process privileges as Quasar. The first version
should require a Quasar restart after install, update, enable, or disable.

Runtime hot-unload is not a first target. Blazor routable components, static
asset mounts, and dependency injection registrations are easier to reason about
when plugin lifetime is tied to the Quasar host lifetime.

Dynamic enable/disable can still be user-friendly because Quasar already has a
bootstrap/launcher flow. The UI can update plugin configuration, ask the worker
to restart, and let Bootstrap bring the worker back with a clean plugin assembly
set.

## Safe Boot

Quasar must have a way to start without loading dynamic UI plugin assemblies.
This prevents one bad plugin from permanently bricking the web UI.

Safe-boot triggers:

- command line: `--safe-mode`
- environment variable: `QUASAR_SAFE_MODE=1`
- environment variable: `QUASAR_DISABLE_UI_PLUGINS=1`
- data-directory marker file: `{Quasar data directory}/ui-plugins.safe-mode`
- automatic Bootstrap fallback after repeated worker startup failures or quick
  crashes

Safe-boot behavior:

- do not load plugin assemblies
- do not mount plugin static assets
- do not register plugin endpoints
- ignore plugin route/nav/patch contributions
- keep core Quasar pages available
- show safe-mode status on `/settings/ui-plugins`
- expose recovery controls for the marker-file safe boot path

Plugin activation should be last-known-good:

1. User installs/enables/updates a plugin.
2. Quasar writes enabled/disabled state to
   `{Quasar data directory}/ui-plugins.state.json`.
3. Quasar restarts through Bootstrap.
4. The new worker loads plugins.
5. After startup and health checks pass, Quasar marks the plugin set active.
6. If startup fails repeatedly, Bootstrap creates the safe-mode marker and
   starts Quasar without dynamic UI plugins.

Disabling a plugin is then config-first: mark disabled, restart worker, load a
fresh process without that plugin.

## Package Manifest

Each plugin repository should contain a `quasar-plugin.json` manifest. Quasar
uses this after resolving a plugin from `quasar-hub`.

Example:

```json
{
  "id": "cometworks.entityviewer",
  "displayName": "Entity Viewer",
  "version": "0.1.0",
  "entryAssembly": "CometWorks.EntityViewer.Quasar.dll",
  "entryType": "CometWorks.EntityViewer.Quasar.EntityViewerQuasarPlugin",
  "projectPath": "src/CometWorks.EntityViewer.Quasar/CometWorks.EntityViewer.Quasar.csproj",
  "staticAssets": "src/CometWorks.EntityViewer/wwwroot",
  "stylesheets": [
    "quasar-plugin.css"
  ],
  "quasarVersion": ">=0.1.0",
  "companionPlugins": [
    {
      "id": "cometworks.entityviewer",
      "projectPath": "src/CometWorks.EntityViewer.Magnetar/CometWorks.EntityViewer.Magnetar.csproj",
      "entryAssembly": "CometWorks.EntityViewer.Magnetar.dll"
    },
    "GridBackups"
  ]
}
```

`companionPlugins` accepts either a string id or an object. A string names an
external Magnetar plugin that the UI plugin can call through the companion
channel. An object with `projectPath` is owned by the UI plugin package: Quasar
builds that Magnetar companion project during UI plugin install and deploys it
automatically to managed servers while the UI plugin is enabled. `entryAssembly`
is optional; when omitted, Quasar derives `{project file name}.dll`.

The `quasar-hub` XML manifest remains the public catalog entry. The package
manifest is the plugin repository's runtime/build descriptor.

## Hub Discovery and Management

Quasar discovers reviewed UI plugin packages from
`https://github.com/CometWorks/quasar-hub` on the `main` branch. The hub follows
the MagnetarHub pattern: XML files under `Plugins/` point at individual plugin
repositories and pinned commits. Quasar only shows entries whose `PluginKind` is
`QuasarUiPlugin`.

The `/settings/ui-plugins` page now manages the QuasarHub catalog:

- refreshes and caches the catalog in
  `{Quasar data directory}/Caches/ui-plugin-hub-catalog.json`
- checks QuasarHub on Quasar startup and every 15 minutes so installed package
  update availability stays current
- automatically installs or updates reviewed hub entries that opt into
  `ImplicitLoading`; disabled installed plugins stay disabled across implicit
  updates
- shows installed, update-available, hidden, and invalid package states
- enables or disables installed packages for the next restart
- clones/fetches the plugin repository and checks out the pinned commit
- builds the declared plugin project with `dotnet build`
- passes `QuasarPluginAbstractionsAssembly` to the build so the plugin compiles
  against the contract DLL loaded by the running Quasar worker
- builds any owned Magnetar companion projects declared in `companionPlugins`
  objects, passing `MagnetarProtocolAssembly` so they compile against the
  protocol assembly used by Quasar.Agent
- removes local plugin packages
- links back to the plugin repository and QuasarHub

Installed UI plugin source packages live under:

```text
{Quasar data directory}/Plugins/{catalog id}
```

Installer staging lives under:

```text
{Quasar data directory}/Caches/ui-plugin-installer
```

Git source cache lives under:

```text
{Quasar data directory}/Caches/ui-plugin-sources
```

Enabled/disabled state lives under:

```text
{Quasar data directory}/ui-plugins.state.json
```

The first installer supports root `quasar-plugin.json` package manifests. Build
configuration comes from `QUASAR_UI_PLUGIN_BUILD_CONFIGURATION`,
`Quasar:Plugins:BuildConfiguration`, or `Debug` in development and `Release`
otherwise.
The installer passes the running worker's physical
`Quasar.Plugin.Abstractions.dll` to adapter builds as
`QuasarPluginAbstractionsAssembly`. Release packaging keeps that DLL beside the
single-file worker so Bootstrap-managed installs can build UI plugins from
QuasarHub without needing a NuGet package.
Owned companion build output is written under
`{installed package}/.quasar/companions/{companion id}`. Quasar passes
`MagnetarProtocolAssembly` from the running worker or staged `Agent/` directory
and builds companions for `x64`, matching Space Engineers Dedicated Server.
When a managed server is prepared, enabled UI plugin companions are copied into
that server's Magnetar `Local` folder and their entry assemblies are added to
the generated Magnetar `Current.xml` profile. Shared loader/protocol files such
as `Magnetar.Protocol.dll`, `Quasar.Agent.dll`, `0Harmony.dll`, and
`PluginSdk.dll` are not copied from companion output; Quasar deploys its own
agent/protocol files.
At startup, Quasar resolves plugin dependencies from the plugin's shadow-copied
build output and records contribution enumeration failures as plugin load errors
instead of letting one plugin crash the worker during catalog construction.

Install, update, and remove operations change files on disk only. The active
plugin assembly set is still loaded at Quasar worker startup, so each operation
requires a Quasar restart before it takes effect. When that restart is triggered
from the UI Plugins page, the browser shows restart progress and polls
`/api/health` until the replacement worker is ready.

QuasarHub descriptors can set `<ImplicitLoading>true</ImplicitLoading>` for
reviewed plugins that should be present by default. Quasar installs or updates
those entries during hub refresh, except in safe mode. A first implicit install
is enabled for the next restart; if an already-installed plugin was explicitly
disabled, implicit updates keep it disabled.

## Plugin Abstractions

Quasar exposes the initial contract project as `Quasar.Plugin.Abstractions`.
The core entry point is:

```csharp
public interface IQuasarPlugin
{
    string Id { get; }
    string DisplayName { get; }

    void ConfigureServices(IServiceCollection services, QuasarPluginContext context);
    void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context);

    IEnumerable<Assembly> GetRazorAssemblies();
    IEnumerable<QuasarNavItem> GetNavItems();
    IEnumerable<QuasarExtensionContribution> GetExtensions();
}
```

The first contract surface includes:

- `IQuasarPlugin`
- `QuasarPluginContext`
- `QuasarPluginManifest`
- `QuasarNavItem`
- `QuasarNavZones`
- `QuasarExtensionContribution`
- `QuasarExtensionTargets`
- `QuasarPatchMode`
- `QuasarPolicyNames`
- `IQuasarCompanionChannel`
- companion request/response envelopes

Service registration happens before `WebApplication.Build()`. Endpoint
registration happens after the app is built. Razor assemblies are passed to the
Blazor router and Razor component endpoint builder so plugin pages can be routed
normally.

The Quasar host has an initial no-op catalog at
`Quasar.Services.Plugins.QuasarUiPluginCatalog`. It provides the stable
integration seam for:

- safe-mode detection
- plugin DI registration
- plugin endpoint registration
- plugin static asset mounting
- sidebar navigation rendering
- router `AdditionalAssemblies`
- Razor component endpoint `AddAdditionalAssemblies`

The catalog also performs first-pass local package discovery and shadow-copy
loading. It scans plugin package roots for `quasar-plugin.json`, loads the
declared entry assembly from a cache folder, and exposes any load failures in
startup logs and the `/settings/ui-plugins` page.

The `/settings/ui-plugins` page also shows loaded plugin static asset paths,
declared stylesheet paths, nav contributions, extension contributions, and
startup load errors. It can create or clear the safe-boot marker, disable a
loaded package for the next restart, and manage QuasarHub-installed packages.

Configuration/environment knobs:

- `Quasar:Plugins:Directory`
- `Quasar:Plugins:BuildConfiguration`
- `QUASAR_UI_PLUGIN_DIR`
- `QUASAR_UI_PLUGIN_DIRS`
- `QUASAR_UI_PLUGIN_BUILD_CONFIGURATION`

Default plugin root:

```text
{Quasar data directory}/Plugins
```

Shadow-copy root:

```text
{Quasar data directory}/Caches/ui-plugins/{pluginId}/{entryAssemblyHash}
```

Plugin navigation items are rendered by the main Quasar sidebar. Supported zones:

- `QuasarNavZones.Main`
- `QuasarNavZones.Settings`
- `QuasarNavZones.Admin`

`QuasarNavZones.Admin` inherits `QuasarPolicyNames.CanManageSecurity` when the
plugin item does not specify its own policy. Navigation policy checks only hide
links; plugin pages and endpoints must still enforce their own authorization.

Extension contributions are rendered through named outlets. The first active
hosted targets are:

- `QuasarExtensionTargets.EntitiesPage`
- `QuasarExtensionTargets.EntityActions`
- `QuasarExtensionTargets.EntityViewerColumnHeader`
- `QuasarExtensionTargets.EntityViewerColumnCell`
- `QuasarExtensionTargets.PluginsPage`

`EntitiesPage` wraps the body of the `/entities` page, so a plugin can render
before/after it, wrap it with a component that accepts `ChildContent`, or replace
it entirely. `Wrap` contributions receive `ChildContent`; when a wrapper is not
authorized, Quasar renders the original child content. `EntityActions` renders
inside each entity row action cell. `EntityViewerColumnHeader` and
`EntityViewerColumnCell` expose the dedicated viewer button column so a viewer
plugin can add the viewer header/button and fullscreen dialog without replacing
the whole entities page. Their component parameters are intentionally primitive:

- `AgentId`
- `ServerName`
- `EntityId`
- `EntityType`
- `EntitySubType`
- `EntityDisplayName`
- `OwnerSteamId`
- `CanViewEntity`

This lets viewer plugins render a button and open their own dialog/static asset
route without taking a dependency on Quasar page internals. Quasar core does not
ship a fallback browser viewer route.

`PluginsPage` renders at the bottom of `/settings/ui-plugins`. It receives
parameters named `UiPlugins`, `LoadedPlugins`, and `LoadErrors` so plugins can
append diagnostics or management panels without replacing the core status page.

## Dependency Injection and Plugin State

Plugins must be able to register their own services during Quasar startup.
`ConfigureServices()` is the intended hook for this.

Allowed registrations:

- singleton state owned by the plugin
- scoped UI/session services
- transient helpers
- typed HTTP clients
- hosted/background services, when clearly bounded and cancellable
- companion-channel clients wrapping `IQuasarCompanionChannel`

Rules:

- Plugin DI registration happens only during startup, before
  `WebApplication.Build()`.
- Enabling, disabling, installing, or updating a plugin requires a worker
  restart so DI returns to a clean state.
- Plugin singletons live for one Quasar worker process lifetime.
- Safe boot skips plugin `ConfigureServices()`, so plugin services are not loaded
  when recovering from a bad plugin.
- Plugins should namespace their service types and options to avoid collisions.
- Plugins should not replace Quasar core services unless Quasar exposes an
  explicit replacement/decoration target.
- Plugins should prefer `TryAdd*` or plugin-specific wrapper services when
  sharing infrastructure.

Recommended plugin layout:

```text
Plugin.Ui/
  Components, pages, UI services, singleton state
Plugin.Quasar/
  Thin adapter implementing IQuasarPlugin and calling AddPluginUi()
PreviewHost/
  Local MudBlazor app that calls the same AddPluginUi()
```

This keeps component previews honest: the preview host and Quasar adapter use the
same plugin services.

## Routing

Quasar currently uses one Blazor `Router` in `Components/Routes.razor`. The
plugin loader should collect plugin Razor assemblies and expose them to the
router:

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="@PluginCatalog.RazorAssemblies"
        NotFoundPage="typeof(Pages.NotFound)">
```

Quasar should also pass plugin assemblies to Razor component endpoint mapping in
`Program.cs`.

Plugins can then provide normal routable Razor components:

```razor
@page "/entity-viewer"
```

## Navigation

Replace hardcoded nav markup with a contribution model. Core Quasar pages should
also become built-in contributions so plugin and core nav use the same path.

```csharp
public sealed record QuasarNavItem(
    string Text,
    string Href,
    string Icon,
    string Zone,
    int Order,
    string? Policy);
```

Suggested zones:

- `nav.main`
- `nav.settings`
- `nav.admin`
- `nav.hidden`

The nav renderer applies auth policies, sort order, and MudBlazor `MudNavLink`
styling. Plugins should provide Material icon names from MudBlazor when
possible.

## Page and Component Replacement

Quasar should support explicit patch targets instead of unsafe global monkey
patching. Built-in pages/components that can be replaced get stable target keys.

```csharp
public sealed record QuasarComponentPatch(
    string TargetKey,
    Type ComponentType,
    QuasarPatchMode Mode,
    int Priority,
    string PluginId,
    string? Policy);

public enum QuasarPatchMode
{
    Replace,
    Before,
    After,
    Wrap
}
```

Route-level replacement should use route hosts. For example, the current
`/entities` route becomes a thin wrapper:

```razor
@page "/entities"
<QuasarPageHost PageKey="quasar.page.entities"
                DefaultComponent="typeof(EntitiesPage)" />
```

The existing page body moves into `EntitiesPage.razor` without an `@page`
directive. A plugin can replace `quasar.page.entities` without competing for the
same route.

Interior extension targets use a component host:

```razor
<QuasarComponentHost TargetKey="quasar.component.entity-actions"
                     DefaultComponent="typeof(EntityActions)" />
```

Fine-grained inline regions are also feasible, but only where Quasar core places
a named outlet. Razor components are compiled, so Quasar should not try to patch
arbitrary markup or component declarations after compilation. Instead, core
components can expose stable region keys around small declarations that plugins
are allowed to extend or replace:

```razor
<QuasarExtensionOutlet TargetKey="quasar.entities.toolbar-actions"
                       Parameters="@ToolbarParameters">
    <MudButton StartIcon="@Icons.Material.Filled.Refresh"
               OnClick="LoadAsync">
        Refresh
    </MudButton>
</QuasarExtensionOutlet>
```

Useful inline region shapes:

- toolbar button groups
- filter controls
- summary chips
- row action buttons
- table columns or cell fragments
- details tabs
- empty/error/loading states
- dialog footers

Each region must document its parameter contract. Small region parameters should
prefer primitives, immutable DTOs, callbacks, and `RenderFragment` values over
page-private model types. This keeps plugins decoupled from the internal shape of
the Razor component.

For declaration replacement, the default declaration becomes the outlet's child
content. A plugin can use `Before`/`After` to inline adjacent UI, `Wrap` to
decorate the declaration through `ChildContent`, or `Replace` to suppress the
default declaration and render its own component. This gives most of the power of
specific Razor declaration replacement while keeping the patch surface explicit,
reviewable, and testable.

Conflict rules:

- One `Replace` winner per target.
- Highest priority wins.
- Same priority replacement conflict is a startup error or disables both
  replacements with a clear diagnostic.
- `Before` and `After` contributions are ordered by priority.
- The plugin manager must show which plugin patches which target.
- Fine-grained targets follow the same conflict rules as page/component targets.

The first useful targets:

- `quasar.page.entities`
- `quasar.page.plugins`
- `quasar.page.analytics`
- `quasar.dashboard.panels`
- `quasar.component.entity-actions`
- `quasar.component.entity-details-tabs`
- `quasar.component.server-detail-actions`
- `quasar.entities.toolbar-actions`
- `quasar.entities.summary-chips`
- `quasar.entities.table-columns`
- `quasar.entities.viewer-column-header`
- `quasar.entities.viewer-column-cell`
- `quasar.entities.empty-state`

## Static Assets

Plugins can ship static assets under their own package. Quasar should mount each
plugin's static asset root under a deterministic path:

```text
/_quasar/plugins/{pluginId}/
```

The Entity Viewer can keep its JavaScript/Three.js-heavy surface in its own
repository and serve it from that plugin path. Quasar core no longer copies
viewer assets into `Quasar/wwwroot`; the QuasarHub installer clones the pinned
viewer repository commit, builds the adapter project, and loads the package from
the Quasar data directory.

Plugins can also ask Quasar to inject package stylesheets into the host page by
declaring manifest-relative paths:

```json
"stylesheets": [
  "styles.css"
]
```

Relative stylesheet paths resolve under `staticAssets` and are emitted as:

```text
/_quasar/plugins/{pluginId}/{stylesheet}
```

Absolute `http://`, `https://`, or root-relative paths are allowed for trusted
plugins. Injected stylesheets load after Quasar core CSS, so plugin CSS must use
narrow selectors and MudBlazor/Quasar CSS variables instead of global resets.

## Companion Data Channel

UI plugins often need live server data from companion Magnetar plugins. Quasar
exposes a generic channel instead of feature-specific Quasar endpoints.

```csharp
public interface IQuasarCompanionChannel
{
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        CancellationToken cancellationToken);
}
```

The channel rides on the existing Quasar.Agent WebSocket transport. Quasar sends
`ServerCommandType.PluginRequest` to the selected agent; the command payload is a
generic `CompanionPluginRequest` with JSON owned by the caller and target
plugin. The agent dispatches the request to a loaded Magnetar plugin that
implements `Magnetar.Protocol.Bridge.IQuasarCompanionRequestHandler` and returns
a `CompanionPluginResponse`.

Envelope fields:

- `pluginId`
- `operation`
- `schemaVersion`
- `correlationId`
- `payloadJson`

Quasar performs web auth at the UI/plugin endpoint layer and binds requests to a
connected managed server. Companion plugins handle only their own operation
names and own the typed request/response DTOs for those operations.

UI-plugin-owned companions are local plugins from Quasar's perspective, not
MagnetarHub selections. Enabling a UI plugin enables its owned companion for all
managed servers prepared after the next Quasar restart. Disabling the UI plugin
removes that companion from subsequently generated Magnetar profiles, although
old copied DLLs can remain inert in the server's `Local` folder.

## Entity Viewer and GridBackups

Entity Viewer is the first Quasar UI plugin.

Entity Viewer owns:

- entity-list viewer button contribution
- fullscreen viewer shell
- static viewer assets
- audit/timeline UI
- typed request DTOs for viewer-specific operations
- companion Magnetar plugin handlers for viewer scene capture

GridBackups owns:

- backup files
- backup retention
- backup/restore events
- grid state serialization
- companion-channel handlers for grid audit, backup list, snapshot metadata, and
  future snapshot content

Entity Viewer should call its Magnetar companion plugin and GridBackups through
the companion channel. Quasar core does not ship a viewer scene API or
viewer-specific scene DTOs. GridBackups should not reference the Entity Viewer UI
assembly. If shared DTOs are useful, put them in a small contracts assembly that
both can reference.

## MudBlazor Requirement

Quasar plugins should use MudBlazor for all normal UI:

- navigation
- buttons
- icon buttons
- dialogs
- tables/data grids
- forms
- tabs
- menus
- alerts
- cards/papers
- progress indicators

Custom HTML/CSS is fine for domain-specific surfaces such as a Three.js viewer,
but the surrounding controls, dialogs, filters, and settings must use MudBlazor
and the current Quasar theme. Plugins should avoid competing CSS frameworks,
hard-coded color systems, and standalone app shells.

Quasar should pass its MudBlazor theme and layout expectations through the plugin
abstractions. Plugins should prefer theme tokens and MudBlazor spacing/color
primitives over custom palettes.

## Security and Trust

Quasar UI plugins are not sandboxed. They can execute arbitrary .NET code inside
the Quasar process. Therefore:

- load only reviewed plugins from `quasar-hub` by default
- pin plugin commits
- record package hashes after build/download
- show requested nav, endpoint, static asset, and patch contributions before
  enabling a plugin
- require admin approval for plugins that replace built-in pages
- make update diffs visible before applying them

## Implementation Phases

1. Add `Quasar.Plugin.Abstractions`.
2. Add plugin manifest loader and local plugin catalog.
3. Add plugin Razor assembly registration.
4. Convert nav to contribution-driven rendering.
5. Add static asset mounting.
6. Add page/component extension hosts.
7. Add fine-grained inline region outlets for stable declarations.
8. Add first patch targets on Entities and Dashboard.
9. Add generic companion data channel through Quasar.Agent.
10. Use Entity Viewer from its external Quasar UI plugin repository.
11. Add GridBackups companion handlers for audit/grid/snapshot requests.
