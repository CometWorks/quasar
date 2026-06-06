# Quasar/Components/Pages/Analytics.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped stylesheet for `Analytics.razor`. Defines the responsive CSS-grid layout for the analytics chart panels and constrains the embedded ApexCharts canvases.

## Structure
- `.analytics-chip-row` — wraps the summary chip row.
- `.analytics-chart-grid` — CSS grid driven by inline custom properties the page sets (`--analytics-grid-columns`, `--analytics-grid-rows`, `--analytics-row-height`); `gap: 16px`, stretch alignment.
- `.analytics-chart-card` / `.analytics-chart-paper` — flex-column card wrappers (`height: 100%`).
- `.analytics-apex-chart` — flexible chart region (`min-height: 280px`); `:global(.apexcharts-canvas)` / `:global(.apexcharts-svg)` capped to `max-width: 100%`.
- Media query `max-width: 1279.98px` collapses the grid to a single column and forces each card to span one column.

## Dependencies
- [`Quasar/Components/Pages/Analytics.razor`](Analytics.razor.md) (consumes these classes and supplies the grid CSS variables).
