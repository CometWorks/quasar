# Quasar/Components/Pages/ServerEditorDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog (no `@page` route) for creating, cloning, or editing a `DedicatedServerDefinition`. It validates server identity/runtime settings, exposes per-server Space Engineers multiplayer-list server/world name overrides, opens inline config/world-template creation dialogs, consumes world-import results that include a config-profile id, can merge missing world-template mods directly into the selected config profile before save, and offers a confirmed reset-world action while editing stopped servers.

## Structure
- **`[CascadingParameter]`** — `IMudDialogInstance MudDialog`
- **`[Inject]`** (via `@code` `[Inject]` attributes, not `@inject` directives)
  - `QuasarConfigProfileCatalog ConfigProfiles`
  - `QuasarWorldTemplateCatalog WorldTemplates`
  - `DedicatedServerCatalog ServerCatalog`
  - `DedicatedServerSupervisor Supervisor`
  - `IDialogService DialogService`
  - `ISnackbar Snackbar`
- **`[Parameter]`s**
  - `DedicatedServerDefinition Definition` — the definition to edit (cloned internally on `OnInitialized`).
  - `bool IsEditing` — true when editing an existing server; false for create/clone.
  - `bool IsClone` — true for the clone workflow; changes title, intro copy, and primary action text while keeping create-style validation/editability.
  - `bool UniqueNameLocked` — true when the server is currently running, disabling the identifier field.
- **Key UI sections**
  - Identity section: display name, identifier (slug), in-game server name (`ServerName`) override, in-game world name (`WorldName`) override, listen port (validated unique), listen IP, config template select with inline "New Template" button (opens `ConfigProfileQuickCreateDialog`), world template select with inline "New Template" button (opens the tabbed `WorldTemplateQuickImportDialog` for predefined-world or custom import), and a `.NET runtime` select bound to `DedicatedServerDefinition.ManagedRuntime`. The display name blur handler copies the normalized display name into blank in-game server/world name fields so the overrides are not accidentally left undefined. The runtime select is disabled (and forced to .NET 10) on non-Windows hosts; on Windows it offers `.NET 10` (default) and `.NET Framework 4.8` (Legacy). Helper text notes that profile session settings and mods are written into the world's `Sandbox_config.sbc` on every start. Warning alert when selected world template has mods not present in the config profile, including a "Merge Into ..." action that imports missing mods directly into the selected profile.
  - Restart Policy expansion panel: restart delay, max consecutive restart attempts, restart-on-crash checkbox, daily restart schedule (HH:mm tokens), maximum uptime (HH:mm).
  - Health Policy expansion panel: enable health monitoring, auto-restart on unhealthy, startup grace, agent attach retry attempts/delay, heartbeat timeout, simulation frame window, minimum simulation rate (0–1), warn after uptime hours.
  - Runtime expansion panel (Advanced): startup priority, ready priority, CPU affinity (text field bound to `DedicatedServerDefinition.CpuAffinity`, placeholder e.g. "0-7 or 0-7,16-23"; helper text notes comma-separated core numbers/ranges, empty = all cores, minimum `CpuAffinitySpec.MinimumCores` cores; label shows the host's logical core count), DS log retention (numeric field bound to `DedicatedServerDefinition.DsLogFilesToKeep`), launch-environment logging (checkbox bound to `DedicatedServerDefinition.LogLaunchEnvironment`, warning that logs may expose secrets), "Disable implicit Magnetar mod load" (read-only checkbox bound to `DedicatedServerDefinition.DisableImplicitMagnetarModLoad`, so MudBlazor cannot toggle it before the dialog handler confirms; the checkbox icon color is driven by the same local `Color` value passed to `MudCheckBox`, while the label keeps MudBlazor's normal text color; tooltip/confirmation warn that enabling it passes `-noimplicitmod`, disables `MagnetarMod`, and breaks the mission screen popup used by server-side plugins; the yellow caption notes Magnetar already disables this mod load automatically when cross-play is enabled), and launch arguments (token help text lists available substitution tokens).
  - Paths expansion panel (Advanced): launcher executable path, working directory, DS app-data path, Magnetar app-data path, world path, edit-only reset-world warning/action, and rendered DS config path.
- **Validation**
  - `ValidateUniqueNameField` — regex `^[a-zA-Z0-9_-]+$`, duplicate name check against `ServerCatalog`.
  - `ValidatePortField` — range 1–65535, duplicate port check.
  - `ValidateRestartSchedule` — validates HH:mm token list.
  - `ValidateMaximumUptime` — validates HH:mm duration.
  - `ValidateConfigProfileField` / `ValidateWorldTemplateField` — non-empty check.
  - CPU affinity — validated via `CpuAffinitySpec.TryParse(value, Environment.ProcessorCount, ...)` (empty allowed).
- **`HandleDisplayNameChanged`** — auto-generates identifier via `SlugifyForIdentifier` unless already manually edited or in edit mode.
- **`HandleDisplayNameBlur`** — normalizes the current display name and copies it into the in-game server and world name fields only when those fields are still blank.
- **`OpenImportWorldTemplateDialogAsync`** — launches the large, tabbed `WorldTemplateQuickImportDialog` with the current `ConfigProfileId`; consumes `WorldTemplateQuickImportResult` so importing a predefined or custom world can also select or create the matching config profile.
- **`TemplateModsMissingFromProfile`** — reads `Sandbox_config.sbc` from the selected world template directory via `WorldSandboxConfigEditor.ReadMods` and diffs against the profile's mod list.
- **`MergeSelectedWorldTemplateModsIntoProfileAsync`** — one-click merge for the warning alert; appends only missing template mods to the selected profile, saves it, and updates the editor state.
- **`SaveAsync`** — validates form, clones editor state, normalises whitespace in UI and in-game names, closes dialog with `Ok(updated)`.
- **`ResetWorldAsync`** — edit-only destructive action that is disabled while the server is active; confirms the configured world path, deletes that folder recursively, and leaves the definition/template selection unchanged so the next start recreates the world from the selected template.
- **`ToggleDisableImplicitMagnetarModLoadAsync`** — drives the checkbox click without `ValueChanged`, so the saved editor model changes only after confirmation. It confirms before accepting a transition from off to on, since the resulting `-noimplicitmod` launch flag breaks MagnetarMod-backed mission-screen popups for server-side plugins; turning the setting off is applied directly.
- **`BuildControlledCheckboxColorStyle(Color)`** — maps the local MudBlazor `Color` used by the controlled checkbox to a scoped CSS variable so read-only styling can preserve the icon/button color without recoloring the label text.
- **Mode text helpers** — `DialogTitle`, `DialogDescription`, and `PrimaryActionText` render Create/Clone/Edit-specific labels so the shared dialog does not present clone as create.
- **`IsWindowsHost` / `FormatRuntime` / `RuntimeHelperText`** — `IsWindowsHost` (`OperatingSystem.IsWindows()`) gates the runtime selector; `FormatRuntime` maps the enum to display text; `RuntimeHelperText` explains the choice (or that .NET 10 is the only runtime off Windows). `OnInitialized` pins `ManagedRuntime` to `DotNet10` on non-Windows hosts to keep the disabled selector coherent.

## Dependencies
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md)
- [`Quasar/Models/DedicatedServerDefinition.cs`](../../Models/DedicatedServerDefinition.cs.md)
- [`Quasar/Models/ManagedServerRuntime.cs`](../../Models/ManagedServerRuntime.cs.md)
- `Quasar/Models/CpuAffinitySpec.cs`
- [`Quasar/Components/Pages/ServerEditorDialog.razor.css`](ServerEditorDialog.razor.css.md)
- `Quasar/Components/Pages/ConfigProfileQuickCreateDialog.razor` (inline template creation)
- `Quasar/Components/Pages/WorldTemplateQuickImportDialog.razor` (inline world template import and optional config-profile selection)
- `Quasar/Services/WorldSandboxConfigEditor.cs` (mod diff check)
- `Quasar/Paths/MagnetarPaths.cs` (default path helper text)
- MudBlazor — `MudDialog`, `MudForm`, `MudExpansionPanel`, `MudTextField`, `MudNumericField`, `MudSelect`, `MudCheckBox`, `IDialogService`.

## Notes
- `UniqueNameRegex` is compiled at class level (`static readonly`) for performance.
- When `UniqueNameLocked = true` the identifier field is disabled and a helper text warns to stop the server before renaming.
- Restart Policy, Health Policy, Runtime, and Paths expansion panels default to collapsed for create, clone, and edit modes; their `ExpandedChanged` handlers only track user toggles during the current dialog session.
- Default path values shown in helper text are computed live from `_editor.UniqueName` (or `<unique-name>` placeholder) and `MagnetarPaths` utilities.
- Reset World deletes only the configured world folder; the server definition remains saved separately and `DedicatedServerRuntimePreparer` seeds the selected template again on the next start because the world path no longer exists.
- This component was previously named `ServerEditorDialog.razor`.
