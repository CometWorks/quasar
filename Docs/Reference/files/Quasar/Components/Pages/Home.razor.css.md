# Quasar/Components/Pages/Home.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped stylesheet for `Home.razor`. Provides layout primitives for the setup wizard step container, individual step cards, text/action column sizing, and the instance-status list rows within the wizard.

## Structure
- `.setup-wizard-steps` — flex column with no gap; acts as the step container.
- `.setup-wizard-step` — `padding: 0.9rem 0` with a top border line (`--mud-palette-lines-default`) between steps; first child removes the top border and padding-top; last child removes bottom padding.
- `.setup-wizard-copy` — `flex: 1 1 28rem; min-width: 16rem`; the descriptive text column in each step row, allows wrapping on narrow screens.
- `.setup-wizard-actions` — `justify-content: flex-end`; action button group aligned to the right.
- `.setup-wizard-status-list` — flex column with 0.5 rem gap; list of instance-status rows within steps 3 and 4.
- `.setup-wizard-status-row` — `display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.75rem`; one row per instance showing name/summary on the left and status chips on the right.

## Dependencies
- [`Quasar/Components/Pages/Home.razor`](Home.razor.md) (scoped to this component)
