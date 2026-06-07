# Magnetar.Protocol/Runtime/QuasarWebReleaseLayout.cs

**Module:** Magnetar.Protocol  **Kind:** class (static)  **Tier:** 1

## Summary
Shared layout validator for Linux Quasar web-release payloads. It rejects staged or downloaded web archives that are missing the worker executable or static assets required for the Blazor/MudBlazor UI to load.

## Structure
Namespace `Magnetar.Protocol.Runtime`; `public static class QuasarWebReleaseLayout`.

- `WorkerExecutableName` is the expected Linux worker executable name (`Quasar`).
- `RequiredRelativeFiles` lists required archive entries: the worker, Blazor runtime script, MudBlazor CSS/JS, global app CSS, config/chart scripts, and bundled uPlot assets.
- `ValidateDirectory(directory)` checks the extracted release directory and throws `InvalidOperationException` listing missing files.

## Dependencies
- `System`, `System.IO`, `System.Linq` (BCL only).

## Notes
This prevents a repeat of broken in-place UI activation where the worker can start and pass `/api/health` but the browser receives 404s for `/_framework/blazor.web.js`, `_content/MudBlazor/*`, or app chart assets.
