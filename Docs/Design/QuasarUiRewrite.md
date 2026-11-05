# Quasar UI rewrite assessment

Removing MudBlazor is feasible without replacing Blazor Server or the supervisor
backend. Budget approximately **30–45 engineering days** for one developer to
migrate the core UI, preserve behavior, and complete regression checks. This is a
planning estimate based on the current source, not a measured delivery schedule.
External UI plugin migrations are additional work, depending on their scope.

## Migration progress

Long operations use the shared native `QuasarOperationLoader`: a named activity,
live status, elapsed time and a progress bar. Percentages come only from reported
progress; elapsed time never generates an estimated percentage. At 30 seconds,
the loader adds a long-operation note. This covers cluster setup, maintenance and
conversion, Host enrollment, managed runtime installation on Dashboard and Updates,
private .NET SDK installation, UI plugin changes and service shutdown. Restart
feedback remains in browser JavaScript so it survives the worker stopping; its
progress bar counts completed restart stages alongside the existing status and
elapsed time. During a planned restart it also suppresses the normal reconnect
dialog and its competing reload attempts; clearing restart feedback restores normal
reconnection handling. Short authorization, entity-query and Workshop-search
indicators remain compact.

This branch targets Quasar **1.3.0**, with the shared version declared in
`Directory.Build.props`. The release workflow uses that version for upcoming PR
builds, allowing the core UI changes to be tested through the regular prerelease
update flow.

The approved visual overhaul now extends across the core workspace. Migrated surfaces:

- Native responsive shell, navigation, theme selector, notification/auth/power
  entry points, and shutdown overlay, with the current Quasar logo. The sidebar
  is 200px wide on desktop and in the mobile drawer, with 16px navigation labels,
  22px icons, larger click targets, and the shortened “Hosts” link. The logo,
  title, and subtitle stay docked at the top while the sidebar scrolls. The
  default subtitle is “Control plane”; saved custom subtitles remain unchanged.
  The top app bar stays docked while the page scrolls. Its 65px height matches
  the sidebar branding row; long branding text is ellipsized with the full text
  available on hover. Navigation controls stay
  vertically centered, and anchor scrolling leaves room for the docked bar.
- Native appearance form with all 29 fields in both palettes, presets, custom
  branding, separate mode-specific logo uploads, favicon, draft preview, save,
  reset, busy states, and invalid-color validation. RGB selection preserves alpha.
- Fleet overview with four summary tiles, compact server rows by default, a
  shared table/view toolbar, and larger page headings. Saved card/list choices
  and query overrides still work. Fleet totals combine standalone servers with cluster-wide presence and current
  node telemetry. Stale or missing readings remain unavailable.
- Simulation history reuses the analytics endpoint and uPlot with a 30-minute
  window, 10-second request throttle, gaps for missing samples, and theme-aware
  colors. Recent activity combines standalone lifecycle and command results with durable cluster operations, including in-progress and failed actions without connected agents. The simulation-speed chart includes stable cluster slot histories alongside standalone servers.
  These panels have explicit empty/error states; production data is not mocked.
- Native standalone/cluster server table with live search, status/type filters,
  resource metrics, optional technical columns, all previous sort choices,
  live detail expansion, copy/config links, restart/error summaries, and existing
  actions/disabled states. The **Game port** column shows and sorts the player port
  for standalone and cluster rows. Cluster ports use the active deployment's
  Steam listener; missing configuration shows **Unknown**. Unmanaged agent expansion
  remains available.

Additional migrated surfaces:

First-server setup remembers its active flow, selected world and manual Back step
in browser storage. Refreshes and server creation preserve the guide; dismissal
requires an explicit skip, cancel, close or completion action. The first choice
offers Single Magnetar, Import, and Cluster on Linux when authorized. Cluster
selection hands off to the existing guided wizard, with a resume panel on Dashboard.
Import refreshes require fresh source selection and confirmation; a completed
import resumes the setup guide if refreshed before Done.

Single-line outlined MudBlazor text inputs and select fields use the same shared
padding, so mixed forms such as Security's Provider, SteamID and Role row keep
aligned control heights. Multiline inputs retain their own sizing.

- Dashboard consent/setup wizard, errors, managed-runtime preparation, progress,
  retries, and copyable paths. Existing setup and lifecycle handlers are retained.
- Server and cluster cards use flat metric rows, structured world/host/profile
  metadata, compact action footers, and expandable live details. Advanced server
  actions use native menus; warning/error summaries remain visible. The grid
  adapts to available content width instead of a fixed two-column layout, and
  the workspace has no desktop width cap. All card rows share the tallest
  content height, including expanded details, with action footers aligned at
  the bottom. Content remains fully accessible without fixed-height clipping.
  Navigation labels use 14px text for readability. Narrow cards use two metric columns
  and contain player-table scrolling. Responsive browser checks cover widths
  from 320 to 3440 pixels: one column on phones, two at 1440, three at 1920,
  and six at 3440 with the six-server fixture. Twelve focused component tests
  cover this card refinement, retained commands, and visible cluster warnings.
- Live detail metrics, chat, player role actions,
  kick/ban confirmations, save guards, and command results. Native player
  popovers preserve their keyed elements across snapshots and use browser
  Escape/light-dismiss behavior. Confirmed player actions recheck permissions
  after the confirmation closes.
- Cluster overview/configuration links and administration controls, including
  settings, staged profiles, diagnostics, exports, handover, and capacity edits.
  The embedded PluginSdk configuration renderer still uses legacy controls.
- Cluster detail navigation groups Overview, Nodes & players, Mods & plugins,
  Maintenance and Settings. The native live health map uses SVG connections,
  resource bars, coverage-aware totals and service addresses. Existing lifecycle
  commands, confirmations and permissions are retained. See
  [Cluster health overview](../ClusterOverview.md) for data sources and limits.
- Notification preferences/browser push controls and public listing settings,
  retaining service validation, publication consent, optimistic revisions,
  missing-source handling, account scope, and permission checks.
- The sibling `CometWorks/viewer` UI package, adapter, and preview host now use
  native HTML and browser dialogs without a MudBlazor package. Existing scene
  rendering, streaming/installer APIs, consent, context URLs, and companion
  protocol are preserved. The iframe follows the enclosing native shell's
  palette, with legacy-host fallback and observer cleanup.
- The sibling `CometWorks/quasar-plugin-template` components, adapter, and preview
  host now have no MudBlazor dependency. They demonstrate scoped native controls,
  retained Material SVG icons, Quasar theme tokens, and updated plugin guidance.

The charcoal/cyan design is now the **Quasar default**, in both dark and light
modes. The CometWorks preset remains as an alias. On normalization, only a saved
`quasar` preset with both complete palettes matching the old defaults is
upgraded. Any edited color in either palette protects the pair; custom presets,
identity, logos, favicon, Midnight, Slate, and High Contrast retain their values.
All 29 fields remain editable in both modes. The live appearance preview includes
the configured mode-specific Quasar logo.

Operational pages, configuration/PluginSdk editors, host/cluster workflows, and
dialogs now share native semantic headings, panels, stacks, and dividers. The
remaining behavioral Mud tables, inputs, tabs, and dialogs receive the same
compact visual treatment. Their providers sit inside the themed shell so native
content in overlays inherits the palette. The server console interior uses
native log-source buttons, file selectors, downloads, and error/refresh controls;
existing log loading, history, auto-refresh, and keyboard rollover are retained.

The sidebar brand row and top bar share an inset separator so their horizontal
edges meet consistently at fractional browser zoom levels.

Configuration profiles use a native responsive grid and sidebar: the name field
and Create profile button stay together above a scrollable list. Profile rows
have left-aligned names, an explicit selected state, and separate labeled clone
and delete buttons. The sidebar stays in view on desktop and stacks above the
editor on smaller screens; the editor keeps normal page scrolling. Profile
selection still prompts for unsaved changes, and deletion keeps its confirmation.

Cluster expansion groups have enclosing borders and shaded expanded headers.
Nested plugin settings, advanced controls, and backup tools sit inside the parent
body's padding with a heavier leading border, making the hierarchy visible without
opening every section. Native disclosures and remaining Mud expansion panels use
the same grouping treatment; opening a parent no longer styles closed child headers
as expanded. Keyboard disclosure behavior and action permissions are unchanged.

`QuasarTheme` is the temporary bridge from stored palette strings to native CSS
and Mud themes. Legacy public theme accessors remain for plugin compatibility.
Native icons reuse Quasar's existing Material SVG paths; their Apache license is
[distributed with the UI](../../Quasar/wwwroot/licenses/material-icons.txt).
Table hover and selection change icon colors through `currentColor`, preserving
each SVG's fill and stroke. Do not force paint onto descendant paths: Material
icons can include transparent canvas paths, and plugin dialogs nested in a row
remain descendants even when displayed in the browser's top layer.

Core Mud assets/providers, dialog/snackbar services, color parsing, and some
helper enum types remain during migration. This is not complete dependency removal.

Focused verification passes 36 core component/service tests, including exact-default
palette migration, custom-color preservation, live fleet totals, view switching,
search/sort/technical columns, console history/download/error selection and
keyboard-scroll reattachment, confirmation-time permission checks, notification
preferences, and cluster content/console/PluginSdk configuration workflows.
The build passes with the matching Magnetar SDK; existing warnings remain.
Ten JavaScript tests pass, including chart palette changes/gaps/disposal, theme
observation, and push registration. Offline browser checks cover fleet/appearance
layout at desktop and 390-pixel widths, light/dark palette propagation, and native
menu dismissal/focus. Screenshot capture became unavailable during final browser
checks. Quasar has not been launched. Real circuits, multi-session behavior,
server/Agent commands, asset streaming, and installed plugin acceptance still
need an explicitly requested smoketest.

## Remaining workflow inventory

| Area | Work remaining |
| --- | --- |
| Shared infrastructure | Native host dialogs/confirmations and toast notifications; migrate consumers in services as well as components. |
| Server management | Replace remaining behavioral Mud controls and dialog host in creation/edit/clone/deletion/import, world/template/profile dialogs, filesystem picker, and the native console. |
| Configuration | Profiles, world templates, Workshop credentials, pending changes, PluginSdk schema editor, and plugin logs. |
| Clusters and hosts | Guided setup/conversion/enrollment, cluster route/console/remove dialogs, deployment/content/fleet panels, and recovery workflows. |
| Operational pages | Replace remaining behavioral controls/services in players, chat, entities, analytics, Discord, security, backup, plugin catalogs, updates, and route fallbacks; shared visual layout is migrated. |
| Dependency removal | Remove Mud providers, services, packages, imports, CSS/JS, palette adapters, helper enums, parser, and legacy test selectors when their final consumers migrate. |
| Release integration | Native viewer pin is merged in QuasarHub; update installed viewer packages through the catalog and coordinate other external plugins. |
| Acceptance | Keyboard/focus/contrast and responsive checks across remaining workflows, then an explicitly requested real Quasar/server/Agent/plugin smoketest. |

The viewer and template changes have separate pull requests:
[viewer #2](https://github.com/CometWorks/viewer/pull/2) and
[UI plugin template #2](https://github.com/CometWorks/quasar-plugin-template/pull/2).
Both are merged. [QuasarHub #1](https://github.com/CometWorks/quasar-hub/pull/1)
is also merged, pinning viewer `868271714a4e4d978ed619d035144efc9011fad4`.
Updating Quasar alone does not update installed viewer plugins: update Entity
Viewer through the UI plugin catalog, then restart Quasar. No separate viewer
binary release is required.

## Visual samples

Open [the interactive design study](quasar-ui-concepts.html) in a browser. It
contains a fleet dashboard, server console, and appearance editor. The proposed
style uses compact tables, restrained typography, thin borders, neutral surfaces,
and cyan accents drawn from [the server directory](https://se-servers.net/) and
[the diagnostics site](https://log.se-servers.net/). The latter's authenticated
screens were unavailable; its public sign-in shell supplies the visual reference.

The samples use HTML, CSS, and browser APIs without a component framework. They
illustrate one coherent design in several operational contexts and color modes.
They are not production Razor components and do not connect to Quasar. Mock
summary metrics and charts illustrate potential dashboard presentation; their
aggregation and event placement would need matching to existing runtime data.
The study adds no runtime dependency or production service configuration.

Screenshots: [fleet dashboard](quasar-fleet-dark.png),
[server console](quasar-console-dark.png), and
[appearance editor](quasar-appearance-light.png).

Working sample interactions:

- Dashboard search, status and host filters.
- Navigation between the three samples; other actions show a prototype notice.
- Console message and severity filtering, plus simulated command submission.
- Light, dark, and system appearance, including OS preference changes.
- Separate light and dark palette editing, including CSS colors with alpha.
- All 29 persisted palette fields and the four current Quasar presets, captured
  from `QuasarTheme` and `BrandingPresets` when the sample was created.
- The original CometWorks palette, now the production Quasar default.
- App name, subtitle, logo previews for each mode, and a tab favicon preview.
- Sample settings saved only under `quasar.design-study.v1` in browser storage.
  Asset previews last for the current tab and are not persisted.

Serve the design directory if local-file browser restrictions interfere:

```bash
python3 -m http.server 8767 --bind 127.0.0.1 --directory Docs/Design
```

Then open `http://localhost:8767/quasar-ui-concepts.html`. This command serves
static samples; it does not launch Quasar.

## Baseline scope

Source inventory on 11 October 2026:

| Surface | Size |
| --- | ---: |
| Razor components | 74 |
| Components declaring routes | 22 |
| Dialog components | 24 |
| Razor files with Mud components or services | 68 |
| Core source files with Mud component, namespace, or CSS references | 81 |
| Mud component opening tags | 2,955 |
| Distinct Mud component tags | 66 |
| Razor source lines, including component logic | 25,837 |

Counts show migration breadth, not how much code must be rewritten. Existing
component logic, services, DTOs, validation rules, and authorization checks should
be retained where they fit. Some visible interfaces are supplied by external UI
plugins and are not included in these counts.

The largest and most sensitive surfaces include `Configs.razor`,
`ServerEditorDialog.razor`, `ServerConsoleDialog.razor`, `Analytics.razor`, and the
schema-driven `PluginConfigEditor.razor`. Dialog and snackbar services also appear
in `ServerManagementActions`, so removal extends beyond markup replacement.

## Recommended implementation

Keep the current Razor routing, interactive server model, service calls,
authorization, and Agent protocol. Build a small Quasar component set around
semantic HTML and the established application behavior: buttons, fields, tables,
tabs, notifications, and dialogs. Use Blazor input validation, `InputFile`, CSS,
and native browser functionality where they fit. Shared controls should own focus
behavior, error presentation, and consistent styling.

The current analytics renderer already uses uPlot; retain its request flow,
range selection, synchronized charts, refresh behavior, and theme notifications.
There is no need to replace it to remove MudBlazor. Likewise, keep the entity
viewer and its companion channel rather than rebuilding its rendering system.

Migrate in coherent slices: appearance and layout first, a complete server
workflow next, then configuration and the remaining operational pages. Existing
Mud components can coexist temporarily while pages move, but the final core
package, imports, provider services, CSS, JavaScript, icons, and test helpers must
be removed or replaced before claiming complete removal.

## Appearance compatibility

Retain `branding.json` field names and existing preset IDs. Its palette values
are plain strings, so a new renderer can map them to Quasar CSS variables without
requiring users to reset their branding. Move the default palette definitions
out of Mud types and replace the palette conversion methods. Preserve the
existing normalization and fallback behavior.

| Existing behavior | Required migration behavior |
| --- | --- |
| `quasar.theme.mode` browser preference | Keep the key and `system`, `light`, `dark` values. |
| OS theme observation | Replace the Mud provider's observer with `matchMedia`, including listener cleanup. |
| Light and dark palettes | Preserve every field, contrast color, and alpha-bearing hover value. |
| Four built-in presets | Preserve IDs and colors; the new visual layout must work with each. |
| Manual color edit | Continue clearing the preset ID to mark a custom palette. |
| Branding saves and external file edits | Keep `BrandingService.Changed` and updates across open sessions. |
| Name, subtitle, logos, favicon | Retain asset paths, mode selection, upload errors and limits, immediate asset persistence, and cache busting. |
| Appearance permissions | Keep `CanManageAppearance` for page access and editing. |
| Default reset | Preserve the existing reset operation and Quasar default values. |

The sample's local live preview is a design exploration. It does not demonstrate
production persistence, external file watching, or propagation to other sessions.

## Plugin compatibility

Magnetar server plugins declare configuration as metadata through PluginSdk.
That protocol can remain stable. Quasar's configuration renderer must still
support tabs, sections, columns, types, ranges, collections, dictionaries,
nested values, errors, defaults, dirty drafts, and apply acknowledgements.

Quasar **UI plugins** are a different boundary. The current
[UI plugin documentation](../QuasarPluginSystem.md#ui-migration-and-mudblazor-compatibility)
now recommends native controls and Quasar palette tokens. Legacy plugins may
still depend on Mud components and CSS variables. Some host components can also be replaced by plugin contributions.
The public abstractions do not directly reference the MudBlazor package, but
that does not make existing plugin components compatible automatically.

Preserving existing plugin binaries and completely removing their Mud dependency
are incompatible when those binaries use MudBlazor. A transition can temporarily
retain the providers and assets, while a fully Mud-free release requires migrated
plugin versions and explicit compatibility handling. Inventory installed plugins
and coordinate their releases before the final dependency removal. Update the
plugin guidance and templates alongside the new controls and theme tokens.

## Effort breakdown

Assumes one developer familiar with Quasar, an agreed visual direction, and
retention of the current backend and framework.

| Work | Engineering days |
| --- | ---: |
| Shared controls, layout, appearance compatibility | 5–7 |
| Dashboard and operational pages | 8–12 |
| Configuration editors, dialogs, and PluginSdk renderer | 8–12 |
| Core UI plugin integration, guidance, final dependency removal | 3–5 |
| Regression checks, accessibility, responsive layouts, polish | 6–9 |
| Total | 30–45 |

That is roughly 6–9 full-time working weeks, with calendar time depending on
plugin coordination and review. A first usable migrated layout, dashboard, and
appearance slice is approximately 5–8 days within this total. Restyling the
current Mud-based UI could produce a similar visual direction sooner, but would
not satisfy complete removal.

## Completion criteria

Maintain a page and workflow inventory that includes server creation/import and
lifecycle confirmations; cluster setup, conversions, deployment and recovery;
configuration profiles, worlds and Workshop flows; both plugin systems; players,
chat, entities, analytics, Discord; hosts, security, backups, notifications and
updates; reconnect and Quasar power behavior.

Preserve role and per-server/cluster permissions, disabled and busy states,
validation, unsaved drafts, confirmation consequences, errors, asynchronous
refresh, connection loss, and navigation state. Verify dialog focus trapping,
Escape behavior, return focus, keyboard operation, labels, contrast, and narrow
layouts. Use existing tests where they express behavior; replace Mud-specific
rendering setup and selectors without weakening assertions. Add focused coverage
for branding compatibility and interactive workflows that currently lack it.

Build and component tests establish code-level compatibility. A later explicitly
authorized Quasar smoketest is needed to verify real circuits, multiple sessions,
server/Agent interactions, and migrated UI plugin packages.

## Sample validation

The design study was checked in the collaborative browser at desktop and
390-pixel mobile widths. Checks covered search, status/host filters, empty states,
navigation, log severity filtering, simulated commands, all four
existing presets, all 29 palette fields, alpha color editing, branding text,
browser save/reload, light/dark selection, system preference changes, and mobile
navigation. The mobile table scrolls within its container without overflowing
the page. Quasar was not launched; these checks validate the prototype only.
