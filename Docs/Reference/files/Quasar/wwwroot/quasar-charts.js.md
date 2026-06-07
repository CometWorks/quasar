# Quasar/wwwroot/quasar-charts.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser-side analytics chart interop module registered as `window.quasarCharts`. It fetches chart series data over same-origin HTTP, renders responsive uPlot charts, keeps chart refreshes off the Blazor Server SignalR circuit, and reports drag-selected time ranges back to Blazor through a stored .NET object reference.

## Structure

**Global object:** `window.quasarCharts`

| Function | Purpose |
|---|---|
| `init(dotNetRef)` | Stores the .NET callback reference used for range selection events. |
| `sync(request)` | Fetches chart payloads, reconciles visible chart containers, updates or creates uPlot instances, and returns render status. |
| `dispose(containerId)` | Destroys one chart instance and its resize observer. |
| `disposeAll()` | Destroys all chart instances tracked by the module. |

**Internal behavior:**
- Holds a `Map` of chart instances by container id.
- Uses uPlot cursor sync key `quasar-analytics` so related charts share cursor movement.
- Suspends periodic refresh while the user is dragging a time selection.
- Uses `ResizeObserver` and `requestAnimationFrame` to resize charts without layout feedback loops.
- Converts server chart payloads into uPlot columnar data arrays.

## Dependencies
- `window.uPlot` from [`Quasar/wwwroot/lib/uplot/uPlot.iife.min.js`](lib/uplot/uPlot.iife.min.js.md)
- [`Quasar/wwwroot/lib/uplot/uPlot.min.css`](lib/uplot/uPlot.min.css.md)
- Called from Blazor analytics components via JavaScript interop
- Same-origin analytics chart HTTP endpoint supplied in the `sync()` request descriptor
