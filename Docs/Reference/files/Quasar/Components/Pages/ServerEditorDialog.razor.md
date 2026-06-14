# Quasar/Components/Pages/ServerEditorDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog (no `@page` route) for creating or editing a `DedicatedServerDefinition`. It validates server identity/runtime settings, opens inline config/world-template creation dialogs, consumes world-import results that include a config-profile id, and can merge missing world-template mods directly into the selected config profile before save.

## Structure
- **`[CascadingParameter]`** — `IMudDialogInstance MudDialog`
- **`[Inject]`** (via `@code` `[Inject]` attributes, not `@inject` directives)
  - `QuasarConfigProfileCatalog ConfigProfiles`
  - `QuasarWorldTemplateCatalog WorldTemplates`
  - `DedicatedServerCatalog ServerCatalog`
  - `IDialogService DialogService`
  - `ISnackbar Snackbar`
- **`[Parameter]`s**
  - `DedicatedServerDefinition Definition` — the definition to edit (cloned internally on `OnInitialized`).
  - `bool IsEditing` — true when editing an existing server; false for create/clone.
  - `bool UniqueNameLocked` — true when the server is currently running, disabling the identifier field.
- **Key UI sections**
  - Identity section: display name, identifier (slug), listen port (validated unique), listen IP, config template select with inline "New Template" button (opens `ConfigProfileQuickCreateDialog`), world template select with inline "New Template" button (opens `WorldTemplateQuickImportDialog`), and a `.NET runtime` select bound to `DedicatedServerDefinition.ManagedRuntime`. The runtime select is disabled (and forced to .NET 10) on non-Windows hosts; on Windows it offers `.NET 10` (default) and `.NET Framework 4.8` (Legacy). Helper text notes that profile session settings and mods are written into the world's `Sandbox_config.sbc` on every start. Warning alert when selected world template has mods not present in the config profile, including a "Merge Into ..." action that imports missing mods directly into the selected profile.
  - Restart Policy expansion panel: restart delay, max consecutive restart attempts, restart-on-crash checkbox, daily restart schedule (HH:mm tokens), maximum uptime (HH:mm).
  - Health Policy expansion panel: enable health monitoring, auto-restart on unhealthy, startup grace, agent attach retry attempts/delay, heartbeat timeout, simulation frame window, minimum simulation rate (0–1), warn after uptime hours.
  - Runtime expansion panel (Advanced): startup priority, ready priority, CPU affinity (text field bound to `DedicatedServerDefinition.CpuAffinity`, placeholder e.g. "0-7 or 0-7,16-23"; helper text notes comma-separated core numbers/ranges, empty = all cores, minimum `CpuAffinitySpec.MinimumCores` cores; label shows the host's logical core count), DS log retention (numeric field bound to `DedicatedServerDefinition.DsLogFilesToKeep`), launch-environment logging (checkbox bound to `DedicatedServerDefinition.LogLaunchEnvironment`, warning that logs may expose secrets), and launch arguments (token help text lists available substitution tokens).
  - Paths expansion panel (Advanced): launcher executable path, working directory, DS app-data path, Magnetar app-data path, world path, and rendered DS config path.
- **Validation**
  - `ValidateUniqueNameField` — regex `^[a-zA-Z0-9_-]+$`, duplicate name check against `ServerCatalog`.
  - `ValidatePortField` — range 1–65535, duplicate port check.
  - `ValidateRestartSchedule` — validates HH:mm token list.
  - `ValidateMaximumUptime` — validates HH:mm duration.
  - `ValidateConfigProfileField` / `ValidateWorldTemplateField` — non-empty check.
  - CPU affinity — validated via `CpuAffinitySpec.TryParse(value, Environment.ProcessorCount, ...)` (empty allowed).
- **`HandleDisplayNameChanged`** — auto-generates identifier via `SlugifyForIdentifier` unless already manually edited or in edit mode.
- **`OpenImportWorldTemplateDialogAsync`** — launches `WorldTemplateQuickImportDialog` with the current `ConfigProfileId`; consumes `WorldTemplateQuickImportResult` so importing a world can also select or create the matching config profile.
- **`TemplateModsMissingFromProfile`** — reads `Sandbox_config.sbc` from the selected world template directory via `WorldSandboxConfigEditor.ReadMods` and diffs against the profile's mod list.
- **`MergeSelectedWorldTemplateModsIntoProfileAsync`** — one-click merge for the warning alert; appends only missing template mods to the selected profile, saves it, and updates the editor state.
- **`SaveAsync`** — validates form, clones editor state, normalises whitespace, closes dialog with `Ok(updated)`.
- **`IsWindowsHost` / `FormatRuntime` / `RuntimeHelperText`** — `IsWindowsHost` (`OperatingSystem.IsWindows()`) gates the runtime selector; `FormatRuntime` maps the enum to display text; `RuntimeHelperText` explains the choice (or that .NET 10 is the only runtime off Windows). `OnInitialized` pins `ManagedRuntime` to `DotNet10` on non-Windows hosts to keep the disabled selector coherent.

## Dependencies
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
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
- Default path values shown in helper text are computed live from `_editor.UniqueName` (or `<unique-name>` placeholder) and `MagnetarPaths` utilities.
- This component was previously named `ServerEditorDialog.razor`.
