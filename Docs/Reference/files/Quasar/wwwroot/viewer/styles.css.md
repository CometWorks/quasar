# Quasar/wwwroot/viewer/styles.css

**Module:** Quasar.Host  **Kind:** CSS  **Tier:** 3

## Summary
Standalone styling for the grid viewer page. It defines the dark sci-fi layout, sidebar control cards, stats/log presentation, full-window Three.js viewport, initial loading/progress overlay, hover/camera overlays, and mobile stacking behavior.

## Structure
Key sections:
- `:root` theme variables for background, panels, text, accent, danger, border, and font stack.
- `.viewer-shell`, `.panel`, and `.viewport-wrap` define the two-column desktop layout and full-height viewport.
- `.card`, buttons, labels, hints, status text, summaries, and stats style the sidebar controls.
- `.loading-overlay`, `.progress-shell`, `.log-card pre`, `.hover-readout`, and `.camera-hint` style loading, diagnostics, and viewport overlays.
- A `max-width: 860px` media query stacks the sidebar above the viewport on narrow screens.

## Dependencies
- [`Quasar/wwwroot/viewer/index.html`](index.html.md) element/class structure.
