# MudBlazor removal plan

This plan finishes the native UI migration described in
[the UI rewrite assessment](QuasarUiRewrite.md) and removes the MudBlazor
package from Quasar. It starts from `main` at `601777cc` (25 November 2026).
That commit includes the native dialog and toast host
([#450](https://github.com/CometWorks/quasar/pull/450)). Open PR
[#454](https://github.com/CometWorks/quasar/pull/454) adds the native message box
and `QuasarNotice`.

## Where things stand

| Measure | Baseline (11 Oct) | Now |
| --- | ---: | ---: |
| Mud opening tags in production Razor/C# | 2,955 | 1,731 |
| Distinct Mud tags | 66 | 47 |
| Razor files with Mud tags | 68 | 68 |

Most of the removed tags were in the shell, fleet overview, appearance page and
server/cluster cards, which were rebuilt as native components. Every other
workflow still contains some Mud markup, so the number of affected files has
not changed.

Native pieces already available:

| Piece | Location | Replaces |
| --- | --- | --- |
| Dialog host, `IQuasarDialogService`, `IQuasarDialogInstance` | `Quasar.Client/Ui/Dialogs`, `Quasar.Ui.Abstractions/Dialogs` | `MudDialogProvider`, `IDialogService`, `MudDialog` |
| Toast host, `IQuasarToastService`, `QuasarSeverity` | `Quasar.Client/Ui/Toasts`, `Quasar.Ui.Abstractions/Toasts` | `MudSnackbarProvider`, `ISnackbar` |
| Message box (#454) | `QuasarMessageBoxDialog` | `IDialogService.ShowMessageBoxAsync` |
| `QuasarNotice` (#454) | `Quasar.Client/UiPlugins` | `MudAlert` |
| `QuasarIcon`, `QuasarIconButton`, `QuasarIcons` | `Quasar.Client/UiPlugins` | `MudIcon`, `MudIconButton`, `Icons.Material.*` (partial) |
| `QuasarProgress`, `QuasarOperationLoader` | `Quasar.Client/UiPlugins` | `MudProgressLinear`, `MudProgressCircular` |
| `QuasarColorField` | `Quasar.Client/Administration` | `MudColorPicker` (still parses with `MudColor`) |
| CSS: `.quasar-button*`, `.quasar-field*`, `.quasar-fieldset`, `.quasar-form-*`, `.quasar-table-*`, `.quasar-tone-*`, `.quasar-details` | `Quasar/wwwroot/quasar-ui.css` | Styling only. No Razor components wrap these classes yet. |
| Native menus (popover API), `<details>` disclosures | 3 and 12 files respectively | Ad-hoc. Not yet extracted as shared components. |

## Inventory

### Components

Counts are opening tags in production code (`Quasar.Tests` and `tests/` excluded).

| Mud component | Uses | Native target | Status |
| --- | ---: | --- | --- |
| `MudButton` | 260 | `QuasarButton` | **New**, CSS exists |
| `MudIconButton` | 47 | `QuasarIconButton` | Exists |
| `MudAlert` | 158 | `QuasarNotice` | #454 |
| `MudTable`, `MudTh`, `MudTd`, `MudTableSortLabel`, `MudTablePager` | 29, 150, 150, 37, 5 | `QuasarTable<T>` | **New**, largest control |
| `MudGrid`, `MudItem` | 36, 141 | CSS grid utilities (`.quasar-grid`, responsive span classes) | **New**, CSS only |
| `MudTextField` | 106 | `QuasarTextField` | **New** |
| `MudNumericField` | 60 | `QuasarNumberField<T>` | **New** |
| `MudSelect`, `MudSelectItem` | 59, 97 | `QuasarSelect<T>` on native `<select>` | **New** |
| `MudCheckBox`, `MudSwitch` | 59, 6 | `QuasarCheckbox`, `QuasarSwitch` (`role="switch"`) | **New** |
| `MudRadioGroup`, `MudRadio` | 1, 2 | `QuasarRadioGroup` (fieldset) | **New** |
| `MudChip` | 76 | `QuasarChip` using `.quasar-tone-*` | **New** |
| `MudTooltip` | 47 | `QuasarTooltip` rendered inside its own subtree | **New** |
| `MudMenu`, `MudMenuItem` | 6, 13 | `QuasarMenu`, extracted from the existing native menus | **Extract** |
| `MudExpansionPanels`, `MudExpansionPanel` | 21, 37 | `QuasarDisclosure` on `<details>` | **Extract** |
| `MudTabs`, `MudTabPanel` | 6, 13 | `QuasarTabs` (ARIA tablist) | **New** |
| `MudList`, `MudListItem` | 4, 11 | `QuasarListbox` for pickers | **New** |
| `MudIcon` | 22 | `QuasarIcon` | Exists |
| `MudProgressLinear`, `MudProgressCircular` | 16, 4 | `QuasarProgress` | Exists |
| `MudLink` | 7 | `<a>` / `NavLink` | Inline |
| `MudSpacer` | 2 | CSS | Inline |
| `MudForm` | 2 | `EditForm` / explicit validation | Inline |
| `MudDialog` | 23 | Native dialog host | Exists (#450) |
| `MudDatePicker`, `MudTimePicker` | 2, 5 | `<input type="date">`, `<input type="time">` | Inline (Analytics, Backups) |
| `MudAutocomplete` | 1 | Input with `<datalist>` or a combobox | One-off (ChatView) |
| `MudStepper`, `MudStep` | 1, 2 | Bespoke `<ol>` stepper | One-off (SetupCluster) |
| `MudDropContainer`, `MudDropZone` | 1, 1 | HTML drag and drop with keyboard move buttons | One-off (ConfigEditor) |
| `MudColorPicker` | 1 | `QuasarColorField` | One-off (SchemaConfigEditor) |
| `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider` | 1 each | Removed | Removed last (`QuasarThemeProvider.razor`) |

Feature usage the new controls must cover:

- **Table:** `Items`, `Hover` and `Dense` on all 27 typed tables. `Breakpoint`
  (stacked layout on narrow screens) on 13. 33 sort columns, 5 pagers, 4 row
  clicks, 2 fixed headers, and one `ServerData` table (LivePluginLogs).
- **Fields:** `Immediate` (52), multiline `Lines` (41), adornments (38),
  `Required`/`Validation` (18), 58 numeric `Min`/`Max`/step constraints,
  one password input, two clearable fields, and 303 responsive grid breakpoint
  attributes (`xs`/`sm`/`md`/`lg`).
- **Select:** one multi-select (DiscordView server picker).

### Services and dialog API

| API | Files | Calls | Target |
| --- | ---: | ---: | --- |
| `ISnackbar` / `Snackbar.Add` | 24 | ~218 | `IQuasarToastService` |
| `IDialogService.ShowMessageBoxAsync` | — | 29 | `IQuasarDialogService.ShowMessageBoxAsync` (#454) |
| `IDialogService.ShowAsync<T>` | — | 20 | `IQuasarDialogService.ShowAsync<T>` |
| `IMudDialogInstance` cascades | 23 | — | `IQuasarDialogInstance` |
| `DialogParameters`, `DialogOptions`, `DialogResult` | 11, 23, 26 | 18, 47, 52 | `QuasarDialogParameters<T>`, `QuasarDialogOptions`, `QuasarDialogResult` |

### Enum and type leakage

Mud enums are used as component parameters and in C# logic, for example when a
method returns `Severity` or `Color` for a state. These usages have to be
rewritten along with the markup.

| Type | References | Files |
| --- | ---: | ---: |
| `Variant.*` | 554 | 50 |
| `Severity.*` | 427 | 65 |
| `Color.*` | 384 | 57 |
| `Icons.Material.*` | 220 | 41 |
| `Size.*` | 101 | 23 |
| `MaxWidth.*` | 24 | 16 |
| `Origin.*` | 19 | 9 |
| `Adornment.*` | 12 | 7 |

The code uses 81 distinct Material icons (80 Filled, 1 Outlined). **48 of them
have no `QuasarIcons` path yet.**

### Theme bridge

| Item | Location | Action |
| --- | --- | --- |
| `MudTheme`, `PaletteLight`, `PaletteDark` construction | `Quasar.Client/Administration/QuasarTheme.cs` (`Create`, `Default`, `ToMudPalette*`) | Delete. `BuildCss` stays. |
| `ThemePalette.ToMudPaletteLight/Dark()` | `BrandingSettings.cs:331-332` | Public plugin accessors. Mark `[Obsolete]`, then delete. |
| `BrandingService.BuildMudTheme()` | `Quasar/Services/BrandingService.cs:66` | **Unused.** Delete now. |
| `MudColor` parsing and formatting | `QuasarColorField.razor`, `NativeUiThemeTests` | Replace with a Quasar color parser (hex 3/4/6/8 digits, `rgb()`/`rgba()`, alpha preserved). |
| Mud providers | `QuasarThemeProvider.razor` | Remove once no Mud component remains. |
| Shell mapping to `--mud-palette-*` | `quasar-ui.css` | Remove with the providers. |

### Packaging and assets

| Item | Location |
| --- | --- |
| `PackageReference MudBlazor 9.4.0` | `Quasar/Quasar.csproj:30`, `Quasar.Client/Quasar.Client.csproj:10` |
| `MudBlazor.min.css` / `.js` | `Quasar/Components/App.razor:21,47` |
| Release layout required files | `Magnetar.Protocol/Runtime/QuasarWebReleaseLayout.cs:25-26` |
| `AddMudServices()` | `Quasar.WebAssembly/Program.cs:9` |
| `@using MudBlazor` | 45 files, including 6 `_Imports.razor` |
| `.mud-*` / `--mud-*` selectors | `app.css` (335), `quasar-ui.css` (69), scoped CSS in FleetServerEditor (11), ConfigEditor (9), SchemaConfigEditor (2) and DashboardSetupDialog (2), `ui-plugins.css` (1) |

### Mud CSS utility classes

Razor markup also uses MudBlazor's global utility classes. Quasar's stylesheets
don't define them, so **they stop working without any build error** once the
Mud stylesheet is removed. There are 255 uses in 44 files: `pa-4` (63),
`mb-4` (37), `mt-2` (28), `mb-3` (18), `mt-3` (16), `mb-2` (12), `pa-3` (10),
plus `py-2`, `d-block`, `ma-4`, `d-flex`, `align-center`, `flex-grow-1` and
others. There are also 70 inline `style` attributes in 32 files.

### Tests

28 test files register Mud services or use Mud types and selectors. The
heaviest are `ClusterPlayerTests` (66 matching lines), `ClusterCreateTests`
(52), `ControlPlaneViewerTests` (29), `FirstSetupTests` (26) and
`NativeUiThemeTests` (25). `tests/Quasar.Wasm.DashboardChecks/browser.test.mjs`
contains three `.mud-*` selectors. Rewrite these tests file by file as their
components migrate, keeping every assertion as strong as before.

### Files by remaining Mud tags

| Tags | Lines | File |
| ---: | ---: | --- |
| 207 | 3,261 | `Configs/ConfigEditor.razor` |
| 117 | 841 | `Administration/Backups.razor` |
| 113 | 955 | `Components/FleetServerEditor.razor` (dialog) |
| 96 | 1,173 | `UiPlugins/UiPluginsView.razor` |
| 94 | 732 | `Components/DiscordView.razor` |
| 72 | 1,035 | `Administration/UpdatesPage.razor` |
| 64 | 645 | `Components/ImportExistingServerDialog.razor` |
| 64 | 1,236 | `Components/AnalyticsView.razor` |
| 62 | 618 | `Components/PlayersView.razor` |
| 60 | 871 | `Components/EntitiesView.razor` |
| 56 | 410 | `Administration/SecurityPage.razor` |
| 52 | 1,325 | `Components/SchemaConfigEditor.razor` (PluginSdk renderer) |
| 43 | 140 | `Components/HostMachines.razor` |
| 42 | 342 | `Components/SetupCluster.razor` |
| 41 | 345 | `Clusters/ClusterDeploymentPanel.razor` |
| 40 | 181 | `Components/LivePluginLogs.razor` |
| 39 | 191 | `Components/PlayerAccessView.razor` |
| 38 | 169 | `Components/WorldLibrary.razor` |
| 35 | 309 | `Components/WorldImportForm.razor` |
| 29 | 178 | `Clusters/ClusterFleetPanel.razor` |
| ≤26 | | 48 further files |

### External UI plugins

Pinned commits for all five QuasarHub catalog entries (Advanced PB Limiter,
Assistant, Entity Viewer, King of the Hill, Sens Essentials) have no MudBlazor
package reference and no Mud markup in their plugin projects. Plugins installed
from outside the catalog may still depend on MudBlazor. Removing the package
would break them when they load.

The UI plugin set is small enough to clean ahead of the cutover. Quasar will
not ship a deprecation release, a MudBlazor load check or compatibility shims.
Catalog plugins are verified as Mud-free immediately before the cutover, and
the release that removes MudBlazor drops it in one step (phase 6).

## Consolidation

Replacing every Mud control one for one would keep the current duplication.
Each page builds its own heading, error banner, loading indicator, section
panels, status colors, server picker and busy handling. The migration is the
right time to give pages one shared vocabulary, because every one of these
sites is being edited anyway.

### Duplication found

| Pattern | Evidence | Shared replacement |
| --- | --- | --- |
| Page skeleton | `DashboardPageHeading` appears in 23 views. The surrounding markup differs between them: actions placed before the heading, after a `MudSpacer`, or wrapped in rows. Error banners appear before or after the heading (34 error alerts in 24 files), and loading uses a spinner (4), a linear bar (14), `QuasarProgress` (4) or nothing. `PageTitle` sits in the route in some places and in the view in 8 others. | `QuasarPage` |
| Section panel | 86 `<section class="quasar-panel pa-4">` blocks, mostly containing `h2.quasar-text.quasar-type-h6` and a description `p` inside a `quasar-stack` or `quasar-layout`. | `QuasarSection` |
| Status color | 15 `GetXColor`/`GetXSeverity` methods returning Mud enums, 39 inline `=> Color.*`/`Severity.*` switch arms in 7 files, and 33 `quasar-tone-@(Color.ToString())` conversions. `FleetWorkspace.StateTone` already maps server states to native tones on its own. | `QuasarTone`, `StatusTones`, `QuasarStatusBadge` |
| Server/cluster picker | Entities, Players, Player access, Chat, Analytics, Plugin stats, Discord, Logs, Backups, World library and Plugin settings each render their own select over their own target type (`EntityTarget`, `ChatTarget`, `DiscordTarget`, `AccessTarget`, `BackupServer`, `ConversionDestination` and others). | `ServerTargetSelect` |
| File browser dialog | `FolderPickerDialog` and `PluginManifestPickerDialog` share about half of their roughly 170 lines: path field, Go button, search, entry list and hidden-file toggle. `BackupFolderPicker` is a third, simpler version. | `QuasarFileBrowserDialog` (folder or file mode) |
| Log and console output | Server, cluster and Discord consoles and LivePluginLogs each implement output rendering, line styling and follow-tail separately. Two sets of CSS classes exist (`quasar-console-*`, `cluster-console-*`). | `QuasarLogView` |
| Player actions | `FleetLiveDetails` has a native player action popover with confirmations and a permission recheck. `PlayersView` repeats the same actions in a `MudMenu`. | `PlayerActionsMenu` |
| Destructive confirmation | `ClusterRemoveDialog` and the forget path ask the user to type the ID. `ServerDeleteDialog`, `RemoveHostEnrollment` and 29 message boxes confirm deletions with differing wording and button tones. | Message box (#454) plus `QuasarConfirmDialog` with a `RequireText` option |
| Copy to clipboard | `CopyablePath` exists, but 7 files call `quasarConfigs.copyText` directly, each with its own toast text. | `QuasarCopyButton`, `CopyablePath` |
| Busy and error handling | 8 private `RunAsync`/`RunBusyAsync` variants and 29 hand-written `try`/`finally` busy toggles that show a toast on failure. 133 `Disabled="@_busy"` bindings. | `QuasarButton Busy`, `ActionRunner` |
| Polling | 30 files run their own `PeriodicTimer` refresh loop with cancellation and `InvokeAsync`. | `PollingRefresh` helper |
| Layout CSS | `.quasar-layout` and `.quasar-stack` are identical. `.quasar-layout-row` and `.quasar-row` overlap. `.quasar-type-body1` and `body2` render the same. Mud utility classes and inline styles fill the gaps. | One set of layout primitives |

### Component tiers

Pages are built only from the first two tiers plus their own feature
components. Tiers 1 and 2 form a **public component library** that Quasar and
UI plugins share, so a plugin page looks and behaves like a core page.

1. **Controls** (`Quasar.Ui.Abstractions/Controls`): button, fields, select,
   checkbox, switch, chip, tooltip, menu, tabs, disclosure, listbox, table, plus
   the existing icon, icon button, progress and operation loader. These are
   generic and know nothing about Quasar's domain.
2. **Patterns** (`Quasar.Ui.Abstractions/Patterns`): `QuasarPage`,
   `QuasarSection`, `QuasarToolbar` (search, filters, refresh, actions),
   `QuasarStatusBadge`, `QuasarEmptyState`, `QuasarCopyButton`,
   `QuasarConfirmDialog`, `QuasarFileBrowserDialog`, `QuasarLogView`. These are
   generic layouts and flows built from controls. They get their data through
   parameters and callbacks, never from Quasar services. For example,
   `QuasarFileBrowserDialog` receives a browse callback.
3. **Feature components** (internal, in `Quasar.Client/Features/<Area>`):
   `ServerTargetSelect`, `PlayerActionsMenu`, `PlayerIdentity`, `StatusTones`,
   and each page's own panels.

Sketch of the page scaffold:

```razor
<QuasarPage Section="Operations" Title="Entities" Error="@_error" Loading="@(_targets is null)">
    <Description>Live world entities for a server or whole cluster.</Description>
    <Actions><QuasarButton Tone="QuasarTone.Primary" OnClick="RefreshAsync">Refresh</QuasarButton></Actions>
    <ChildContent>
        <QuasarToolbar>
            <ServerTargetSelect @bind-Value="_targetId" Targets="_targets" />
            <QuasarTextField Label="Search" @bind-Value="_search" Immediate />
        </QuasarToolbar>
        <QuasarSection Title="Results" Description="…">…</QuasarSection>
    </ChildContent>
</QuasarPage>
```

`QuasarPage` owns `PageTitle`, the heading, the actions slot, error and
loading placement, and the extension outlet if one is needed. `QuasarSection`
owns padding, heading level and spacing. Pages then need no utility classes or
inline styles for layout.

Rules for the patterns:

- **One status vocabulary.** A public `QuasarTone` enum (Neutral, Primary,
  Info, Success, Warning, Error) is used by buttons, chips, badges, notices and
  toasts (`QuasarSeverity` maps onto it). Domain-to-tone mappings live in one
  internal `StatusTones` class (server state, cluster phase, plugin hub status,
  log level, runtime component state) and are not repeated in each page.
- **One target model for pickers.** `ServerTargetSelect` takes a small
  `DashboardTarget` (ID, name, kind server/cluster/node, state). Each view maps
  its own DTO to it. Merging the endpoint DTOs is optional follow-up work.
- **Behavior helpers are opt-in.** `ActionRunner` (busy flag, cancellation,
  success and failure toasts, `ObjectDisposedException` on navigation) and
  `PollingRefresh` change behavior. Adopt them only in slices that have tests
  covering the busy, error and refresh paths (decision 5).

### Folder and namespace layout

Shared controls currently live in three places: `UiPlugins/` (icons, icon
button, progress, operation loader, `CopyablePath`, `StableRows`, under the
`Quasar.Components.Shared` namespace), `Components/Shared/` (the dialog and
toast hosts) and `Ui/` (services). Feature code is split by component kind
rather than by feature. `Components/` mixes views, dialogs and fleet parts from
every area, and `Pages/` holds both routes and dialogs.

Target layout:

```text
Quasar.Ui.Abstractions/   # public UI surface for core and plugins (Razor class library)
  Dialogs/  Toasts/       # existing contracts, unchanged
  Controls/  Patterns/  Icons/
Quasar.Client/
  Ui/                     # host-only: dialog and toast host implementations, services
  Features/
    Fleet/  Servers/  Clusters/  Hosts/  Worlds/  Configuration/
    Players/  Chat/  Entities/  Analytics/  Discord/  Plugins/  UiPlugins/
    Access/  Backups/  Updates/  Appearance/  Notifications/  Setup/
  Layout/                 # shell, drawer, power
```

Each feature folder holds its route component, views, dialogs and its
`*Dashboard.cs` client. Routes, extension `TargetKey` strings and the existing
dialog and toast contracts stay unchanged. Browser plugins reference only
`Quasar.Ui.Abstractions`, never `Quasar.Client`, so the feature folders can move
freely.

The controls and patterns ship inside `Quasar.Ui.Abstractions`. Plugins already
receive this assembly, so no new DLL, staging step or build property is needed:

- The worker already stages `Quasar.Ui.Abstractions.dll` and its XML docs
  (`Quasar.csproj:138-160`), and `QuasarUiPluginHubCatalogService` already
  passes it to plugin builds. Plugins reference it with `Private="false"`, so the
  host loads one copy and plugin and core components share type identity.
- The project switches from `Microsoft.NET.Sdk` to `Microsoft.NET.Sdk.Razor` and
  adds `Microsoft.AspNetCore.Components.Web` (for forms and events). It still
  references neither `Quasar.Client` nor any Quasar service. Its package
  description ("Public dialog and toast contracts…") and the plugin docs need
  updating to mention components.
- The component styles stay in the host's `quasar-ui.css`, which the shell
  already loads for every page and plugin. The library ships no `wwwroot`, so
  plugins have no static-asset paths to resolve and never load styles
  themselves.
- Components use the `Quasar.Ui.Components` namespace, separate from the
  `Quasar.Ui` dialog and toast contracts, so a plugin opts in with one
  `@using`.

Move files with pure `git mv` PRs per feature, containing no logic or markup
changes, at the start of each slice. This keeps rename diffs separate from the
review-heavy migration diffs.

## Constraint that determines the order

A native modal uses `showModal()`, which makes everything outside the dialog
inert. Mud selects, menus, tooltips, autocompletes and pickers render their
popovers at shell level, so they cannot appear above a native modal
([plugin docs](../QuasarPluginSystem.md#native-dialogs-and-toasts)). **A dialog can
move to the native host only once its contents contain no Mud popover controls.**
The native replacements therefore have to exist before the larger dialogs move.
They must also render their popups inside their own subtree or the top layer,
never in a shell-level provider.

Mud dialogs by size and popover controls:

| Dialog | Mud tags | Popover controls |
| --- | ---: | --- |
| PluginCatalogDescriptionDialog | 2 | none |
| ConfigProfilePendingChangesDialog | 5 | none |
| WorldTemplateFromServerDialog | 5 | none |
| ClusterRemoveDialog | 6 | none |
| FleetQuickProfile | 6 | none (uses Snackbar/DialogService) |
| ServerDeleteDialog | 6 | none |
| SteamWorkshopAccountDialog | 6 | none |
| SteamWorkshopApiKeyDialog | 6 | none |
| AnalyticsPanelDialog | 7 | none |
| ClusterConsoleDialog | 7 | none |
| DiscordLinkDialog | 7 | none |
| NewServerDialog | 7 | none |
| QuasarControlDialog | 8 | none |
| BackupFolderPicker | 9 | none |
| DiscordConsoleDialog | 9 | Tooltip ×1 |
| FleetChooseTemplate | 9 | Select ×2 |
| ServerConsoleDialog | 10 | none |
| MergeWorldTemplateModsDialog | 12 | Select ×1 |
| FolderPickerDialog | 16 | none |
| PluginManifestPickerDialog | 16 | none |
| DashboardSetupDialog | 26 | none |
| ImportExistingServerDialog | 64 | Select ×1 |
| FleetServerEditor | 113 | Select ×5, Tooltip ×2 |

## Phases

Each phase is a set of reviewable PRs. A PR should migrate a whole workflow
(component, callers, tests, CSS overrides, docs) rather than one control type
across the whole codebase. The exceptions are the mechanical service sweeps in
phase 2.

### Phase 0: groundwork (1–2 days)

1. Merge #454.
2. Add a **Mud usage ratchet test**. It compares a checked-in baseline of
   per-file counts against the source: Mud tags, `using MudBlazor`, Mud utility
   classes and inline `style` attributes. It fails when any file's count
   increases or a new file starts using Mud. Each migration PR lowers the
   baseline, so new code cannot reintroduce Mud during the migration.
3. Delete the unused `BrandingService.BuildMudTheme()`.
4. Add the 48 missing icon paths to `QuasarIcons`, copied from MudBlazor 9.4.0
   under the existing Apache license notice.
5. Switch `Quasar.Ui.Abstractions` to the Razor SDK and move the existing
   generic controls into it: icons, icon button, progress, operation loader,
   `CopyablePath`, `StableRows` and `QuasarNotice` from #454. Move the dialog and
   toast hosts into `Quasar.Client/Ui`. Apart from the new namespace and public
   visibility, these are moves only.
6. Deduplicate the layout CSS. Make `.quasar-stack` the canonical class and keep
   `.quasar-layout` as an alias, settle on `.quasar-row`, and merge the identical
   type classes. Aliases stay until phase 6 so no page changes appearance.

### Phase 1: controls and patterns (11–16 days)

Controls and patterns go in `Quasar.Ui.Abstractions` and are styled in
`quasar-ui.css`. They are a **public API from the first PR**, because plugins adopt
them as soon as the library is complete:

- XML docs on every public parameter. No reference to `Quasar.Client` or Quasar
  services.
- An approved public-API snapshot test, so any signature change shows up in
  review.
- Styling through the public `--quasar-palette-*` tokens plus documented
  per-component variables (`--quasar-button-*`, `--quasar-field-*` and so on),
  following the dialog and toast CSS contract.

Every control needs bUnit tests and an entry on the
`tests/Quasar.Wasm.PluginChecks` fixture page, which is a real plugin. This
also tests the plugin consumption path. The browser checks must include each
popup control rendered **inside a native modal dialog**.

| PR | Controls | Notes |
| --- | --- | --- |
| 1a | `QuasarTone`, `StatusTones`, `QuasarButton`, `QuasarChip`, `QuasarStatusBadge`, `QuasarTooltip`, grid CSS | `QuasarTone` and an `Appearance` parameter (Filled/Outlined/Text) replace Mud's `Color` × `Variant` matrix. Include a mapping table in the PR so call sites convert mechanically. `QuasarButton` has a `Busy` parameter that disables the button and shows a spinner. The tooltip uses `aria-describedby` and also appears on keyboard focus. |
| 1b | `QuasarTextField`, `QuasarNumberField<T>`, `QuasarSelect<T>`, `QuasarCheckbox`, `QuasarSwitch`, `QuasarRadioGroup` | One shared field wrapper handles label, helper text, error, `aria-invalid` and `aria-describedby`, and integrates with `EditContext`/`FieldIdentifier`. `Immediate` binds on input, otherwise on change. The number field parses with the invariant culture, clamps to `Min`/`Max`, and supports nullable types. |
| 1c | `QuasarMenu`, `QuasarTabs`, `QuasarDisclosure`, `QuasarListbox` | Extract these from the existing native menu and `<details>` implementations. Tabs need arrow-key navigation and a two-way bound active index. |
| 1d | `QuasarTable<T>` | Sort labels, pager, hover and dense modes, row click and expansion, empty and loading states, and optional server data. Two narrow-screen layouts sit behind a `NarrowLayout` parameter (`Stack` or `Scroll`) with a `Breakpoint`. `Stack` turns rows into label/value cards using each column's data label, replacing Mud's `Breakpoint` for its 13 current tables. `Scroll` keeps the table and scrolls it sideways inside `.quasar-table-scroll`, as the fleet table does. Both layouts share the same column definitions, sorting and row actions. |

Then the patterns and shared feature components. Each one ships with at least
one real consumer, so it is not designed without a use case:

| PR | Components | First consumer |
| --- | --- | --- |
| 1e | `QuasarPage`, `QuasarSection`, `QuasarToolbar`, `QuasarEmptyState` | HostMachines (small, has heading, actions, error, loading and empty state) |
| 1f | `QuasarCopyButton` (with `CopyablePath` built on it), `QuasarConfirmDialog` (`RequireText`) | ClusterRemoveDialog, ServerDeleteDialog |
| 1g | `QuasarFileBrowserDialog` (folder or file mode) | Replaces FolderPicker, PluginManifestPicker and BackupFolderPicker |
| 1h | `QuasarLogView` | ClusterConsoleDialog, then the other consoles in phase 3 |
| 1i | `ServerTargetSelect`, `DashboardTarget`, `PlayerActionsMenu`, `PlayerIdentity` (internal feature components) | PlayersView reuses the `FleetLiveDetails` actions |
| 1j | Publish the library to plugins | Update `QuasarPluginSystem.md` and `skills/quasar-book/UiPlugins.md` with the component reference. Switch the `quasar-plugin-template` from its scoped native controls to the library. Release with the next Quasar version. |

Plugins can adopt the library from 1j onward, while core pages are still
migrating.

### Phase 2: service sweeps (1–2 days)

These are mechanical changes and do not depend on phase 1, so they can run in
parallel with it.

1. `ISnackbar` → `IQuasarToastService` in all 24 files (~218 calls), mapping
   `Severity` to `QuasarSeverity`.
2. `ShowMessageBoxAsync` → native message box (29 calls). This needs #454.
3. Update test assertions for toasts and confirmations.

### Phase 3: dialogs (3–5 days)

Each dialog moves together with its callers: `ShowAsync<T>`,
`DialogParameters` → `QuasarDialogParameters<T>`, and `IMudDialogInstance` →
`IQuasarDialogInstance`. Shared dialogs from phase 1 replace these 23 dialogs
where they fit:

- **Merged dialogs:** the three folder and file pickers become one
  `QuasarFileBrowserDialog`. Server, cluster and Discord consoles move onto
  `QuasarLogView`. ClusterRemove, ServerDelete and RemoveHostEnrollment use
  `QuasarConfirmDialog` or the message box.
- **Wave A** (no popover controls): the remaining small dialogs, such as
  PluginCatalogDescription, ConfigProfilePendingChanges, WorldTemplateFromServer,
  the Steam Workshop credential dialogs, AnalyticsPanel, DiscordLink, NewServer,
  QuasarControl, FleetQuickProfile and DashboardSetup.
- **Wave B** (needs `QuasarSelect`/`QuasarTooltip`): FleetChooseTemplate,
  MergeWorldTemplateModsDialog.
- **Wave C**: ImportExistingServerDialog, then FleetServerEditor. Together they
  are the complete server create/edit/import workflow.

### Phase 4: pages and views (8–12 days)

Each slice follows the same order, ideally as one PR per step for large files:

1. `git mv` the slice's files into `Features/<Area>/` (a move-only PR).
2. Replace the page skeleton with `QuasarPage`/`QuasarSection`. This step also
   removes most of the Mud utility classes and inline styles.
3. Replace the controls. Use `ServerTargetSelect`, `QuasarToolbar` and the
   shared dialogs wherever they fit.
4. Move status mappings into `StatusTones` and delete the page's own
   `GetXColor` methods.
5. Optionally adopt `ActionRunner`/`PollingRefresh` where tests cover the
   behavior.

The slices follow the workflow inventory in the assessment:

| Slice | Files (Mud tags) | Special cases |
| --- | --- | --- |
| 4a Small and shell | Home (2), DashboardPower (1), AccessView (4), HostEnroll (1), notification bell and card, DiscordLinkButton, BrowserClusterNotice, RemoveHostEnrollment, LivePluginEditor, RoleMappingAccounts, ClusterDetailPanel, NotificationPage, ListingPage, SetupPage, AppearancePage, AuditLogPage, ClusterDetail | |
| 4b Operational | PlayersView (62), PlayerAccessView (39), PlayerLinksView (13), ChatView (15), EntitiesView (60), AnalyticsView (64), PluginStatsHistoryView (11), DiscordView (94), LivePluginLogs (40) | Autocomplete (Chat); date and time pickers (Analytics); multi-select (Discord); server-data table (Logs) |
| 4c Administration | SecurityPage (56), Backups (117), UpdatesPage (72) | Time picker and switches (Backups) |
| 4d Clusters, hosts, worlds | SetupCluster (42), ConvertCluster (19), ClusterDeploymentPanel (41), ClusterFleetPanel (29), ClusterContentPanel (19), HostMachines (43), HostEnrollmentForm (23), WorldLibrary (38), WorldImportForm (35) | Stepper (SetupCluster) |
| 4e Plugins | UiPluginsView (96), BrowserSdkAuthorization (18), ServerPluginSettings (9) | |
| 4f Configuration | ConfigEditor (207), SchemaConfigEditor (52) | Split ConfigEditor by tab. The drop container needs a keyboard alternative. SchemaConfigEditor must keep every PluginSdk feature: tabs, sections, columns, ranges, collections, dictionaries, nested values, errors, defaults, dirty drafts and apply acknowledgements. |

### Phase 5: theme bridge (1–2 days)

1. Add a Quasar color parser and port the `NativeUiThemeTests` round-trip
   assertions to it. Switch `QuasarColorField` to the new parser.
2. Remove `QuasarTheme.Create`/`Default`/`ToMudPalette*` and the
   `ThemePalette.ToMudPalette*` accessors outright, with no `[Obsolete]` step.
3. Remove the Mud providers from `QuasarThemeProvider`. The
   `quasar.theme.mode` key and OS theme observation through `matchMedia` must
   keep working.

### Phase 6: cutover (2–3 days)

This is a single cutover with no compatibility code.

1. **Before you start:** recheck every QuasarHub catalog entry at its current
   pinned commit for MudBlazor package references, `<Mud*>` markup and
   `ISnackbar`/`IDialogService`. Clean any plugin that has picked up Mud and
   bump its pin first. Also check the same entries for the old
   `Quasar.Components.Shared.QuasarIcon`, `QuasarIconButton` and `QuasarIcons`
   names. They moved to `Quasar.Ui.Components`, and Quasar forwards only the new
   names.
2. Remove both package references, the CSS/JS links in `App.razor`, the
   `QuasarWebReleaseLayout` entries, `AddMudServices()` and every
   `@using MudBlazor`.
3. Delete the `.mud-*` and `--mud-*` rules in `app.css`, `quasar-ui.css`,
   `ui-plugins.css` and the scoped stylesheets, along with the phase 0 CSS
   aliases and `DashboardPageHeading`. Compare screenshots at 390, 1440 and
   1920 px in light and dark mode.
4. Remove the remaining Mud test helpers and replace the ratchet with an
   assertion that nothing references MudBlazor.
5. Update the docs: replace the MudBlazor compatibility section in
   `QuasarPluginSystem.md`, and update this plan, `QuasarUiRewrite.md`, the
   affected `skills/quasar-book` pages (`book.py stale`) and the plugin template
   guidance. The release notes must state that MudBlazor and its providers are
   gone, that UI plugins from outside the catalog which use MudBlazor no longer
   load, and that the replacement is the component library in
   `Quasar.Ui.Abstractions`.

### Acceptance (3–5 days, overlaps phases 4–6)

Check keyboard operation, focus and contrast on every migrated workflow, and
the responsive layout from 320 px up. This phase ends with the real Quasar,
server, Agent and plugin smoketest, which needs explicit approval to run.

**Total estimate: about 30–47 engineering days.** This is a planning estimate,
not a measured schedule. Consolidation adds about 4–7 days to phases 0 and 1
for the patterns and the folder move, and saves roughly 3–4 days in phases 3
and 4, because merged dialogs and the page scaffold replace bespoke rewrites.
Supporting both table layouts and hardening the library as a public API add
another 2–3 days to phase 1. Skipping a deprecation release saves a little in
phases 5 and 6. Phases 1 and 2 can overlap, and wave A of phase 3 can start
once 1a and 1b have landed.

## Decisions

1. **Plugin compatibility:** clean the catalog plugins ahead of time, then cut
   over in one release. There is no deprecation release, load check or
   compatibility code.
2. **Narrow tables:** `QuasarTable<T>` supports both the stacked and the
   horizontally scrolling layout.
3. **Plugin access:** the controls and patterns are a public library in
   `Quasar.Ui.Abstractions` from the start, and are published to plugins as soon
   as phase 1 is complete (1j).
4. **Feature folders:** adopt the `Quasar.Client/Ui` + `Features/<Area>/`
   layout, moved with move-only PRs at the start of each slice.
5. **Behavior helpers:** `ActionRunner` and `PollingRefresh` are adopted only in
   slices whose tests already cover busy, error and refresh behavior. Elsewhere
   they wait until that coverage exists.

## Risks

- **Validation semantics.** Mud's `Required`, `Validation` functions and
  `Immediate` timing affect when errors appear. Keep the existing tests and add
  tests where none exist before swapping a form.
- **Weaker tests.** Many bUnit tests locate elements with `FindComponent<MudX>`
  or `.mud-*` selectors. Replace these with role-, label- or `data-testid`-based
  queries without dropping assertions.
- **Visual drift.** The `app.css` overrides exist to make Mud match the native
  look. Remove each workflow's overrides in the same PR that migrates the
  workflow, so the visual change can be reviewed together with the code change.
- **Public API churn.** Once plugins use the library, renaming a parameter
  breaks them. The API snapshot test makes such changes visible in review.
  Breaking changes follow the Quasar version and are listed in the release
  notes. Until 1j, the library can still change freely.
- **Non-catalog plugins break at cutover.** This is accepted (decision 1). The
  release notes are the only mitigation.
- **Over-generalized patterns.** A pattern with too many parameters for each
  page's special cases becomes worse than the duplication it replaced. Each
  pattern ships with a real consumer. A page that doesn't fit uses the controls
  directly instead of adding a parameter.
- **Large rename diffs.** The feature-folder moves touch tests and `@using`
  lines. Keep them move-only and separate from migration PRs so reviews and
  `git blame` stay usable.
- **Popover regressions.** If a Mud popover control is left inside a migrated
  native modal, its popup will not open. The browser fixture check from phase 1
  and the ratchet test reduce this risk.
