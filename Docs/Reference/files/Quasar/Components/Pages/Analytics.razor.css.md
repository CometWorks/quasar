# Quasar/Components/Pages/Analytics.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped stylesheet for `Analytics.razor`. Defines the responsive CSS grid layout for the analytics chart area and ensures each ApexCharts card fills its cell fully.

## Structure
- `.analytics-chip-row` — `flex-wrap: wrap` so summary chips reflow on narrow viewports.
- `.analytics-chart-grid` — CSS grid driven by three custom properties injected from code: `--analytics-grid-columns`, `--analytics-grid-rows`, `--analytics-row-height`. Uses `grid-template-columns: repeat(var(--analytics-grid-columns), minmax(0, 1fr))` and `grid-template-rows: repeat(...)` so server-side column/row counts control layout.
- `.analytics-chart-card` — `min-width: 0` prevents grid blowout on long chart labels.
- `.analytics-chart-paper` — `height: 100%; display: flex; flex-direction: column` so the `MudPaper` card stretches to fill its cell.
- `.analytics-apex-chart` — flexible chart host with an explicit minimum height that fills the remaining card space and prevents width/height blowout.
- `.analytics-apex-chart :global(.apexcharts-canvas)` / `.analytics-apex-chart :global(.apexcharts-svg)` — cap rendered ApexCharts surfaces at the available card width.
- `.analytics-apex-chart :global(.apexcharts-menu)` / `.apexcharts-menu-item` — styles the ApexCharts toolbar export menu as white text on a black background with a darker hover/focus state.
- `@media (max-width: 1279.98px)` — collapses to a single column and forces `grid-column: span 1` on all cards, overriding inline span styles.

## Dependencies
- [`Quasar/Components/Pages/Analytics.razor`](Analytics.razor.md) (scoped to this component)
