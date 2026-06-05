# Quasar/Components/Pages/NotFound.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Minimal fallback page at `/not-found` rendered inside `MainLayout`. Displays a static "Not Found" heading and a short explanatory message; no logic or services.

## Structure
- **`@page "/not-found"`**
- **`@layout MainLayout`**
- Static HTML: `<h3>Not Found</h3>` and a `<p>` description.
- No injected services, parameters, or code block.

## Dependencies
- [`Quasar/Components/Layout/MainLayout.razor`](../Layout/MainLayout.razor.md) — layout host.
