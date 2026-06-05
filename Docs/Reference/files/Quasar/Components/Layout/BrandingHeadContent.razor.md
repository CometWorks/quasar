# Quasar/Components/Layout/BrandingHeadContent.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
A lightweight head-content component that reactively updates the page favicon whenever `BrandingService` fires its `Changed` event. Rendered inside `MainLayout` so the favicon tracks live branding changes without a full page reload.

## Structure
No `@page` route — used as a child inside `MainLayout`.

**Injected services:**
- `BrandingService BrandingService`

**Implements:** `IDisposable`

**Markup:** `<HeadContent>` with a single `<link rel="icon">` whose `href` is `BrandingService.Settings.FaviconPath ?? "/Quasar.ico"`.

**Lifecycle:**
- `OnInitialized` — subscribes to `BrandingService.Changed`.
- `Dispose` — unsubscribes.
- `OnChanged` — calls `InvokeAsync(StateHasChanged)` to re-render the favicon link on the current circuit.

## Dependencies
- [`Quasar/Services/BrandingService.cs`](../../Services/BrandingService.cs.md)

## Notes
The static fallback favicon `/Quasar.png` in `App.razor` covers the pre-interactive render period before this component becomes active.
