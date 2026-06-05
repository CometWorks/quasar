# Quasar/Services/BrowserLauncher.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`BrowserLauncher` is a static helper that decides whether to open a browser on startup and cross-platform launches the system default browser at a given URL. On Linux it requires a display server (`DISPLAY` or `WAYLAND_DISPLAY`) to be available.

## Structure
Namespace: `Quasar.Services`

**`BrowserLauncher`** — `static class`

| Member | Notes |
|--------|-------|
| `ShouldOpenBrowser(WebServiceOptions)` | Returns `false` in service mode or when `OpenBrowserOnStart` is false; on Linux additionally requires `DISPLAY` or `WAYLAND_DISPLAY`; on other platforms checks `Environment.UserInteractive` |
| `TryOpen(string url)` | On Linux tries `xdg-open`, then `gio open`, then `sensible-browser`; falls through to `Process.Start` with `UseShellExecute = true`; silently swallows all exceptions |
| `TryStartBrowserCommand(string, string)` | Private: starts a named launcher with redirected stdio and returns success/failure |

## Dependencies
- `Quasar/Models/WebServiceOptions.cs` — `IsServiceMode`, `OpenBrowserOnStart`
- BCL `System.Diagnostics.Process`
