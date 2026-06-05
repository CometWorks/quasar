# Quasar/Components/App.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
The root HTML document component for the Blazor Server application. It renders the full `<html>` skeleton, wires MudBlazor, ApexCharts and app CSS, loads the Blazor WebAssembly/server JS runtime, and hosts `<Routes>` and `<ReconnectModal>` as the two top-level interactive components.

## Structure
- No `@page` route — this is the document root, registered in `Program.cs` as the root component.
- `<head>`: `<ResourcePreloader />`, Google Fonts (Roboto), static favicon fallback (`/Quasar.png`), MudBlazor CSS, ApexCharts CSS, `app.css`, `Quasar.styles.css`, `<ImportMap />`, `<HeadOutlet @rendermode="InteractiveServer" />`.
- `<body>`: `<Routes @rendermode="InteractiveServer" />`, `<ReconnectModal />`, three scripts: `quasar-configs.js`, `_framework/blazor.web.js`, `MudBlazor.min.js`.
- Uses `@Assets[...]` for fingerprinted asset URLs.
- No `@code` block; purely markup.

## Dependencies
- [`Quasar/Components/Routes.razor`](Routes.razor.md)
- [`Quasar/Components/Layout/ReconnectModal.razor`](Layout/ReconnectModal.razor.md)
- MudBlazor (CSS + JS via CDN-style bundles)
- Blazor-ApexCharts (component CSS bundle)
