# Quasar/Components/_Imports.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Global `@using` directive file for the `Quasar/Components` folder subtree. All namespaces listed here are automatically in scope for every `.razor` file under `Quasar/Components/` and its subdirectories, eliminating per-file repetition.

## Structure
Namespaces imported (in order):
- `System.Net.Http`, `System.Net.Http.Json`
- `Magnetar.Protocol.Model`, `Magnetar.Protocol.Runtime`, `Magnetar.Protocol.Transport`
- `Microsoft.AspNetCore.Components.Forms`, `.Routing`, `.Web`, `.Authorization`
- `static Microsoft.AspNetCore.Components.Web.RenderMode`
- `Microsoft.AspNetCore.Components.Web.Virtualization`
- `Microsoft.JSInterop`
- `Quasar`, `Quasar.Components`, `Quasar.Components.Dashboard`, `Quasar.Components.Layout`, `Quasar.Components.Shared`
- `Quasar.Models`, `Quasar.Services`, `Quasar.Services.Analytics`, `Quasar.Services.Auth`, `Quasar.Services.Discord`, `Quasar.Services.PluginSdk`
- `MudBlazor`

## Dependencies
- Magnetar.Protocol (shared protocol library)
- MudBlazor
- All Quasar service and model namespaces referenced in components
