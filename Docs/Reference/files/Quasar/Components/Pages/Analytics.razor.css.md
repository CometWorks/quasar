# Quasar/Components/Pages/Analytics.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped stylesheet for `Analytics.razor`. Defines the responsive CSS grid layout for the analytics chart area and ensures each chart card fills its cell fully.

## Structure
- `.analytics-chip-row` — `flex-wrap: wrap` so summary chips reflow on narrow viewports.
- `.analytics-chart-grid` — CSS grid driven by three custom properties injected from code: `--analytics-grid-columns`, `--analytics-grid-rows`, `--analytics-row-height`. Uses `grid-template-columns: repeat(var(--analytics-grid-columns), minmax(0, 1fr))` and `grid-template-rows: repeat(...)` so server-side column/row counts control layout.
- `.analytics-chart-card` — `min-width: 0` prevents grid blowout on long chart labels.
- `.analytics-chart-paper` — `height: 100%; display: flex; flex-direction: column` so the `MudPaper` card stretches to fill its cell.
- `.analytics-chart-paper :global(.mud-chart)` — `flex: 1 1 auto; min-height: 0` lets the MudBlazor chart fill remaining vertical space.
- `@media (max-width: 1279.98px)` — collapses to a single column and forces `grid-column: span 1` on all cards, overriding inline span styles.

## Dependencies
- [`Quasar/Components/Pages/Analytics.razor`](Analytics.razor.md) (scoped to this component)
