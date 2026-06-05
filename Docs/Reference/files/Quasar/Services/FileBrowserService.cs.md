# Quasar/Services/FileBrowserService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`FileBrowserService` provides server-side directory listing, shortcut generation, and breadcrumb computation for the world-path picker UI. It identifies Space Engineers world folders by the presence of `Sandbox.sbc` and offers well-known shortcuts to SE save locations.

## Structure
Namespace: `Quasar.Services`

**`FileBrowserService`** — `sealed class`

| Member | Notes |
|--------|-------|
| `ListDirectories(string path, bool showHidden)` | Enumerates immediate subdirectories; filters hidden entries unless `showHidden`; marks world folders; sorts world folders first, then alphabetically |
| `GetShortcuts()` | Returns shortcuts for `~` (Home), SE Dedicated Saves, and SE Player Saves if those directories exist |
| `IsWorldFolder(string path)` | `static`; returns `true` if `Sandbox.sbc` exists in the directory |
| `ResolvePath(string path)` | `static`; expands leading `~` to the user profile directory; calls `Path.GetFullPath` |
| `GetBreadcrumbs(string path)` | `static`; walks `DirectoryInfo.Parent` chain to build a root-to-leaf list |

**Companion records:**
- `FileBrowserEntry(string Name, string FullPath, bool IsWorldFolder)`
- `FileBrowserShortcut(string Label, string FullPath)`
- `FileBrowserBreadcrumb(string Label, string FullPath)`

## Dependencies
None beyond BCL.

## Notes
`UnauthorizedAccessException` is silently swallowed per directory so a single inaccessible entry does not abort the listing.
