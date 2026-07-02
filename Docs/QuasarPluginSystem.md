# Quasar Plugin System Plan

Quasar plugins are trusted UI extensions loaded by the Quasar web host. They are
separate from Magnetar Dedicated Server plugins. The first target plugin is the
Grid Viewer: a heavy, mostly independent UI surface that should live outside the
Quasar core repository while still feeling native inside Quasar.

## Goals

- Keep Quasar core small while allowing large feature surfaces to ship
  independently.
- Let plugins add nav items, routable Razor pages, dialogs, dashboard panels, and
  entity/server actions.
- Let plugins replace or wrap selected built-in Quasar pages/components through
  explicit extension targets.
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
- data-directory marker file, for example `safe-mode`
- automatic crash-loop fallback after repeated plugin-load failures

Safe-boot behavior:

- do not load plugin assemblies
- do not mount plugin static assets
- do not register plugin endpoints
- ignore plugin route/nav/patch contributions
- keep core Quasar pages available
- show a visible safe-mode banner
- expose a recovery page to disable, roll back, or remove plugins

Plugin activation should be last-known-good:

1. User installs/enables/updates a plugin.
2. Quasar writes the requested plugin state as pending.
3. Quasar restarts through Bootstrap.
4. The new worker loads plugins.
5. After startup and health checks pass, Quasar marks the plugin set active.
6. If startup fails repeatedly, Bootstrap starts Quasar in safe boot.

Disabling a plugin is then config-first: mark disabled, restart worker, load a
fresh process without that plugin.

## Package Manifest

Each plugin repository should contain a `quasar-plugin.json` manifest. Quasar
uses this after resolving a plugin from `quasar-hub`.

Example:

```json
{
  "id": "cometworks.gridviewer",
  "displayName": "Grid Viewer",
  "version": "0.1.0",
  "entryAssembly": "CometWorks.GridViewer.QuasarPlugin.dll",
  "entryType": "CometWorks.GridViewer.QuasarPlugin.GridViewerPlugin",
  "projectPath": "src/CometWorks.GridViewer.QuasarPlugin/CometWorks.GridViewer.QuasarPlugin.csproj",
  "staticAssets": "wwwroot",
  "quasarVersion": ">=0.1.0",
  "companionPlugins": [
    "GridBackups"
  ]
}
```

The `quasar-hub` XML manifest remains the public catalog entry. The package
manifest is the plugin repository's runtime/build descriptor.

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
- `IQuasarCompanionChannel`
- companion request/response envelopes

Service registration happens before `WebApplication.Build()`. Endpoint
registration happens after the app is built. Razor assemblies are passed to the
Blazor router and Razor component endpoint builder so plugin pages can be routed
normally.

The Quasar host has an initial no-op catalog at
`Quasar.Services.Plugins.QuasarUiPluginCatalog`. It currently provides the
stable integration seam for:

- safe-mode detection
- plugin DI registration
- plugin endpoint registration
- plugin static asset mounting
- router `AdditionalAssemblies`
- Razor component endpoint `AddAdditionalAssemblies`

Dynamic manifest discovery and assembly loading are the next implementation
layer on top of this catalog.

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
@page "/grid-viewer"
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

Conflict rules:

- One `Replace` winner per target.
- Highest priority wins.
- Same priority replacement conflict is a startup error or disables both
  replacements with a clear diagnostic.
- `Before` and `After` contributions are ordered by priority.
- The plugin manager must show which plugin patches which target.

The first useful targets:

- `quasar.page.entities`
- `quasar.page.plugins`
- `quasar.page.analytics`
- `quasar.dashboard.panels`
- `quasar.component.entity-actions`
- `quasar.component.entity-details-tabs`
- `quasar.component.server-detail-actions`

## Static Assets

Plugins can ship static assets under their own package. Quasar should mount each
plugin's static asset root under a deterministic path:

```text
/_quasar/plugins/{pluginId}/
```

The Grid Viewer can keep its JavaScript/Three.js-heavy surface in its own
repository and serve it from that plugin path. Quasar core should stop copying
viewer assets into `Quasar/wwwroot` once the plugin model replaces the current
submodule staging path.

## Companion Data Channel

UI plugins often need live server data from companion Magnetar plugins. Quasar
should expose a generic channel instead of feature-specific Quasar endpoints.

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

The channel rides on the existing Quasar.Agent WebSocket transport. The agent
adds generic command envelopes:

- `PluginRequest`
- `PluginResponse`
- `PluginEvent`

Envelope fields:

- `pluginId`
- `operation`
- `schemaVersion`
- `correlationId`
- `payload`

Quasar performs web auth, rate limits requests, and binds requests to a managed
server. Companion plugins handle only their own operation names.

## Grid Viewer and GridBackups

Grid Viewer should become the first Quasar UI plugin.

Grid Viewer owns:

- nav/page/dialog contribution
- fullscreen viewer shell
- static viewer assets
- audit/timeline UI
- typed request DTOs for viewer-specific operations

GridBackups owns:

- backup files
- backup retention
- backup/restore events
- grid state serialization
- companion-channel handlers for grid audit, backup list, snapshot metadata, and
  future snapshot content

Grid Viewer should call GridBackups through the companion channel. GridBackups
should not reference the Grid Viewer UI assembly. If shared DTOs are useful, put
them in a small contracts assembly that both can reference.

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
6. Add `QuasarPageHost` and `QuasarComponentHost`.
7. Add first patch targets on Entities and Dashboard.
8. Add generic companion data channel through Quasar.Agent.
9. Move Grid Viewer into a Quasar UI plugin repository.
10. Add GridBackups companion handlers for audit/grid/snapshot requests.
