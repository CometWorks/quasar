# Quasar UI rewrite assessment

Removing MudBlazor is feasible without replacing Blazor Server or the supervisor
backend. Budget approximately **30–45 engineering days** for one developer to
migrate the core UI, preserve behavior, and complete regression checks. This is a
planning estimate based on the current source, not a measured delivery schedule.
External UI plugin migrations are additional work, depending on their scope.

## Migration progress

This branch targets Quasar **1.3.0**, with the shared version declared in
`Directory.Build.props`. The release workflow uses that version for upcoming PR
builds, allowing the core UI changes to be tested through the regular prerelease
update flow.

The approved rewrite has started. Migrated surfaces:

- Native responsive shell, navigation, theme selector, notification/auth/power
  entry points, and shutdown overlay, with the current Quasar logo.
- Native appearance form with all 29 fields in both palettes, presets, custom
  branding, separate mode-specific logo uploads, favicon, draft preview, save,
  reset, busy states, and invalid-color validation. RGB selection preserves alpha.
- Native dashboard heading, summary metrics, card/list selector and layout.
  New browsers default to the dense list; saved card/list choices and query
  overrides still work. Metrics use existing standalone runtime data.
- Native standalone/cluster server table with all nine sortable data columns,
  live detail expansion, copy/config links, restart/error summaries, and existing
  actions/disabled states. Endpoint sorting uses comparable host/port pairs for
  mixed standalone and cluster rows. Unmanaged agent expansion remains available.

Additional migrated surfaces:

- Dashboard consent/setup wizard, errors, managed-runtime preparation, progress,
  retries, and copyable paths. Existing setup and lifecycle handlers are retained.
- Server and cluster cards, live detail metrics, chat, player role actions,
  kick/ban confirmations, save guards, and command results. Native player
  popovers preserve their keyed elements across snapshots and use browser
  Escape/light-dismiss behavior. Confirmed player actions recheck permissions
  after the confirmation closes.
- Cluster overview/configuration links and administration controls, including
  settings, staged profiles, diagnostics, exports, handover, and capacity edits.
  The embedded PluginSdk configuration renderer still uses legacy controls.
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

The charcoal/cyan **CometWorks** preset is optional. Choose it in Appearance and
save to match the approved samples. Existing saved branding and the four original
presets are preserved. The editor now labels light/dark uploads consistently
with the shell's existing `LogoLightPath`/`LogoDarkPath` behavior.

`QuasarTheme` is the temporary bridge from stored palette strings to native CSS
and Mud themes. Legacy public theme accessors remain for plugin compatibility.
Native icons reuse Quasar's existing Material SVG paths; their Apache license is
[distributed with the UI](../../Quasar/wwwroot/licenses/material-icons.txt).

Core Mud assets/providers, dialog/snackbar services, color parsing, and some
helper enum types remain during migration. This is not complete dependency removal.

Focused verification passes 32 core component/service tests covering appearance,
fleet sorting, native chat/player commands, live snapshots, save guards,
confirmation-time permission revocation, listing saves, notification thresholds,
and cluster console/content workflows. The core worker/Agent build passes.
The native theme observer test passes. Both plugin adapters and preview hosts
build without MudBlazor; viewer checks pass 3 component tests and 24 browser
runtime tests. Offline rendered server details and viewer dialogs were inspected
at desktop and 390-pixel widths, including native popover/modal Escape dismissal,
focus return, theme propagation, and contained table scrolling. Quasar has not
been launched. Real circuits, multi-session behavior, server/Agent commands,
asset streaming, and installed plugin acceptance still need a requested smoketest.

## Remaining workflow inventory

| Area | Work remaining |
| --- | --- |
| Shared infrastructure | Native host dialogs/confirmations and toast notifications; migrate consumers in services as well as components. |
| Server management | Creation, edit, clone, deletion, import, world/template/profile dialogs, filesystem picker, and server console. |
| Configuration | Profiles, world templates, Workshop credentials, pending changes, PluginSdk schema editor, and plugin logs. |
| Clusters and hosts | Guided setup/conversion/enrollment, cluster route/console/remove dialogs, deployment/content/fleet panels, and recovery workflows. |
| Operational pages | Players, chat, entities, analytics, Discord, security, backup, both plugin catalogs, updates, and route fallbacks. |
| Dependency removal | Remove Mud providers, services, packages, imports, CSS/JS, palette adapters, helper enums, parser, and legacy test selectors when their final consumers migrate. |
| Release integration | Publish/pin the native viewer revision in QuasarHub and coordinate remaining installed UI plugin migrations. |
| Acceptance | Keyboard/focus/contrast and responsive checks across remaining workflows, then an explicitly requested real Quasar/server/Agent/plugin smoketest. |

The viewer and template changes have separate pull requests:
[viewer #2](https://github.com/CometWorks/viewer/pull/2) and
[UI plugin template #2](https://github.com/CometWorks/quasar-plugin-template/pull/2).
The new viewer revision has not been published or pinned into QuasarHub. Updating
Quasar alone does not update installed viewer plugins; those continue to use
their currently pinned revisions until the plugin release is updated.

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

- Dashboard search, status and host filters, and row density.
- Navigation between the three samples; other actions show a prototype notice.
- Console message and severity filtering, plus simulated command submission.
- Light, dark, and system appearance, including OS preference changes.
- Separate light and dark palette editing, including CSS colors with alpha.
- All 29 persisted palette fields and the four current Quasar presets, captured
  from `QuasarTheme` and `BrandingPresets` when the sample was created.
- The CometWorks preset, now also selectable in the current production slices.
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
row density, navigation, log severity filtering, simulated commands, all four
existing presets, all 29 palette fields, alpha color editing, branding text,
browser save/reload, light/dark selection, system preference changes, and mobile
navigation. The mobile table scrolls within its container without overflowing
the page. Quasar was not launched; these checks validate the prototype only.
