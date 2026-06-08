# Quasar/wwwroot/quasar-configs.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Small JavaScript interop module registered as `window.quasarConfigs`. Provides utility functions called from Blazor components via `IJSRuntime.InvokeAsync`. The object is assigned with `window.quasarConfigs = window.quasarConfigs || { ... }` so re-evaluation is safe (it keeps any existing instance).

## Structure
`window.quasarConfigs` object with ten methods:

| Method | Signature | Description |
|---|---|---|
| `getSystemDarkMode()` | `() → bool` | Returns `true` if the OS/browser prefers a dark colour scheme via `window.matchMedia('(prefers-color-scheme: dark)')` |
| `getViewportWidth()` | `() → number` | Returns the viewport width (`innerWidth` / `clientWidth`), floored, with a 320 px minimum and 1280 fallback — used for chart sizing/density heuristics |
| `focusElement(id)` | `(string) → void` | Scrolls the element into view (smooth, center), briefly adds the `config-option-focus` CSS class (1800 ms), then focuses it with `preventScroll` |
| `scrollToBottom(id)` | `(string) → void` | Sets `scrollTop = scrollHeight` on the element, scrolling it to the bottom |
| `scrollToRatio(id, ratio)` | `(string, number) → void` | Sets `scrollTop` to a clamped ratio of the element's scrollable height; used after server-log window rollover |
| `isScrolledNearBottom(id, threshold)` | `(string, number?) → bool` | Returns `true` if the element is within `threshold` px (default 32) of its bottom; returns `true` if the element is not found |
| `getScrollEdgeState(id, threshold)` | `(string, number?) → object` | Returns `{ nearTop, nearBottom }` booleans for scroll containers; retained for simple edge checks |
| `attachRolloverLog(id, dotNetRef, options)` | `(string, DotNetObjectReference, object) → void` | Attaches browser-side scroll, click, and `Ctrl`/`Alt` + `PageUp`/`PageDown`/`Home`/`End` listeners for the server-log viewer; calls .NET only when a 250-line window move or start/end jump is needed |
| `detachRolloverLog(id)` | `(string) → void` | Removes listeners installed by `attachRolloverLog` |
| `reloadWhenHealthy(targetUrl, options)` | `(string, object?) → void` | Used during a Quasar worker restart (the Blazor circuit drops): after an initial delay, polls the anonymous `/api/health` endpoint at `pollIntervalMs` (default 1 s) and navigates to `targetUrl` once it responds `ok`; falls back to a plain reload after `maxWaitMs` (default 120 s) |

## Dependencies
- Called by Blazor components via `IJSRuntime` (specific callers not determinable from this file alone)
- The anonymous `/api/health` endpoint polled by `reloadWhenHealthy`
- [`Quasar/wwwroot/app.css`](app.css.md) (no longer defines `.config-option-focus`; that class is applied here but styled elsewhere/scoped)
