# Public Access Auth, RBAC, Passkey, and MFA Plan

## Goal

Add login support for public Quasar access while preserving the current low-friction local operator workflow.

Localhost and trusted same-subnet access can bypass login by default. Requests from any other remote IP must authenticate. External OAuth/OIDC providers must be supported, including claim mappings into Quasar RBAC. System roles such as `viewer`, `editor`, and `admin` must be available for initial setup through `appsettings.json`, with fine-grain runtime mapping managed later through a security control panel.

## Current State

Quasar is a Blazor Server app targeting `net10.0`. It currently has no user authentication pipeline. `Program.cs` wires Razor components, MudBlazor, service singletons, health/discovery endpoints, one launcher-token-protected internal drain endpoint, and the raw `/ws/agent` WebSocket handler.

Most Quasar runtime settings are file-backed JSON catalogs with atomic writes and filesystem watchers. Auth should follow that style where practical, but passkeys and MFA are better served by ASP.NET Core Identity's established storage model.

## Proposed Architecture

Use ASP.NET Core Identity as the authentication foundation, backed by a small SQLite database stored under Quasar app data. Keep Quasar authorization rules and runtime RBAC mappings in JSON catalogs.

This gives Quasar:

- local user accounts
- passkey support
- MFA/TOTP and recovery codes
- external OAuth/OIDC login support
- persistent user security state
- runtime Quasar-specific role and claim mapping config

Identity owns user credentials, passkeys, external login bindings, MFA state, and recovery codes. Quasar owns policy definitions, role semantics, local/trusted-network bypass rules, external claim-to-role mappings, and fine-grain permission config.

## Config Shape

Initial `appsettings.json` shape:

```json
{
  "Quasar": {
    "Auth": {
      "Enabled": true,
      "RequireHttpsForPublicAccess": true,
      "TrustedNetworkBypass": {
        "AllowLoopback": true,
        "AllowSameSubnet": true,
        "TrustedProxies": []
      },
      "InitialRoleMappings": {
        "admin": [ "admin@example.com" ],
        "editor": [ "ops@example.com" ],
        "viewer": []
      },
      "Mfa": {
        "RequireForAdmin": true,
        "RequireForEditor": false,
        "RequireForViewer": false
      },
      "ExternalProviders": {
        "Oidc": {
          "Enabled": false,
          "Authority": "",
          "ClientId": "",
          "ClientSecret": "",
          "Scopes": [ "openid", "profile", "email" ],
          "NameClaim": "name",
          "EmailClaim": "email",
          "RoleClaim": "roles"
        }
      }
    }
  }
}
```

Runtime RBAC config shape:

```json
{
  "userRoles": [
    {
      "subject": "admin@example.com",
      "roles": [ "admin" ]
    }
  ],
  "claimRoleMappings": [
    {
      "claim": "groups",
      "value": "quasar-admins",
      "roles": [ "admin" ]
    },
    {
      "claim": "roles",
      "value": "server-editor",
      "roles": [ "editor" ]
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
- `admin`: full control, including user management, RBAC, trusted network bypass, OAuth/OIDC config, shutdown/drain controls, and security policy.

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

The bypass should create a synthetic principal with a clear auth type, for example `QuasarTrustedNetwork`, and assign a configurable local role set. Default local bypass role should be `admin` only during early rollout, then configurable later.

## Endpoint Protection

Review every endpoint before enabling global auth.

Likely endpoint treatment:

| Endpoint | Auth behavior |
| --- | --- |
| `/` and Blazor routes | trusted bypass or authenticated user |
| `/login`, `/logout`, setup pages | anonymous |
| `/api/health` | anonymous but safe fields only, or trusted/authenticated for full fields |
| `/api/discovery` | trusted/authenticated unless needed for LAN discovery |
| `/api/internal/drain` | launcher token plus trusted network |
| `/ws/agent` | separate agent authentication path, not user login |
| `/branding/*`, static assets | anonymous |

Do not rely only on UI hiding. Mutating API/service actions need policy checks too.

## External OAuth/OIDC

Add generic OIDC provider support first. Avoid provider-specific code until needed.

Implementation pieces:

- configure `AddOpenIdConnect` when `ExternalProviders:Oidc:Enabled=true`
- map name/email/role claims from configured claim names
- store external login binding in Identity
- run `IClaimsTransformation` after login
- transform configured external claims into Quasar roles
- preserve original external claims for audit/debug views

Claim mappings must support:

- exact match
- multiple roles per match
- multiple rules per provider
- runtime updates through RBAC config
- appsettings seed mappings for initial setup

## Passkeys and MFA

Use ASP.NET Core Identity passkey support for local accounts.

Support:

- passkey registration
- passkey sign-in
- password plus TOTP MFA
- recovery codes
- optional passkey-first login for local accounts
- admin MFA required by default

Passkeys require secure origin behavior. Public passkey usage should require HTTPS. Localhost development can use the browser's localhost secure-context exception.

## Security Control Panel

Add `/settings/security`, guarded by `CanManageSecurity`.

Tabs:

- Users
- Roles
- Claim mappings
- External provider
- MFA/passkeys
- Trusted networks
- Audit

Expected operations:

- list users and external identities
- assign/remove system roles
- require/reset MFA
- revoke sessions
- manage passkeys
- edit claim-to-role mappings
- configure OIDC fields
- configure trusted network bypass
- view effective permissions for a user

Safety constraints:

- cannot remove the last admin
- cannot disable auth while public access is enabled unless explicit dangerous confirmation is used
- cannot save invalid OIDC config without validation warning
- secrets should not be rendered back in clear text after save

## Stages

### Stage 1: Auth Options and Data Model

- Add `QuasarAuthOptions`, `TrustedNetworkBypassOptions`, `MfaOptions`, and OIDC option models.
- Add `QuasarAuthDbContext`.
- Add `QuasarUser` Identity model if custom fields are needed; otherwise use `IdentityUser`.
- Add auth DB path helper under `MagnetarPaths`.
- Add migrations or startup database initialization.
- Add config validation with useful startup logs.

### Stage 2: Authentication Pipeline

- Register Identity, cookies, authorization, authentication state, and SQLite auth DB.
- Add `UseAuthentication()` and `UseAuthorization()`.
- Convert router to `AuthorizeRouteView`.
- Add login/logout/setup pages.
- Add first-admin setup flow.
- Verify anonymous public access redirects to login.

### Stage 3: Trusted Network Bypass

- Add `ITrustedNetworkEvaluator`.
- Add trusted-network auth middleware or authentication handler.
- Add tests for loopback, same-subnet, remote public IP, IPv6, and trusted proxy behavior.
- Add visible app warning when same-subnet bypass is enabled.
- Ensure bypass does not apply to proxy-forwarded addresses unless proxy is trusted.

### Stage 4: System Roles and Policies

- Define system roles and default policy mappings.
- Seed roles at startup.
- Apply `InitialRoleMappings`.
- Add policy checks to pages and mutating operations.
- Add tests for viewer/editor/admin access boundaries.

### Stage 5: Runtime RBAC Catalog

- Add `RbacConfigCatalog` using Quasar's existing JSON catalog pattern.
- Store user role mappings, claim mappings, policy overrides, and history snapshots.
- Add last-admin protection.
- Add effective-permission evaluation service.
- Make runtime RBAC changes hot-reload via filesystem watcher.

### Stage 6: External OIDC

- Add generic OIDC registration.
- Add configurable claim mapping.
- Add external-login callback handling.
- Add `IClaimsTransformation` for Quasar roles.
- Add UI and logs for unmapped/unknown external users.
- Add tests using fake OIDC claims.

### Stage 7: Passkeys and MFA

- Enable Identity passkeys.
- Add passkey registration and sign-in UI.
- Add TOTP setup, reset, and recovery code UI.
- Enforce role-based MFA policy.
- Add admin/session warnings for accounts missing required MFA.
- Verify HTTPS behavior for public URLs.

### Stage 8: Security Control Panel

- Add `/settings/security`.
- Add nav entry visible only to admins.
- Implement users/roles/claims/provider/MFA/trusted-network/audit tabs.
- Add validation and confirmation dialogs for risky changes.
- Avoid nested-card UI; match current MudBlazor page style.

### Stage 9: Audit and Hardening

- Add security event logging:
  - login success/failure
  - bypass login
  - role changes
  - claim mapping changes
  - MFA/passkey changes
  - OIDC config changes
- Add rate limiting for login and MFA attempts.
- Add antiforgery review for auth endpoints.
- Add secure cookie settings.
- Add optional session timeout and persistent-login controls.

### Stage 10: Public Access Validation

- Test direct LAN access.
- Test public remote access.
- Test reverse proxy access with trusted proxy config.
- Test passkey registration/login under HTTPS.
- Test OIDC login and role mapping.
- Test viewer/editor/admin behavior across all pages.
- Verify existing agent WebSocket and launcher drain behavior remain intact.

## Initial Execution Order

Recommended first execution slice:

1. Add auth option models and config binding.
2. Add Identity + SQLite storage.
3. Add setup/login/logout pages.
4. Add global auth enforcement with loopback bypass only.
5. Add same-subnet bypass after tests prove IP classification.
6. Add system roles/policies.
7. Add RBAC runtime catalog and admin panel.
8. Add external OIDC.
9. Add passkeys/MFA.

This sequence gets a working public-login boundary early, then layers RBAC, external identity, and stronger MFA without mixing all risks at once.
