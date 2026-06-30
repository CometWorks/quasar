# Quasar/wwwroot/viewer/logging.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Small in-page log/statistics export helper for the standalone grid viewer. It retains the latest 500 timestamped log entries, batches visible log DOM updates with `requestAnimationFrame`, can download the retained log, and can export the current scene summary plus stats panel values as text files.

## Structure

| Export | Purpose |
|---|---|
| `log(message, isWarning = false)` | Adds an `INFO` or `WARN` line to the retained buffer and schedules a visible log refresh. |
| `downloadLog()` | Creates a temporary text blob and downloads `quasar-viewer.log`. |
| `exportStatistics()` | Creates a temporary text blob with the current scene summary and statistics panel values and downloads `quasar-viewer-statistics.txt`. |

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for cached summary/log elements and the current stats object.
- Browser `Blob`, object URL, and animation-frame APIs.

## Notes
Successful texture-load chatter is kept out of this DOM log by default; warnings and fallbacks remain visible and downloadable. Path-cache diagnostics are exposed through the viewer stats panel and can be exported with the statistics download.
