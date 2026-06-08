# Quasar/Components/Pages/ServerConsoleDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog (no `@page` route) that shows two file-backed diagnostic logs for a specific server identified by `UniqueName`: the latest Space Engineers Dedicated Server log from the server's app-data directory, and Magnetar's `info.log`. The server-log tab renders a bounded 500-line window and rolls that window by 250 lines when browser-side scroll/key listeners request the next slice, keeping DOM size stable for large logs and avoiding per-scroll Blazor events. Both tabs can show recent content or extract recent exception excerpts. Opened from `Servers.razor` via `IDialogService`. Previously named `ServerConsoleDialog`.

## Structure
- **`@implements IAsyncDisposable`** — detaches JS rollover listeners and disposes the `DotNetObjectReference` when the dialog closes.
- **`[Inject]`**
  - `DedicatedServerCatalog ServerCatalog`
  - `IJSRuntime JS`
- **`[CascadingParameter]`** — `IMudDialogInstance MudDialog`
- **`[Parameter]`**
  - `string UniqueName` — the server's unique identifier; used to resolve the Dedicated Server log and Magnetar `info.log` paths.
- **Tabs**
  - **Server log** (index 0) — resolves the newest `SpaceEngineersDedicated*.log` under the configured `DedicatedServerDefinition.DedicatedServerAppDataPath` (falling back to the Quasar-managed default), reads the latest 500 complete lines, and offers Download, Exceptions, and Refresh actions. Download points to `/api/servers/{UniqueName}/logs/server/download` for the full file. While in normal log mode, JS listens for edge scrolling and `Ctrl+PageUp/PageDown` to request 250-line older/newer moves, plus `Ctrl+Home/End` to jump to the oldest or newest window.
  - **Magnetar info.log** (index 1) — reads tail of `info.log` from the configured `DedicatedServerDefinition.MagnetarAppDataPath` (falling back to `MagnetarPaths.GetQuasarServerMagnetarAppDataDirectory(UniqueName)`); shows line count, truncation notice, file path, and Download/Exceptions/Refresh actions. Download points to `/api/servers/{UniqueName}/logs/magnetar/download`; Exceptions scans the recent log tail for `Exception` lines and shows each latest match with about 50 following lines. Loaded lazily on first tab switch.
- **Key state:** `_serverLogLines`, `_serverLogPath`, `_serverLogLineCount`, `_serverLogWindowStart`, `_serverLogRolloverBusy`, `_serverLogInteropDirty`, `_serverLogInteropAttached`, `_serverLogInteropRef`, `_serverLogTruncated`, `_serverLogMissing`, `_serverLogError`, `_serverLogLoaded`, `_serverLogMode`, `_activeTabIndex`, `_infoLogContent`, `_infoLogPath`, `_infoLogLineCount`, `_infoLogTruncated`, `_infoLogMissing`, `_infoLogError`, `_infoLogLoaded`, `_infoLogMode`.
- **JS interop calls:**
  - `quasarConfigs.scrollToBottom(containerId)` — scrolls the active log container to the bottom after lazy load, refresh, or exception extraction.
  - `quasarConfigs.attachRolloverLog(containerId, dotNetRef, options)` — attaches JS scroll/click/keyboard listeners for the server-log tab and updates its can-load flags after each render.
  - `quasarConfigs.detachRolloverLog(containerId)` — removes those listeners on tab switch or dialog disposal.
- **`ReadTailAsync`** — opens the log file with `FileShare.ReadWrite | FileShare.Delete` (safe for live writing), seeks to the requested tail byte count, drops the partial leading line after seek.
- **`ReadLatestLineWindowAsync`** — streams the server log once while retaining only the latest 500 lines, returning the total line count and the starting line number for the retained window.
- **`ReadLineWindowAsync`** — streams the server log and returns a normalized 500-line slice for rollover navigation without materializing the entire log as DOM.
- **`RequestServerLogWindowShiftAsync` / `RequestServerLogWindowJumpAsync`** — JS-invoked endpoints that return a move result with updated older/newer availability and a scroll ratio for browser-side repositioning.
- **`ExtractExceptionBlocks`** — finds latest `Exception` matches in the searched tail and returns merged excerpts with roughly 50 following lines per match.
- **`ResolveLatestServerLogPath()`** — picks the newest `SpaceEngineersDedicated*.log` file from the DS app-data root, returning a wildcard-looking expected path when no file exists yet.
- **`ResolveInfoLogPath()`** — resolves the Magnetar `info.log` path from the configured server definition with the Quasar-managed default as fallback.
- **Inline `<style>` block** — CSS for the shared monospace log output container and file path text.

## Dependencies
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- MudBlazor — `MudDialog`, `MudTabs`, `MudTabPanel`, `MudButton`, `MudIconButton`, `MudTooltip`.
- `Microsoft.JSInterop` (`IJSRuntime`, `JSDisconnectedException`).

## Notes
- `JSDisconnectedException` from scroll helpers is silently caught; the dialog still functions even after the circuit disconnects.
- `info.log` is read with `FileShare.ReadWrite | FileShare.Delete` to avoid locking the file while Magnetar writes to it.
- `ServerLogWindowLines = 500`; `ServerLogRolloverLines = 250`; `InfoLogTailBytes = 256 * 1024`. Exception mode searches the latest 4 MB and returns up to 10 matching excerpts; server-log exception output is also capped to the same 500 rendered lines.
- This component was previously named `ServerConsoleDialog.razor`.
