# Public Access Auth, RBAC, Steam Login, and Workshop Plan

## Goal

Add login support for public Quasar access while preserving the current low-friction local operator workflow.

Localhost and trusted same-subnet access can bypass login by default. Requests from any other remote IP must authenticate. Steam should be the default login provider and default RBAC subject source. Generic external OIDC should be supported as an optional replacement provider.

Role mappings must be easy to seed from `appsettings.json`, especially by SteamID. Fine-grain role and claim mapping can then be managed through a runtime security control panel.

## Current State

Quasar is a Blazor Server app targeting `net10.0`. It currently has no user authentication pipeline. `Program.cs` wires Razor components, MudBlazor, service singletons, health/discovery endpoints, one launcher-token-protected internal drain endpoint, and the raw `/ws/agent` WebSocket handler.

Most Quasar runtime settings are file-backed JSON catalogs with atomic writes and filesystem watchers. Auth and RBAC should follow that shape. Quasar should not add local password users, local passkey registration, local TOTP, or a full ASP.NET Core Identity user database as the default auth model.

## Proposed Architecture

Use external-provider authentication with cookie sessions:

- default provider: Steam
- optional provider: generic OIDC
- local/trusted-network bypass: synthetic Quasar principal
- RBAC: Quasar-owned roles, policies, and mappings
- runtime config: file-backed JSON catalog

Steam is the default because it aligns with Space Engineers operators and enables seamless Steam Workshop integration in the mod selection UI.

Quasar owns:

- trusted network bypass rules
- role definitions
- policy definitions
- appsettings-seeded role mappings
- runtime RBAC mapping catalog
- Steam Workshop query limits/cache/filtering

External providers own:

- account authentication
- MFA/passkey/security challenges
- identity proofing

Quasar should not store local passwords, local passkeys, local TOTP secrets, or recovery codes. If passkey/MFA support is needed, the selected external provider must supply it. For Steam, that means Steam account security/Steam Guard. For generic OIDC, that means the upstream identity provider's MFA/passkey policy.

## Config Shape

Initial `appsettings.json` shape:

```json
{
  "Quasar": {
    "Auth": {
      "Enabled": true,
      "RequireHttpsForPublicAccess": true,
      "DefaultProvider": "Steam",
      "TrustedNetworkBypass": {
        "AllowLoopback": true,
        "AllowSameSubnet": true,
        "TrustedProxies": [],
        "Roles": [ "admin" ]
      },
      "Steam": {
        "Enabled": true,
        "AllowedSteamIds": [],
        "RoleMappings": {
          "admin": [ "76561198000000000" ],
          "editor": [],
          "viewer": []
        }
      },
      "ExternalProviders": {
        "Oidc": {
          "Enabled": false,
          "Authority": "",
          "ClientId": "",
          "ClientSecret": "",
          "Scopes": [ "openid", "profile", "email" ],
          "NameClaim": "name",
          "SubjectClaim": "sub",
          "EmailClaim": "email",
          "RoleClaim": "roles",
          "RoleMappings": {
            "admin": [],
            "editor": [],
            "viewer": []
          }
        }
      },
      "Workshop": {
        "Enabled": true,
        "AppId": 244850,
        "WebApiKey": "",
        "PopularLimit": 50,
        "SearchLimit": 50,
        "RequiredTags": [ "Mod" ],
        "MatchingFileType": "Items",
        "PopularQueryType": "RankedByTotalUniqueSubscriptions",
        "SearchQueryType": "RankedByTextSearch",
        "CacheMaxAgeSeconds": 300,
        "SearchDebounceMilliseconds": 350
      }
    }
  }
}
```

Steam provider protocol constants can stay in code because operators should not need to configure them:

- Steam OpenID endpoint: `https://steamcommunity.com/openid/`
- SteamID claim type: `steamid`
- external login provider name: `Steam`

Steam RBAC subject values should be configurable because initial setup needs to map known SteamIDs to Quasar roles.

Runtime RBAC config shape:

```json
{
  "subjectRoleMappings": [
    {
      "provider": "Steam",
      "subject": "76561198000000000",
      "roles": [ "admin" ]
    },
    {
      "provider": "Oidc",
      "subject": "user-or-group-subject",
      "roles": [ "editor" ]
    }
  ],
  "claimRoleMappings": [
    {
      "provider": "Oidc",
      "claim": "groups",
      "value": "quasar-admins",
      "roles": [ "admin" ]
    }
  ],
  "policyOverrides": {
    "CanControlServers": [ "admin", "editor" ],
    "CanManageSecurity": [ "admin" ]
  }
}
```

## Roles and Policies

System roles:

- `viewer`: read-only access to dashboards, metrics, players, configs, instances, plugins, analytics, and node status.
- `editor`: all viewer rights, plus edit configs, templates, plugins, instances, Discord settings, and normal operational changes.
- `admin`: full control, including user/RBAC mapping, trusted network bypass, external provider config, shutdown/drain controls, and security policy.

Authorization should use policies instead of direct role checks in components:

- `CanView`
- `CanEditConfigs`
- `CanEditInstances`
- `CanControlServers`
- `CanManageDiscord`
- `CanManageAppearance`
- `CanManageSecurity`
- `CanShutdownQuasar`

Default policy mapping:

| Policy | Roles |
| --- | --- |
| `CanView` | `viewer`, `editor`, `admin` |
| `CanEditConfigs` | `editor`, `admin` |
| `CanEditInstances` | `editor`, `admin` |
| `CanControlServers` | `editor`, `admin` |
| `CanManageDiscord` | `editor`, `admin` |
| `CanManageAppearance` | `editor`, `admin` |
| `CanManageSecurity` | `admin` |
| `CanShutdownQuasar` | `admin` |

## Trusted Network Bypass

Add an `ITrustedNetworkEvaluator` service.

Rules:

- loopback bypass allowed when `AllowLoopback=true`
- same-subnet bypass allowed when `AllowSameSubnet=true`
- all other IPs require login
- forwarded headers are ignored unless the proxy is explicitly listed in `TrustedProxies`
- public access with trusted-network bypass enabled must show a visible warning in the security control panel

Same-subnet detection should compare `HttpContext.Connection.RemoteIpAddress` against local NIC unicast addresses and subnet masks. IPv4 and IPv6 should both be considered, but IPv4 can ship first if implementation scope needs trimming.

The bypass should create a synthetic principal with auth type `QuasarTrustedNetwork`. The role set should come from `TrustedNetworkBypass:Roles`, defaulting to `admin` for early rollout.

## Endpoint Protection

Review every endpoint before enabling global auth.

Likely endpoint treatment:

| Endpoint | Auth behavior |
| --- | --- |
| `/` and Blazor routes | trusted bypass or authenticated external user |
| `/login`, `/logout`, auth callback paths | anonymous |
| `/api/health` | anonymous but safe fields only, or trusted/authenticated for full fields |
| `/api/discovery` | trusted/authenticated unless needed for LAN discovery |
| `/api/internal/drain` | launcher token plus trusted network |
| `/ws/agent` | separate agent authentication path, not user login |
| `/branding/*`, static assets | anonymous |

Do not rely only on UI hiding. Mutating API/service actions need policy checks too.

## Steam Login

Steam is the default login provider.

Steam browser login uses OpenID 2.0, not standard OpenID Connect. Quasar should use a dedicated Steam auth handler that verifies Steam OpenID responses and extracts the 64-bit SteamID from the claimed identifier.

Steam login goals:

- allow `Sign in with Steam`
- normalize the authenticated user into a Quasar principal
- expose normalized claims such as `provider=Steam`, `steamid`, and `steam_profile_url`
- map SteamID directly into roles through appsettings seed mappings
- map SteamID directly into roles through runtime RBAC mappings
- optionally restrict access to known SteamIDs through `AllowedSteamIds`
- avoid local account creation unless a lightweight remembered-profile cache is needed for display names/avatars

Initial RBAC by SteamID should be simple:

```json
{
  "Quasar": {
    "Auth": {
      "Steam": {
        "RoleMappings": {
          "admin": [ "76561198000000000" ],
          "editor": [ "76561198000000001" ],
          "viewer": [ "76561198000000002" ]
        }
      }
    }
  }
}
```

If no admin mapping exists and auth is enabled for public access, Quasar should show a startup warning and require trusted-network setup before public login can manage security.

## Optional External OIDC

Generic OIDC is the optional replacement path when an operator does not want Steam as the login provider.

Implementation pieces:

- configure `AddOpenIdConnect` when `ExternalProviders:Oidc:Enabled=true`
- allow `DefaultProvider=Oidc`
- map subject/name/email/role claims from configured claim names
- run `IClaimsTransformation` after login
- transform configured external claims into Quasar roles
- preserve original external claims for audit/debug views

Claim mappings must support:

- exact subject match
- exact claim/value match
- multiple roles per match
- multiple rules per provider
- runtime updates through RBAC config
- appsettings seed mappings for initial setup

## Steam Workshop Integration

Steam Workshop integration should support the mod selection screen without flooding Steam.

Steam Workshop goals:

- make the mod selection screen integrate cleanly with Steam Workshop
- query Space Engineers Workshop by default with `AppId=244850`
- keep all Steam Web API keys server-side
- cache Steam responses server-side
- avoid client-side direct Steam API calls
- cap the popular list at 50 items
- cap search results at 50 items
- debounce search input before calling Steam
- return cached popular results before making fresh calls when cache is valid
- fetch details only for items currently shown

Workshop filtering:

- popular list should query the first 50 most popular mods
- search should query the first 50 matching mods
- search should always pass `search_text`
- Steam query should use `filetype=Items` when querying published files
- Steam query should use `requiredtags=Mod` or the closest Space Engineers Workshop mod tag when supported
- if Steam cannot filter by exact mod type for a query, Quasar should post-filter returned details by tags/type before display
- collections, guides, screenshots, videos, and non-mod Workshop items should not appear in the mod picker

Workshop query defaults:

| Purpose | Steam query shape |
| --- | --- |
| Popular mods | `QueryFiles`, `query_type=RankedByTotalUniqueSubscriptions`, `numperpage=50`, `appid=244850`, `creator_appid=244850`, `filetype=Items`, `requiredtags=Mod` when valid |
| Search mods | `QueryFiles`, `query_type=RankedByTextSearch`, `search_text=<term>`, `numperpage=50`, `appid=244850`, `creator_appid=244850`, `filetype=Items`, `requiredtags=Mod` when valid |
| Item details | `GetPublishedFileDetails` only for IDs shown or selected |

Rate-limit posture:

- one cached popular request per cache window
- one active search request per user query after debounce
- no infinite scroll in the first version
- no prefetch beyond the first 50 results
- no per-keystroke Steam calls
- exponential backoff after Steam errors or throttling
- show stale cached results when Steam is unavailable and cache exists

Steam references:

- Steam browser login uses OpenID 2.0 and returns a claimed SteamID: https://partner.steamgames.com/doc/features/auth?l=english
- Steam Workshop `QueryFiles` supports `numperpage`, `search_text`, `filetype`, tags, and query types: https://partner.steamgames.com/doc/webapi/ipublishedfileservice?l=english

## Passkeys and MFA

Quasar should not implement local passkeys or local MFA in the default model.

MFA/passkey handling belongs to the selected external provider:

- Steam login relies on Steam account security and Steam Guard.
- OIDC login relies on the upstream identity provider's MFA/passkey policy.
- Quasar can display provider and auth-time metadata when available.
- Quasar can require that `DefaultProvider=Oidc` uses an IdP policy that enforces MFA, but Quasar should not store local MFA secrets.

## Security Control Panel

Add `/settings/security`, guarded by `CanManageSecurity`.

Tabs:

- Steam users
- Roles
- Claim mappings
- Provider selection
- Steam/Workshop
- Trusted networks
- Audit

Expected operations:

- list remembered authenticated Steam/OIDC subjects
- assign/remove system roles
- edit SteamID-to-role mappings
- edit OIDC subject/claim-to-role mappings
- configure default provider selection
- configure OIDC fields when OIDC is enabled
- configure Workshop API key and limits
- configure trusted network bypass
- view effective permissions for a subject

Safety constraints:

- cannot remove the last admin mapping
- cannot disable auth while public access is enabled unless explicit dangerous confirmation is used
- cannot switch to OIDC without a valid admin mapping path
- cannot save invalid OIDC config without validation warning
- secrets should not be rendered back in clear text after save

## Stages

### Stage 1: Auth Options and RBAC Model

- Add `QuasarAuthOptions`, `SteamAuthOptions`, `OidcAuthOptions`, `TrustedNetworkBypassOptions`, and `WorkshopOptions`.
- Add role constants, policy constants, and provider constants.
- Add Steam protocol constants in code.
- Add config validation with useful startup logs.
- Add mapping parser for appsettings role mappings.
- Add unit tests for mapping normalization and missing-admin warnings.

### Stage 2: Authentication Pipeline

- Register cookie authentication and authorization.
- Register Steam as the default challenge provider.
- Register OIDC only when enabled.
- Add `UseAuthentication()` and `UseAuthorization()`.
- Convert router to `AuthorizeRouteView`.
- Add login/logout/callback pages or endpoints.
- Verify anonymous public access redirects to Steam login by default.

### Stage 3: Trusted Network Bypass

- Add `ITrustedNetworkEvaluator`.
- Add trusted-network auth middleware or authentication handler.
- Add tests for loopback, same-subnet, remote public IP, IPv6, and trusted proxy behavior.
- Add visible app warning when same-subnet bypass is enabled.
- Ensure bypass does not apply to proxy-forwarded addresses unless proxy is trusted.

### Stage 4: System Roles and Policies

- Define system roles and default policy mappings.
- Apply SteamID role mappings from `appsettings.json`.
- Apply optional OIDC role mappings from `appsettings.json`.
- Add policy checks to pages and mutating operations.
- Add tests for viewer/editor/admin access boundaries.

### Stage 5: Runtime RBAC Catalog

- Add `RbacConfigCatalog` using Quasar's existing JSON catalog pattern.
- Store provider/subject role mappings, claim mappings, policy overrides, and history snapshots.
- Add last-admin protection.
- Add effective-permission evaluation service.
- Make runtime RBAC changes hot-reload via filesystem watcher.

### Stage 6: Optional External OIDC

- Add generic OIDC registration.
- Add configurable subject and claim mapping.
- Add external-login callback handling.
- Add `IClaimsTransformation` for Quasar roles.
- Add UI and logs for unmapped/unknown external users.
- Add tests using fake OIDC claims.

### Stage 7: Steam Workshop Mod Browser

- Add server-side `SteamWorkshopService`.
- Query popular mods with a hard `numperpage=50` cap.
- Query search results with a hard `numperpage=50` cap.
- Use `filetype=Items` and `requiredtags=Mod` when Steam accepts the filter.
- Post-filter non-mod results when Steam filtering is incomplete.
- Cache popular and search responses.
- Add search debounce and request cancellation.
- Update mod selection UI to show popular mods before search.
- Keep Steam API keys out of browser payloads and logs.
- Add tests for limit enforcement, tag filtering, cache behavior, and failed Steam calls.

### Stage 8: Security Control Panel

- Add `/settings/security`.
- Add nav entry visible only to admins.
- Implement users/roles/claims/provider/workshop/trusted-network/audit tabs.
- Add validation and confirmation dialogs for risky changes.
- Avoid nested-card UI; match current MudBlazor page style.

### Stage 9: Audit and Hardening

- Add security event logging:
  - login success/failure
  - trusted-network bypass login
  - role changes
  - claim mapping changes
  - provider config changes
  - Workshop API/config changes
- Add rate limiting for login callbacks and auth endpoints.
- Add antiforgery review for auth endpoints.
- Add secure cookie settings.
- Add optional session timeout and persistent-login controls.

### Stage 10: Public Access Validation

- Test direct LAN access.
- Test public remote access.
- Test reverse proxy access with trusted proxy config.
- Test Steam login and SteamID role mapping.
- Test OIDC login and role mapping.
- Test viewer/editor/admin behavior across all pages.
- Test Workshop popular/search limits and filtering.
- Verify existing agent WebSocket and launcher drain behavior remain intact.

## Initial Execution Order

Recommended first execution slice:

1. Add auth option models and RBAC mapping models.
2. Add cookie auth with Steam as default provider.
3. Add login/logout/callback endpoints.
4. Add global auth enforcement with loopback bypass only.
5. Add same-subnet bypass after tests prove IP classification.
6. Add SteamID appsettings role mappings.
7. Add system roles/policies.
8. Add RBAC runtime catalog and admin panel.
9. Add optional external OIDC.
10. Add Steam Workshop mod browser.

This sequence gets Steam-based public login and SteamID RBAC working first, then layers runtime control, optional OIDC replacement, and Workshop integration.
