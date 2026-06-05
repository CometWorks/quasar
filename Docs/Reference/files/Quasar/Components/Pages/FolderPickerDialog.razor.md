# Quasar/Components/Pages/FolderPickerDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
General-purpose server-side folder browser dialog. Renders a navigable directory tree with breadcrumb navigation, bookmark shortcuts, hidden-folder toggle, and an optional world-folder validation mode. Returns the selected absolute path on confirmation. Used from `Configs.razor` (dev-folder picker) and server instance editors (world-folder picker).

## Structure
- **No route** (dialog component only)
- **Cascading parameter:** `IMudDialogInstance MudDialog`
- **Injected services:** `FileBrowserService` (Browser)
- **Parameters:**
  - `InitialPath` (string?) — starting directory; falls back to the user profile folder if null/empty.
  - `DialogTitle` (string) — defaults to `"Pick world folder"`.
  - `RequireWorldFolder` (bool) — when true, the "Use this folder" button is only enabled if the current path contains a `Sandbox.sbc` file (detected by `FileBrowserService.IsWorldFolder`).
- **UI:**
  - Path text field with Enter-to-navigate and a refresh adornment button; "Go" button; "Up" icon button.
  - "Show hidden folders" checkbox.
  - Shortcut chips row (from `Browser.GetShortcuts()`).
  - Breadcrumb buttons row — each crumb navigates to that directory level.
  - Error/success alert (error on navigation failure; success when a valid world folder is detected with `RequireWorldFolder=true`).
  - `MudList` of subdirectory entries (max-height 320 px, scrollable); world-folder entries shown with a green globe icon and "world" chip.
  - Cancel and "Use this folder" buttons (latter disabled unless `CanUseCurrentFolder`).
- **Key methods:**
  - `NavigateTo(string)` — calls `FileBrowserService.ResolvePath`, `Browser.ListDirectories`, `FileBrowserService.GetBreadcrumbs`; sets `_currentPath`; catches and displays exceptions.
  - `GoUp()` — navigates to `Directory.GetParent(_currentPath)`.
  - `HandleShowHiddenChanged(bool)` — toggles hidden-folder visibility and re-navigates.
  - `HandlePathKeyDownAsync` — navigates on Enter key.
  - `UseCurrent()` — closes with `DialogResult.Ok(_currentPath)`.
- **`CanUseCurrentFolder`:** `_error` empty AND `Directory.Exists(_currentPath)` AND (not `RequireWorldFolder` OR `FileBrowserService.IsWorldFolder(_currentPath)`).

## Dependencies
- [`Quasar/Services/FileBrowserService.cs`](../../Services/FileBrowserService.cs.md)
- MudBlazor (`MudDialog`, `MudTextField`, `MudCheckBox`, `MudList`, `MudListItem`, `MudChip`, `MudIconButton`, `MudButton`, `MudAlert`)

## Notes
- All filesystem operations are server-side; the dialog is safe to use in Blazor Server.
- On Linux the path resolution in `FileBrowserService` handles case-insensitive path correction.
