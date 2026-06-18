# Quasar/wwwroot/viewer/logging.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Small in-page log buffer for the standalone grid viewer. It retains the latest 500 timestamped entries, batches visible log DOM updates with `requestAnimationFrame`, and can download the retained log as a text file.

## Structure

| Export | Purpose |
|---|---|
| `log(message, isWarning = false)` | Adds an `INFO` or `WARN` line to the retained buffer and schedules a visible log refresh. |
| `downloadLog()` | Creates a temporary text blob and downloads `quasar-viewer.log`. |

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for the cached log element.
- Browser `Blob`, object URL, and animation-frame APIs.

## Notes
Successful texture-load chatter is kept out of this DOM log by default; warnings and fallbacks remain visible and downloadable.
