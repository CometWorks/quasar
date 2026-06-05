# Quasar/wwwroot/quasar-configs.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Small JavaScript interop module registered as `window.quasarConfigs`. Provides utility functions called from Blazor components via `IJSRuntime.InvokeAsync`. The object is initialised lazily with `||=` to allow safe re-evaluation.

## Structure
`window.quasarConfigs` object with five methods:

| Method | Signature | Description |
|---|---|---|
| `getSystemDarkMode()` | `() → bool` | Returns `true` if the OS/browser prefers dark colour scheme via `window.matchMedia('(prefers-color-scheme: dark)')` |
| `getViewportWidth()` | `() → number` | Returns `window.innerWidth` (minimum 320 px fallback) for chart sizing and chart density heuristics |
| `focusElement(id)` | `(string) → void` | Scrolls the element with the given `id` into view (smooth, center), briefly adds `config-option-focus` CSS class (1800 ms), and focuses it |
| `scrollToBottom(id)` | `(string) → void` | Sets `scrollTop = scrollHeight` on the element, scrolling it to the bottom |
| `isScrolledNearBottom(id, threshold)` | `(string, number?) → bool` | Returns `true` if the element is within `threshold` pixels (default 32) of its bottom; returns `true` if element not found |
| `reloadWhenHealthy(targetUrl, options)` | `(string, object?) → void` | Polls `/api/health` after a startup delay and navigates to `targetUrl` once the endpoint responds `ok` |

## Dependencies
- Called by Blazor components via `IJSRuntime` (specific callers not determinable from this file alone)
- `app.css` (defines the `.config-option-focus` CSS class used by `focusElement`)
