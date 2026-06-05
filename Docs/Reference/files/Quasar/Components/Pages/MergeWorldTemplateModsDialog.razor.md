# Quasar/Components/Pages/MergeWorldTemplateModsDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog used from the Configs page to merge mods from a selected world template into the current config profile. It computes a diff of new vs. already-present Workshop IDs and returns only the net-new `QuasarModSelection` list via `DialogResult.Ok`.

## Structure
- **No `@page` route** — dialog only.
- **`[Inject]`**
  - `QuasarWorldTemplateCatalog WorldTemplates`
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]`**
  - `IReadOnlyCollection<long> ExistingWorkshopIds` — the caller passes the profile's current mod Workshop IDs so the dialog can compute the diff.
- **Key UI**
  - `MudSelect` — picks the target world template; triggers `LoadDiff()` on change.
  - `MudAlert` — shows count of new and duplicate mods.
  - `MudTable` — lists new mods (Name, Workshop ID) when `_newMods.Count > 0`.
  - "Merge N mod(s)" confirm button — disabled when `_newMods` is empty or load error is present.
- **Key logic**
  - `LoadDiff` — calls `WorldSandboxConfigEditor.ReadMods(sandboxConfigPath)`, partitions results into `_newMods` and `_duplicateCount` using a `HashSet<long>` of `ExistingWorkshopIds`.
  - Returns `DialogResult.Ok(_newMods)` on confirm.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md)
- `Quasar/Utilities/WorldSandboxConfigEditor.cs` — `ReadMods`, `SandboxConfigFileName`.
- `Quasar/Models/QuasarModSelection.cs`
- MudBlazor — `MudDialog`, `MudSelect`, `MudTable`, `MudAlert`.
