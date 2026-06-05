# Quasar/Components/Pages/ServerConsoleDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog (no `@page` route) that shows two tabs of diagnostic output for a specific server identified by `UniqueName`: a live process-console tab fed by `PluginLogStream`, and a Magnetar `info.log` viewer that reads the last 256 KB of the log file from disk. Opened from `Servers.razor` via `IDialogService`. Previously named `ServerConsoleDialog`.

## Structure
- **`@implements IDisposable`**
- **`[Inject]`**
  - `PluginLogStream LogStream`
  - `IJSRuntime JS`
- **`[CascadingParameter]`** — `IMudDialogInstance MudDialog`
- **`[Parameter]`**
  - `string UniqueName` — the server's unique identifier; used to scope log entries and resolve the `info.log` path.
- **Tabs**
  - **Process console** (index 0) — renders `_entries` (filtered by `MagnetarOnly` switch) as a monospace grid with columns: timestamp, level (coloured), optional plugin name, message, optional exception. Auto-scrolls to bottom; respects "near bottom" heuristic before each update. Includes a Clear button.
  - **Magnetar info.log** (index 1) — reads tail of `info.log` from `MagnetarPaths.GetQuasarServerMagnetarAppDataDirectory(UniqueName)/info.log`; shows line count, truncation notice, file path, and a Refresh button. Loaded lazily on first tab switch.
- **Key state:** `_entries`, `MagnetarOnly`, `_activeTabIndex`, `_infoLogContent`, `_infoLogPath`, `_infoLogLineCount`, `_infoLogTruncated`, `_infoLogMissing`, `_infoLogError`, `_infoLogLoaded`.
- **JS interop calls:**
  - `quasarConfigs.scrollToBottom(containerId)` — scroll the console div to the bottom.
  - `quasarConfigs.isScrolledNearBottom(containerId, 48)` — returns bool; controls auto-scroll.
- **`ReadTailAsync`** — opens the log file with `FileShare.ReadWrite | FileShare.Delete` (safe for live writing), seeks to last `InfoLogTailBytes` bytes, drops the partial leading line after seek.
- **`Refresh()`** — re-reads `LogStream.GetEntries(UniqueName)`, applies `MagnetarOnly` filter.
- **`IsMagnetar(entry)`** — returns true when `entry.Plugin == "Magnetar"` (case-insensitive).
- **Inline `<style>` block** — CSS for `.quasar-console-output`, `.quasar-console-line`, `.quasar-console-line-with-plugin`, and log-level/plugin colour classes.

## Dependencies
- `Quasar/Services/PluginLogStream.cs`
- `Quasar/Paths/MagnetarPaths.cs`
- MudBlazor — `MudDialog`, `MudTabs`, `MudTabPanel`, `MudSwitch`, `MudButton`.
- `Microsoft.JSInterop` (`IJSRuntime`, `JSDisconnectedException`).

## Notes
- `JSDisconnectedException` from scroll helpers is silently caught; the dialog still functions even after the circuit disconnects.
- `info.log` is read with `FileShare.ReadWrite | FileShare.Delete` to avoid locking the file while Magnetar writes to it.
- `InfoLogTailBytes = 256 * 1024`; older log content beyond that boundary is not shown, but this is indicated to the user.
- This component was previously named `ServerConsoleDialog.razor`.
