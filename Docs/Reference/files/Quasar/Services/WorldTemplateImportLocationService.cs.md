# Quasar/Services/WorldTemplateImportLocationService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 2

## Summary
Scoped helper for the world-template import UI. It builds shortcut chips for the Space Engineers Dedicated Server content folders that ship template/scenario worlds, discovers installed world/scenario template folders for one-click import, picks the initial browse location from the current field, the last selected source folder in browser `localStorage`, or the first existing DS content shortcut, and remembers the last selected folder after a successful picker result.

## Structure

Namespace: `Quasar.Services`

**`WorldTemplateImportLocationService`** — sealed scoped service.

**`InstalledWorldTemplateSource`** — record returned for optional preset imports: `Category`, `DisplayName`, `SourcePath`, `SourceDisplayPath`, `Description`.

| Member | Description |
|---|---|
| `GetContentShortcuts()` | Returns existing DS content shortcut chips, ordered as `Content/CustomWorlds`, `Content/QuickStarts`, `Content/Scenarios`. Roots are resolved from `ManagedRuntimeOptions.DedicatedServerInstallDirectory`, an optional `DedicatedServer64OverridePath` parent, and the managed default `MagnetarPaths.GetQuasarManagedDedicatedServerInstallDirectory()`. |
| `GetInstalledWorldTemplates()` | Scans the same DS content roots for folders containing `Sandbox.sbc`, including recursive scenario worlds. Xbox scenario variants are skipped, names are derived from `SessionName` when useful, generic platform folders such as `PC` are ignored when falling back to folder names, short relative source paths are computed for display, and results are deduped by full source path. |
| `GetInitialPathAsync(currentPath)` | Uses the current source path when present; otherwise uses the stored last source folder if it still exists; otherwise falls back to the first existing DS content shortcut or an empty string so `FolderPickerDialog` falls back to the user profile. |
| `RememberAsync(path)` | Resolves and stores the selected folder in `localStorage` key `quasar.worldTemplates.lastSourceFolder` when it exists. JS interop disconnect/prerender errors are ignored. |

## Dependencies

- [`Quasar/Services/ManagedRuntimeOptions.cs`](ManagedRuntimeOptions.cs.md) — managed DS install and override paths.
- [`Quasar/Services/FileBrowserService.cs`](FileBrowserService.cs.md) — `~` expansion and full path resolution.
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md) — managed DS default root.
- `ILocalStorageService` / `JSDisconnectedException` — browser-side last-folder persistence.
- `System.Xml.Linq` — reads installed template `SessionName` from `Sandbox.sbc`.
