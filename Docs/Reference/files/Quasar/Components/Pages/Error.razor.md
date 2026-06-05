# Quasar/Components/Pages/Error.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Standard ASP.NET Core error page rendered at `/Error`. Displays a generic error message and, when a request or activity ID is available, shows it for diagnostics. Advises enabling the Development environment for detailed exceptions and warns against doing so in production.

## Structure
- **Route:** `@page "/Error"`
- **Cascading parameter:** `HttpContext?` — used to retrieve the trace identifier if `Activity.Current?.Id` is null.
- **Properties:**
  - `RequestId` (string?) — populated from `Activity.Current?.Id` or `HttpContext?.TraceIdentifier`.
  - `ShowRequestId` (bool) — true when `RequestId` is non-empty.
- **UI:** Static HTML headings with Bootstrap `text-danger` classes; conditional `<p>` with the request ID; explanatory paragraphs about the Development environment.
- No injected services, no dialogs, no MudBlazor components.

## Dependencies
- `System.Diagnostics` (`Activity`)
- ASP.NET Core (`HttpContext`)
