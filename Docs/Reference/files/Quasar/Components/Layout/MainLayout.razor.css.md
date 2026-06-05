# Quasar/Components/Layout/MainLayout.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped CSS for `MainLayout.razor`. Styles the brand logo mark in the app bar and the Blazor framework error banner.

## Structure
- `.quasar-brand` — `min-width: 0` to allow flex shrink of the brand stack.
- `.quasar-brand-mark` — 64×64 px block image, `object-fit: contain`, `flex: 0 0 64px`.
- `#blazor-error-ui` — fixed bottom banner, light-yellow background, `display: none` by default; shown by the Blazor runtime on unhandled circuit errors.
- `#blazor-error-ui .dismiss` — absolute-positioned close button (×).

## Dependencies
None (scoped to `MainLayout.razor` by the Blazor CSS isolation mechanism).
