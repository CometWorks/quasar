# Quasar/Components/Layout/NavMenu.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Side-drawer navigation menu. Renders two `MudNavMenu` groups separated by a `MudDivider` — a primary group with the visible main app routes and a settings group with the appearance and backup links plus a policy-gated security link.

## Structure
No `@page` route — rendered as a child of `MainLayout`'s `MudDrawer`.

No `@code` block; purely markup (no group title text, just the two nav menus).

**Primary nav links:**
| Route | Icon | Label |
|---|---|---|
| `/` (exact, `NavLinkMatch.All`) | Dashboard | Dashboard |
| `/servers` | Dns | Servers |
| `/configs` | Tune | Configs |
| `/world-templates` | Public | Worlds |
| `/players` | Groups | Players |
| `/entities` | ViewInAr | Entities |
| `/plugins` | Extension | Plugins |
| `/analytics` | QueryStats | Analytics |
| `/discord` | Forum | Discord |

**Settings nav links:**
- `/settings/appearance` — Palette icon — always visible.
- `/backup` — Backup icon — always visible.
- `/settings/security` — Security icon — wrapped in `<AuthorizeView Policy="CanManageSecurity">`, only shown to authorized users.

**MudBlazor components:** `MudStack`, `MudDivider`, `MudNavMenu`, `MudNavLink`, `AuthorizeView`.

## Dependencies
- `Quasar/Auth/QuasarPolicyNames.cs` — policy constant `CanManageSecurity`
- MudBlazor
