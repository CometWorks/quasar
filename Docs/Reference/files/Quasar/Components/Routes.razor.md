# Quasar/Components/Routes.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Router root component. Wraps the Blazor `<Router>` in `<CascadingAuthenticationState>` so that all pages receive the authentication context. Handles not-found, authorizing, and not-authorized states with inline MudBlazor feedback.

## Structure
- No `@page` route — rendered from `App.razor`.
- `<CascadingAuthenticationState>` — propagates `AuthenticationState` to all descendants.
- `<Router AppAssembly="typeof(Program).Assembly" NotFoundPage="typeof(Pages.NotFound)">` — discovers routable pages.
- `<Found>` branch: `<AuthorizeRouteView>` with `DefaultLayout="typeof(Layout.MainLayout)"`.
  - `<Authorizing>`: shows `<MudProgressCircular>` inside `MainLayout`.
  - `<NotAuthorized>`: shows `<MudAlert Severity.Warning>` + a `/login` `<MudButton>`.
- `<FocusOnNavigate Selector="h1">` on successful navigation.
- No `@code` block.

## Dependencies
- [`Quasar/Components/Layout/MainLayout.razor`](Layout/MainLayout.razor.md)
- `Quasar/Pages/NotFound.razor` (referenced by type)
- MudBlazor (`MudProgressCircular`, `MudAlert`, `MudButton`)
- Microsoft.AspNetCore.Components.Authorization
