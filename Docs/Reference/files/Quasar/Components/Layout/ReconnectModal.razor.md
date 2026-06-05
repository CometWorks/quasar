# Quasar/Components/Layout/ReconnectModal.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Custom Blazor Server reconnect modal rendered as an HTML `<dialog>` element. Replaces the default Blazor reconnect UI with a branded animated dialog that handles the first-attempt, retry, failure, pause, and resume-failed states. Loads its companion JS module via a `<script type="module">`.

## Structure
No `@page` route — rendered directly in `App.razor`'s `<body>`.

No `@code` block; purely markup.

**Markup elements:**
- `<script type="module" src="@Assets["Components/Layout/ReconnectModal.razor.js"]">` — loads the JS module that drives state transitions.
- `<dialog id="components-reconnect-modal" data-nosnippet>` — native dialog; shown/closed by JS via `showModal()` / `close()`.
- `.components-reconnect-container` — flex column.
- `.components-rejoining-animation` — two animated divs (CSS ripple rings).
- Paragraphs for each state (all hidden by default via CSS, shown by class switching in JS):
  - `.components-reconnect-first-attempt-visible` — "Rejoining the server..."
  - `.components-reconnect-repeated-attempt-visible` — countdown message with `#components-seconds-to-next-attempt`.
  - `.components-reconnect-failed-visible` — failure message + Retry button.
  - `.components-pause-visible` — pause message + Resume button.
  - `.components-resume-failed-visible` — resume failure message.

## Dependencies
- [`Quasar/Components/Layout/ReconnectModal.razor.js`](ReconnectModal.razor.js.md)
- [`Quasar/Components/Layout/ReconnectModal.razor.css`](ReconnectModal.razor.css.md)

## Notes
CSS classes on `#components-reconnect-modal` (e.g. `components-reconnect-show`, `components-reconnect-retrying`, `components-reconnect-failed`, `components-reconnect-paused`) are toggled by the Blazor runtime; the CSS and JS respond to those class changes rather than Blazor component state.
