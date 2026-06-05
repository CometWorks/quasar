# Quasar/Components/Layout/ReconnectModal.razor.js

**Module:** Quasar.Components  **Kind:** JS  **Tier:** 3

## Summary
ES module that drives the reconnect modal dialog. Listens to the `components-reconnect-state-changed` custom event fired by the Blazor runtime and maps state transitions to native `<dialog>` show/close calls and `Blazor.reconnect()` / `Blazor.resumeCircuit()` API calls.

## Structure
**Initialization (top-level, runs once on module load):**
- Grabs `#components-reconnect-modal`, `#components-reconnect-button`, `#components-resume-button`.
- Attaches `handleReconnectStateChanged` to the modal's `components-reconnect-state-changed` event.
- Attaches `retry` to the Retry button click.
- Attaches `resume` to the Resume button click.

**`handleReconnectStateChanged(event)`:**
- `"show"` → `reconnectModal.showModal()` (native dialog API).
- `"hide"` → `reconnectModal.close()`.
- `"failed"` → registers `retryWhenDocumentBecomesVisible` on `document.visibilitychange` for auto-retry on tab refocus.
- `"rejected"` → `location.reload()` (circuit ID unknown, full reload needed).

**`retry()`** (async):
- Removes the visibility-change auto-retry listener.
- Calls `Blazor.reconnect()`.
  - Returns `true` → success, modal closes.
  - Returns `false` → circuit lost; tries `Blazor.resumeCircuit()`, reloads on failure.
  - Throws → server unreachable, re-registers visibility-change auto-retry.

**`resume()`** (async):
- Calls `Blazor.resumeCircuit()`.
- On failure: replaces the `paused` CSS class with `resume-failed` on the modal.
- On exception: reloads the page.

**`retryWhenDocumentBecomesVisible()`** (async): calls `retry()` when `document.visibilityState === "visible"`.

## Dependencies
- [`Quasar/Components/Layout/ReconnectModal.razor`](ReconnectModal.razor.md) (provides the DOM elements)
- `Blazor` global (the Blazor WebAssembly/Server JS runtime — `blazor.web.js`)

## Notes
Loaded as `type="module"` so it is deferred and has its own scope. No Blazor DotNet object reference is needed; all orchestration is done via the `Blazor.*` JS API and the native `<dialog>` API.
