# Quasar/Components/Layout/NavMenu.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Side-drawer navigation menu. Renders two `MudNavMenu` groups — a "Control Surface" group with the main app routes and a "Settings" group with appearance and (policy-gated) security links.

## Structure
No `@page` route — rendered as a child of `MainLayout`'s `MudDrawer`.

No `@code` block; purely markup.

**Control Surface nav links:**
| Route | Icon | Label |
|---|---|---|
| `/` (exact) | Dashboard | Dashboard |
| `/servers` | Dns | Servers |
| `/configs` | Tune | Configs |
| `/world-templates` | Public | World Templates |
| `/players` | Groups | Players |
| `/entities` | ViewInAr | Entities |
| `/hosts` | Hub | Hosts |
| `/plugins` | Extension | Plugins |
| `/analytics` | QueryStats | Analytics |
| `/discord` | Forum | Discord |

**Settings nav links:**
- `/settings/appearance` — Palette icon — always visible.
- `/settings/security` — Security icon — wrapped in `<AuthorizeView Policy="CanManageSecurity">`, only shown to authorized users.

**MudBlazor components:** `MudStack`, `MudDivider`, `MudText`, `MudNavMenu`, `MudNavLink`, `AuthorizeView`.

## Dependencies
- `Quasar/Services/Auth/QuasarPolicyNames.cs` — policy constant `CanManageSecurity`
- MudBlazor
