# Quasar/Components/Pages/DiscordConsoleDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog that displays the dedicated Discord integration log (`discord.log`). It mirrors the server console log UX with a file path caption, full-log download button, Refresh action, exception-excerpt mode, and a scrollable monospace log pane.

## Structure
- **No route:** opened as a dialog from `Discord.razor`.
- **Injected services:** `WebServiceOptions`, `IJSRuntime`
- **Key UI sections:**
  - Status/path header showing line count, tail/exceptions mode, and resolved `discord.log` path.
  - Actions: download (`/api/discord/log/download`), Exceptions, Refresh.
  - Missing-log info alert, error alert, or scrollable `.quasar-console-output` containing a `<pre>` with log text.
- **Key constants:** `LogTailBytes` (256 KiB), `ExceptionSearchBytes` (4 MiB), `ExceptionContextLines` (50), `ExceptionMatchLimit` (10).
- **Key state:** `_content`, `_logPath`, `_lineCount`, `_truncated`, `_missing`, `_error`, `_loaded`, `_scrollPending`, `_mode`.
- **Key methods:** `LoadAsync`, `LoadExceptionsAsync`, `ReadTailAsync`, `ExtractExceptionBlocks`, `CountLines`, `DiscordLogStatusText`, `ResolveLogPath`, `ScrollToBottomAsync`.

## Dependencies
- [`Quasar/Services/QuasarLoggingConfigurator.cs`](../../Services/QuasarLoggingConfigurator.cs.md) — resolves the Discord log path
- [`Quasar/Services/WebServiceOptions.cs`](../../Services/WebServiceOptions.cs.md) — supplies the configured log directory
- [`Quasar/Program.cs`](../../Program.cs.md) — maps `/api/discord/log/download`
- MudBlazor dialog components and JS interop (`quasarConfigs.scrollToBottom`)

## Notes
- The dialog reads the log with `FileShare.ReadWrite | FileShare.Delete` so it can tail files while NLog is still writing.
- Exception mode searches only the latest 4 MiB to keep UI reads bounded.
