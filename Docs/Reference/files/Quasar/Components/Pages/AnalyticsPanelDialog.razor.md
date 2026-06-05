# Quasar/Components/Pages/AnalyticsPanelDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Modal dialog opened from `Analytics.razor` to edit the display settings of a single analytics chart panel. Returns an `AnalyticsPanelDialogResult` containing the updated visibility, order, column span, and row span when the user clicks Save.

## Structure
- **No route** (dialog component only)
- **Cascading parameter:** `IMudDialogInstance MudDialog`
- **Parameters:**
  - `Title` (string) — metric title shown as the dialog title.
  - `Subtitle` (string) — secondary description shown inside the form.
  - `Visible` (bool) — initial visibility state.
  - `Order` (int) — panel sort order (0–99).
  - `ColumnSpan` (int) — column span within the grid (clamped to `GridColumns`).
  - `RowSpan` (int) — row span within the grid (clamped to `GridRows`).
  - `GridColumns` (int) — current grid column count, used as the upper bound.
  - `GridRows` (int) — current grid row count, used as the upper bound.
- **UI:** `MudDialog` with `MudCheckBox` (Visible), two `MudNumericField`s (Order, ColumnSpan, RowSpan), Cancel and Save action buttons.
- **Result type:** `AnalyticsPanelDialogResult` — a plain class with `Visible`, `Order`, `ColumnSpan`, `RowSpan` fields; set from parameters on init and passed back via `MudDialog.Close(DialogResult.Ok(...))`.
- `Save()` re-clamps span values before closing to guard against out-of-range edits.
- `Cancel()` calls `MudDialog.Cancel()`.

## Dependencies
- `Quasar/Services/Analytics/AnalyticsPanelDialogResult.cs` (or equivalent model)
- [`Quasar/Components/Pages/Analytics.razor`](Analytics.razor.md) (caller)
- MudBlazor (`MudDialog`, `MudCheckBox`, `MudNumericField`, `MudButton`)
