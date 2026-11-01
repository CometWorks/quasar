# Access control

This page covers who can use the Quasar web UI and API: Steam sign-in, the trusted-network bypass, the three roles and what each can do, the `rbac.json` file, the **Security** page, API tokens for automation, and how to recover when you lock yourself out. For first-time admin setup see [First setup](FirstSetup.md#set-the-first-administrator).

## How Quasar identifies a caller

Quasar has three ways to authenticate a request:

| Method | Who uses it | Roles |
| --- | --- | --- |
| Steam sign-in (Steam OpenID) | People using the web UI | From `rbac.json`, looked up by SteamID64 |
| Trusted network bypass | Browsers and tools on the Quasar machine (loopback), optionally the local subnet | `Quasar:Auth:TrustedNetworkBypass:Roles`, default `admin` |
| Service principal (bearer token) | Scripts and the `Quasar cluster` CLI | Scopes `cluster.query` or `cluster.manage`, cluster API only |

Steam is the only interactive sign-in provider. `appsettings.json` contains an `ExternalProviders:Oidc` section and the **Security** page offers "Oidc" as a mapping provider, but Quasar does not register an OIDC sign-in, so these settings have no effect.

In headless mode (`--headless`) the `/login` page does not exist. Only the trusted network bypass and service principals work there.

## Steam sign-in

- `/login` redirects to Steam (`https://steamcommunity.com/openid/`) and back. Any Steam account can complete the sign-in. Quasar then gives it the roles mapped to its SteamID64 in `rbac.json`.
- The top bar shows your Steam profile name beside the sign-out button and in its tooltip. Quasar looks it up from your public Steam Community profile at sign-in without an API key and keeps it in the session cookie. If the lookup fails or takes more than five seconds, it shows `Steam <SteamID64>`. Sign out and back in to refresh the name, including after upgrading. Permissions and audit records still use SteamID64.
- An account with no mapping gets a 403 page: "You are signed in, but this account has no Quasar role." Its buttons are **Sign out** (an antiforgery-protected `POST /logout`) and **Retry Steam sign-in** (`/login?forceSteam=true`).
- The sign-in cookie is `Quasar.Auth`, HTTP-only, valid for 12 hours with sliding renewal. With `RequireHttpsForPublicAccess` enabled it is also always Secure. The cookie is encrypted with keys in `DataProtection-Keys` in the Quasar data directory. Deleting that folder signs everyone out (and makes other secrets encrypted with those keys unreadable).
- Quasar re-reads the roles from `rbac.json` on every request and reloads open UI sessions when `rbac.json` changes. Granting or removing a role takes effect without signing out. Write actions check the role again when you click, including after a confirmation dialog.
- If `Quasar:Auth:Steam:Enabled` is `false`, or `Quasar:Auth:DefaultProvider` is not `Steam`, `/login` shows "Login unavailable. Steam is not enabled as the active Quasar auth provider."

## Roles and permissions

There are three roles: `viewer`, `editor` and `admin`. Each page or action checks one permission (policy):

| Permission | Roles | Covers |
| --- | --- | --- |
| `CanView` | viewer, editor, admin | Opening any UI page, read APIs, log downloads for DS and Magnetar logs |
| `CanEditConfigs` | editor, admin | Config profiles, world templates, Magnetar plugin settings |
| `CanEditServers` | editor, admin | Creating, editing, importing and deleting servers |
| `CanControlServers` | editor, admin | Start, stop, restart, chat and commands, player actions, entity deletion, profiler mode |
| `CanManageDiscord` | editor, admin | Discord bot settings, Discord log download |
| `CanManageAppearance` | editor, admin | **Appearance** page |
| `CanManageSecurity` | admin | **Security**, **UI Plugins**, **Backups**, **Updates**, host enrollment, listing changes, backup downloads |
| `CanShutdownQuasar` | admin | Power button in the top bar (**Restart Quasar**, **Shutdown Quasar**) |
| `ClusterQuery` | viewer, editor, admin, or scope `cluster.query`/`cluster.manage` | Viewing clusters and the cluster query API |
| `ClusterManage` | editor, admin, or scope `cluster.manage` | Changing clusters |

With authentication on, every UI page requires at least `viewer`.

### What viewers can and cannot do

Viewers see all pages under **Control plane** in the navigation: **Dashboard**, **Configs**, **Worlds**, **Plugins**, **Players**, **Chat**, **Entities**, **Analytics**, **Discord** and **Listings**, plus **Hosts**, **Notifications** and cluster status. They cannot change anything.

| Page | Viewer sees | Change needs |
| --- | --- | --- |
| Configs | Profile settings, mods, plugins, access lists; passwords hidden | `CanEditConfigs` |
| Worlds | Managed and installed world templates | `CanEditConfigs` |
| Plugins | Plugin inventory and logs; plugin configuration values hidden | `CanEditConfigs`, `ClusterManage` for cluster details |
| Players | Known players and status | `CanControlServers` |
| Chat | Recent chat, refresh | `CanControlServers` to send chat or commands, save, restart |
| Entities | Entity queries and details | `CanControlServers` to delete |
| Analytics | Charts and profiler status | `CanControlServers` to change profiler mode |
| Discord | Bot status and channel bindings; token and console log hidden | `CanManageDiscord` |
| Listings | Publication settings | `CanManageSecurity` |

Viewers do not see **Security**, **UI Plugins**, **Backups**, **Updates** or **Appearance** in the navigation. Chat and plugin logs can contain player names and private messages, so give `viewer` only to people allowed to read them.

Plugin-provided UI pages follow the permission each plugin declares (see [UI plugins](UiPlugins.md)).

## The rbac.json file

`rbac.json` lives in the Quasar data directory (the install root, `QUASAR_INSTALL_DIR`), next to `appsettings.json`. See [Configuration](Configuration.md#where-files-live) for paths.

```json
{
  "subjectRoleMappings": [
    { "provider": "Steam", "subject": "76561198000000000", "roles": ["admin"] },
    { "provider": "Steam", "subject": "76561198000000001", "roles": ["viewer"] }
  ],
  "claimRoleMappings": [],
  "policyOverrides": {}
}
```

Rules Quasar applies when it reads the file:

- `provider` defaults to `Steam`. `subject` is the 17-digit SteamID64. Matching is case-insensitive.
- Role names are lowercased. Unknown role names are dropped. A mapping with no valid role or an empty subject is dropped. Duplicate provider and subject pairs keep the first.
- A subject can have several roles; the highest one decides in practice, since each role grants a superset of the one below.
- `claimRoleMappings` and `policyOverrides` are read and saved but do not change who can do what. The permission table above is fixed.

Quasar watches the file. Saving it while Quasar runs applies the change within moments, and the log shows "Reloaded RBAC config from disk after external edit." If the file is not valid JSON, Quasar logs "Failed loading RBAC config from <path>" and treats it as having no mappings, so no Steam user has a role until the file is fixed.

`QUASAR_ADMIN_STEAM_ID` creates the file with one `admin` mapping when the file does not exist; see [First setup](FirstSetup.md#option-3-quasar_admin_steam_id).

## The Security page

**Security** (`/settings/security`, admin only, under **Infrastructure & tools**) has four panels.

- **Auth Provider**: read-only summary of the default provider, Steam login, private-LAN/public-HTTPS mode, trusted loopback, trusted private LAN and trusted proxies, and the path of the `appsettings.json` it writes.
- **Private LAN and Reverse Proxy**: checkboxes **Trust loopback access**, **Trust private LAN access**, and **Require HTTPS for non-LAN access**, the **Trusted reverse proxies** text box (one IP or CIDR per line, comma or space), **Save Security Settings** and **Apply Public Proxy Preset** (loopback and private LAN on, non-LAN HTTPS required; you still have to save). It writes the corresponding `Quasar:Auth` values in the data directory's `appsettings.json`. The trusted-address checkboxes apply immediately; HTTPS mode and trusted proxy changes need a Quasar restart. An invalid entry fails with "'<value>' is not a valid IP address or CIDR range."
- **Data Handling Consent**: the YES/NO data-sharing decision, diagnostic reports, crash dumps and listing visibility. See [Diagnostics](Diagnostics.md).
- **Runtime Subject Role Mappings**: add a mapping with **Provider** (Steam), **SteamID** and **Role**, then click the plus button. Each add grants one role. The delete button removes a mapping after confirmation ("Remove RBAC mapping?"). Changes are saved to `rbac.json` at once and apply to signed-in users right away.

The Provider, SteamID/Subject and Role controls use matching single-line field heights.

## Trusted network bypass

When a request has no bearer token and no signed-in user, Quasar checks whether it comes from a trusted address. If so, it treats the caller as the user "Trusted network" with the roles in `Quasar:Auth:TrustedNetworkBypass:Roles` (default `admin`).

| Key | Default | Meaning |
| --- | --- | --- |
| `AllowLoopback` | `true` | Trust `127.0.0.1` and `::1` |
| `AllowSameSubnet` | `true` | Trust RFC 1918 private IPv4 ranges, IPv6 ULA (`fc00::/7`), and IPv4 clients on an active local interface subnet |
| `TrustedProxies` | `[]` | Reverse proxy IPs or CIDR ranges whose `X-Forwarded-*` headers Quasar accepts; loopback proxies are always trusted |
| `Roles` | `["admin"]` | Roles given to trusted callers; unknown names fall back to `admin` |

Treat every trusted address as a full admin unless you reduce `Roles`. Trusted addresses also need an intended destination and same-origin browser context. With `AllowSameSubnet` on (the default), LAN users at an accepted destination can manage Quasar over HTTP without signing in. This includes `192.168.1.10` connecting to `http://192.168.1.80:8080`, all `192.168.x.x`, `10.x.x.x`, `172.16.x.x` through `172.31.x.x`, and private IPv6 ULA addresses. Turn off **Trust private LAN access** to opt out; an explicit `AllowSameSubnet: false` disables LAN bypass. Browser push still requires HTTPS or localhost.

Quasar checks the original Host and scheme before proxy forwarding and checks the effective destination again before granting roles. Accepted destinations are the public BaseUrl (`QUASAR_PUBLIC_BASE_URL`, legacy `MAGNETAR_WEB_BASE_URL`), a concrete listener URL, or localhost, loopback or the actual socket destination IP at its local port. A wildcard listener does not authorize arbitrary DNS aliases. Set the public BaseUrl to the URL browsers should use.

A supplied Origin must match the effective scheme, host and port. A supplied `Sec-Fetch-Site` must be `same-origin` or `none`. Without Origin, a supplied Referer must be same-origin. Same-origin Viewer iframes and direct navigation or bootstrap clients without context headers still work. A rejected context gets no automatic roles; existing signed-in users and service principals keep their normal authentication and permissions. The separate machine endpoint policy is unchanged.

The Viewer follow-up needs both the Quasar shared auth update and the Viewer plugin path update. It does not delay the original urgent agent/Host fix. See [Viewer follow-up rollout](https://github.com/CometWorks/quasar/blob/dis-0042-viewer-guards/Docs/SecurityUpgradeDIS0042.md#viewer-follow-up-rollout).

### Behind a reverse proxy

- Quasar accepts `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` only from loopback and from `TrustedProxies` entries, one proxy hop deep. The proxy must send all three so Steam returns the browser to the right public URL.
- Quasar defaults to private LAN HTTP mode with `RequireHttpsForPublicAccess=false` and same-subnet bypass enabled. Keep its port behind a firewall. The public proxy preset enables HTTPS enforcement but retains loopback and same-subnet exceptions. After trusted forwarding, non-LAN public clients must use HTTPS and sign in; direct LAN clients retain HTTP and trusted-network access. Auth and Steam correlation cookies are always Secure while HTTPS enforcement is on.
- Trusted bypass requires a known original socket peer, an accepted original backend Host, one valid `X-Forwarded-For` address and fully consumed `X-Forwarded-*` values. The proxy must replace incoming forwarding values. `Forwarded`, `X-Real-IP`, ambiguous or unconsumed values and client-supplied `X-Original-*` do not establish bypass trust. The user can still sign in with Steam.
- Behind a trusted proxy, Quasar checks the forwarded client address, so remote browsers have to sign in even when the proxy runs on the same machine.

The checklist under **Safely expose Quasar to the internet** on the **Security** page: put Quasar behind an HTTPS reverse proxy, keep the Quasar port closed on the router, apply the public proxy preset, add the proxy's IP or Docker bridge CIDR if it is on another host, send the three `X-Forwarded-*` headers, restart Quasar, then test from an outside network and expect the Steam sign-in.

## API authentication for automation

Service principals are bearer tokens for scripts. They work only on the cluster API (`/api/v1/clusters/...`). Other endpoints need a signed-in user or a trusted address.

Configure them in `appsettings.json`. Store only the name of an environment variable; the token itself goes in the Quasar service environment:

```json
{
  "Quasar": {
    "Auth": {
      "ServicePrincipals": [
        {
          "Name": "factory-reader",
          "TokenEnvironmentVariable": "QUASAR_FACTORY_READER_TOKEN",
          "Scopes": [ "cluster.query" ],
          "Clusters": [ "production" ]
        }
      ]
    }
  }
}
```

- The token must be at least 32 characters. Shorter values never match.
- Scopes: `cluster.query` (read and operation status) and `cluster.manage` (also changes). Other scope names are dropped.
- `Clusters` lists the cluster names the principal may access; `"*"` means all.
- An entry without a name, variable, valid scope or cluster is ignored. If two principals have the same token value, the token matches neither.
- Cluster setup and conversion routes also require the `editor` role, so service principals cannot call them.
- Failures on the cluster API return JSON: 401 `authentication_required` ("A valid Quasar credential is required.") or 403 `scope_forbidden` / `cluster_forbidden`.
- A request with an `Authorization: Bearer` header never falls back to the trusted network bypass, even from loopback.
- Service principals are read at startup. Restart Quasar after adding one or changing its token variable.

Send the token as `Authorization: Bearer <token>`. The `Quasar cluster ...` CLI reads it from `QUASAR_API_TOKEN` (or the variable named with `--token-env`) and sends nothing when the variable is empty, which works from the Quasar machine through the loopback bypass. See [Configuration](Configuration.md#command-line) and [Clusters](Clusters.md).

Public health and readiness probes expose only status. The update overlay uses
`/api/version` to confirm the running release; that endpoint requires the same
read permission as the dashboard and does not cache its response.

## Turning authentication off

`Quasar:Auth:Enabled` defaults to `true`. Setting it to `false` removes the authorization checks on HTTP endpoints, but Quasar starts only when every effective listener is loopback. It checks `Quasar:Host`, `QUASAR_WEB_HOST`, and every `ASPNETCORE_URLS` binding before Kestrel starts. The UI still checks roles and Quasar no longer creates trusted-network users, so pages show "Access denied." and write actions fail. Do not use it to skip sign-in; use the trusted network bypass instead.

## Locking yourself out and recovering

- The **Security** page refuses to remove the last mapping with the `admin` role: "Failed saving RBAC settings: Cannot remove the last runtime administrator mapping."
- Editing `rbac.json` by hand has no such check. To recover, use one of these:
  - Edit `rbac.json` in the data directory and add your SteamID with `admin`. Quasar reloads it without a restart.
  - Open `http://127.0.0.1:8080` in a browser on the Quasar machine. With `AllowLoopback` on (default), you are admin without signing in. An SSH tunnel to the Quasar port (`ssh -L 8080:127.0.0.1:8080 user@quasar-host`) also arrives as loopback.
  - Docker: delete `quasar-data/rbac.json` while `QUASAR_ADMIN_STEAM_ID` is set; Quasar writes a new file with that admin. This removes all other mappings.

## Settings reference

All under `Quasar:Auth` in `appsettings.json`. These are read at startup; restart Quasar after editing them by hand (the **Security** page checkboxes are the exception).

| Key | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | See [Turning authentication off](#turning-authentication-off) |
| `RequireHttpsForPublicAccess` | `false` | Private LAN HTTP by default; enable for a public reverse proxy to reject non-LAN HTTP and secure auth cookies |
| `DefaultProvider` | `Steam` | Only `Steam` has a sign-in |
| `Steam:Enabled` | `true` | `false` disables `/login` |
| `TrustedNetworkBypass:*` | see above | Also editable on the **Security** page |
| `ServicePrincipals` | `[]` | See [API authentication](#api-authentication-for-automation) |
| `Workshop:*` | | Steam Workshop browsing for mods, see [Config profiles](ConfigProfiles.md) |

Environment variables use `__` as separator, for example `QUASAR__AUTH__TRUSTEDNETWORKBYPASS__ALLOWSAMESUBNET=false`.

## Common problems

| Symptom | Cause and fix |
| --- | --- |
| "this account has no Quasar role" after Steam sign-in | No mapping for that SteamID64. Add it on **Security** or in `rbac.json`. |
| Steam sign-in works locally but remote users land on the wrong URL | The reverse proxy does not send `X-Forwarded-Host`/`X-Forwarded-Proto`, or its address is not in `TrustedProxies` (restart after adding). |
| Everyone on the LAN can change settings without signing in | `AllowSameSubnet` is on. Turn it off on **Security**. |
| Local browser is asked to sign in | The request went through a proxy with a non-LAN forwarded address, used an unaccepted destination/context, or its applicable LAN trust option is off. |
| All Steam users lost access after an edit | `rbac.json` is not valid JSON. Check the Quasar log for "Failed loading RBAC config". |
| Cluster API returns 401 with a token | Token variable missing in the Quasar service environment, shorter than 32 characters, shared by two principals, or Quasar not restarted after the change. |
| `Login unavailable` page | `Quasar:Auth:Steam:Enabled` is `false` or `DefaultProvider` is not `Steam`. |

## Hosted public access

All configured Steam administrators share their hosted Nebula panel; one payer controls billing. Billing transfers need that payer’s approval. Recovery onto another deployment retains its subscription and removes the old registration. See [Hosted access](HostedPanel.md).

**Security → Manage access setup** lets an admin choose self-deployment or Nebula
hosted enrollment at `https://dash.se-servers.net/<panel-name>/`. Hosted
traffic always uses Steam/RBAC and endpoint credentials, even though frpc runs
on loopback. Platform super-admin/free-hosting rights do not create a local role.
Stripe billing is implemented in Nebula; checkout remains
disabled until operator onboarding and acceptance.
See [Hosted access](HostedPanel.md) for setup, private files and release gates.

Public panel access places its setup and billing buttons in a spaced row below
the description. The buttons wrap on narrow screens, with billing details below.

## Sources

- `Quasar/Services/Auth/SteamProfileClient.cs`
- `Docs/Configuration.md`
- `Docs/QuickStart.md`
- `Docs/Docker.md`
- `Quasar/appsettings.json`
- `Quasar/Program.cs`
- `Quasar/Services/Auth/QuasarAuthConstants.cs`
- `Quasar/Services/Auth/QuasarAuthOptions.cs`
- `Quasar/Services/Auth/PublicAccessSecurity.cs`
- `Quasar/Services/Auth/QuasarAuthSettingsService.cs`
- `Quasar/Services/Auth/QuasarPermissionService.cs`
- `Quasar/Services/Auth/QuasarRoleMapper.cs`
- `Quasar/Services/Auth/RbacConfig.cs`
- `Quasar/Services/Auth/RbacConfigCatalog.cs`
- `Quasar/Services/Auth/ServicePrincipalAuthenticator.cs`
- `Quasar/Services/Auth/TrustedNetworkEvaluator.cs`
- `Quasar/Services/MachineEndpointSecurity.cs`
- `Quasar/Services/WebServiceProbeEndpoints.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Docs/SecurityUpgradeDIS0042.md`
- `Quasar/Services/ClusterApi.cs`
- `Quasar/Services/ClusterHostEnrollmentApi.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Components/Routes.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Pages/Security.razor`
- `Quasar/Components/Pages/Configs.razor`
- `Quasar/Components/Pages/Plugins.razor`
- `Quasar/Components/Pages/Players.razor`
- `Quasar/Components/Pages/Chat.razor`
- `Quasar/Components/Pages/Entities.razor`
- `Quasar/Components/Pages/Analytics.razor`
- `Quasar/Components/Pages/Discord.razor`
- `Quasar/Components/Pages/ServerListing.razor`
- `Quasar/Components/Pages/Hosts.razor`
- `Quasar/Components/Pages/Notifications.razor`
- `Quasar/Components/Pages/UiPlugins.razor`
- `Quasar/Components/Pages/Backup.razor`
- `Quasar/Components/Pages/Updates.razor`
- `Quasar/Components/Pages/Appearance.razor`
- `Quasar.Bootstrap/ClusterCli.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/wwwroot/quasar-ui.css`
