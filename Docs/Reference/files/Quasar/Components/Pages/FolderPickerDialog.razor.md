# Quasar/Components/Pages/FolderPickerDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
General-purpose server-side folder browser dialog. Renders a navigable directory tree with breadcrumb navigation, bookmark shortcut chips, caller-supplied shortcut chips, hidden-folder toggle, quick filtering for the current folder list, and an optional world-folder validation mode. Returns the selected absolute path on confirmation. Used from `Configs.razor` (dev-folder picker), server instance editors (world-folder picker), and world-template import flows.

## Structure
- **No route** (dialog component only)
- **Cascading parameter:** `IMudDialogInstance MudDialog`
- **Injected services:** `FileBrowserService` (Browser)
- **Parameters:**
  - `InitialPath` (string?) — starting directory; falls back to the user profile folder if null/empty.
  - `DialogTitle` (string) — defaults to `"Pick world folder"`.
  - `RequireWorldFolder` (bool) — when true, the "Use this folder" button is only enabled if the current path contains a `Sandbox.sbc` file (detected by `FileBrowserService.IsWorldFolder`).
  - `AdditionalShortcuts` (`IReadOnlyList<FileBrowserShortcut>`) — extra shortcut chips supplied by a caller, de-duplicated with built-in shortcuts and shown only when the target directory exists.
- **UI:**
  - Path text field with Enter-to-navigate and a refresh adornment button; "Go" button; "Up" icon button.
  - "Show hidden folders" checkbox.
  - "Filter current folder" search field that narrows the visible child-directory list without changing the current path; it clears when navigation moves to a different folder.
  - Shortcut chips row (from `Browser.GetShortcuts()` plus `AdditionalShortcuts`).
  - Breadcrumb buttons row — each crumb navigates to that directory level.
  - Error/success alert (error on navigation failure; success when a valid world folder is detected with `RequireWorldFolder=true`).
  - `MudList` of subdirectory entries (max-height 320 px, scrollable); world-folder entries shown with a green globe icon and "world" chip.
  - Cancel and "Use this folder" buttons (latter disabled unless `CanUseCurrentFolder`).
- **Key methods:**
  - `NavigateTo(string)` — calls `FileBrowserService.ResolvePath`, `Browser.ListDirectories`, `FileBrowserService.GetBreadcrumbs`; sets `_currentPath`; catches and displays exceptions.
  - `GoUp()` — navigates to `Directory.GetParent(_currentPath)`.
  - `HandleShowHiddenChanged(bool)` — toggles hidden-folder visibility and re-navigates.
  - `HandlePathKeyDownAsync` — navigates on Enter key.
  - `FilteredEntries` — filters `_entries` by case-insensitive folder-name substring from `_entrySearch`.
  - `UseCurrent()` — closes with `DialogResult.Ok(_currentPath)`.
  - `BuildShortcuts()` — combines built-in and caller-supplied shortcuts, de-duplicates by full path, and drops missing folders.
- **`CanUseCurrentFolder`:** `_error` empty AND `Directory.Exists(_currentPath)` AND (not `RequireWorldFolder` OR `FileBrowserService.IsWorldFolder(_currentPath)`).

## Dependencies
- [`Quasar/Services/FileBrowserService.cs`](../../Services/FileBrowserService.cs.md)
- MudBlazor (`MudDialog`, `MudTextField`, `MudCheckBox`, `MudList`, `MudListItem`, `MudChip`, `MudIconButton`, `MudButton`, `MudAlert`)

## Notes
- All filesystem operations are server-side; the dialog is safe to use in Blazor Server.
- On Linux the path resolution in `FileBrowserService` handles case-insensitive path correction.
