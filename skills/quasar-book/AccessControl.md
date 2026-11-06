# Access control

This page covers who can use the Quasar web UI and API: Steam sign-in, the trusted-network bypass, the three roles and what each can do, the `rbac.json` file, the **Security** page, the assistant and player agent principals, the **Audit log**, API tokens for automation, the separate policy for machine endpoints, and how to recover when you lock yourself out. For first-time admin setup see [First setup](FirstSetup.md#set-the-first-administrator).

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
- The sign-in cookie is `Quasar.Auth`, HTTP-only, `SameSite=Lax`, valid for 12 hours with sliding renewal. With `RequireHttpsForPublicAccess` enabled it is also always Secure. Through a [hosted panel](HostedPanel.md) its name gets the panel name as a suffix, its path is the panel prefix, and it is always Secure. The cookie is encrypted with keys in `DataProtection-Keys` in the Quasar data directory. Deleting that folder signs everyone out (and makes other secrets encrypted with those keys unreadable).
- Quasar re-reads the roles from `rbac.json` on every request and reloads open UI sessions when `rbac.json` changes. Granting or removing a role takes effect without signing out. Write actions check the role again when you click, including after a confirmation dialog.
- If `Quasar:Auth:Steam:Enabled` is `false`, or `Quasar:Auth:DefaultProvider` is not `Steam`, `/login` shows "Login unavailable. Steam is not enabled as the active Quasar auth provider."

## Roles and permissions

There are three roles: `viewer`, `editor` and `admin`. Each page or action checks one permission (policy):

| Permission | Roles | Covers |
| --- | --- | --- |
| `CanView` | viewer, editor, admin | Opening any UI page, read APIs, log downloads for DS and Magnetar logs |
| `CanEditConfigs` | editor, admin | Config profiles, world templates, Magnetar plugin settings |
| `CanEditServers` | editor, admin | Creating, editing, importing and deleting servers; direct world import |
| `CanControlServers` | editor, admin | Start, stop, restart, chat and commands, player actions, entity deletion, profiler mode |
| `CanManageDiscord` | editor, admin | Discord bot settings, Discord log download |
| `CanManageAppearance` | editor, admin | **Appearance** page |
| `CanManageSecurity` | admin | **Security**, **UI Plugins**, **Backups**, **Updates**, host enrollment, listing changes, backup downloads |
| `CanShutdownQuasar` | admin | Power button in the top bar (**Restart Quasar**, **Shutdown Quasar**) |
| `CanUseAssistant` | editor, admin | Chatting with the admin assistant of an assistant UI plugin |
| `CanActAsPlayer` | none; only the player agent | Player-scoped operations of the player agent |
| `ClusterQuery` | viewer, editor, admin, or scope `cluster.query`/`cluster.manage` | Viewing clusters and the cluster query API |
| `ClusterManage` | editor, admin, or scope `cluster.manage` | Changing clusters |
| `ClusterMaintenance` | authenticated admin | Offline profiling, deliberate compensation prediction and per-key production-rate cache clear |

With authentication on, every UI page requires at least `viewer`.

### What viewers can and cannot do

Viewers see all pages under **Control plane** in the navigation: **Dashboard**, **Configs**, **Worlds**, **Plugins**, **Players**, **Chat**, **Entities**, **Analytics**, **Discord** and **Listings**, plus **Hosts**, **Notifications** and cluster status. They cannot change anything.

| Page | Viewer sees | Change needs |
| --- | --- | --- |
| Configs | Profile settings, mods, plugins, access lists; passwords hidden | `CanEditConfigs` |
| Worlds | Selected server saves, status and profiles; reusable templates | `CanEditServers` for direct world import; `CanEditConfigs` for templates and imported profile creation |
| Plugins | Plugin inventory and logs; plugin configuration values hidden | `CanEditConfigs`, `ClusterManage` for cluster details |
| Players | Known players and status | `CanControlServers` |
| Chat | Recent chat, refresh | `CanControlServers` to send chat or commands, save, restart |
| Entities | Entity queries and details | `CanControlServers` to delete |
| Analytics | Charts and profiler status | `CanControlServers` to change profiler mode |
| Discord | Bot status and channel bindings; token and console log hidden | `CanManageDiscord` |
| Listings | Publication settings | `CanManageSecurity` |

Viewers do not see **Security**, **UI Plugins**, **Backups**, **Updates** or **Appearance** in the navigation. Chat and plugin logs can contain player names and private messages, so give `viewer` only to people allowed to read them.

Plugin-provided UI pages follow the permission each plugin declares (see [UI plugins](UiPlugins.md)).

### Plugin endpoint authorization

On **Security**, **Plugin endpoint authorization** lists operations registered
with Quasar's browser SDK. They require authentication by default. You can disable
an operation or replace its required policies; every selected policy must pass.
Both its SDK route and adopted legacy URLs use that rule. **Restore declared
policies** removes the operation override but keeps shared role grants.

**Shared policy role grants** changes the roles allowed by a role-based policy
for every endpoint using it. Empty role grants deny access. The editor keeps the
`admin` role for `CanManageSecurity` and retains scope/resource requirements.
Only callers with current `CanManageSecurity` permission can view or save these
settings. A stale administrator page cannot save a change after access is lost.

Settings are stored in `rbac.json`. Earlier builds saved `policyOverrides` without
using them; they now affect authorization. Review existing overrides before an
upgrade. Invalid SDK rules deny access. If the RBAC file is malformed, restore a
valid backup before editing SDK rules. The viewer registers all 15 scene, asset
setup/streaming and replay operations. View operations default to `CanView`;
asset administration and history default to `CanManageSecurity`. Asset consent
and user ownership still apply after you change an operation policy. WebRTC
remains a separate implementation. See [the SDK guide](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarBrowserSdk.md).

### Assistant and player agent

An assistant UI plugin acts through two agent principals that Quasar builds
itself; no cookie, token or request header can claim to be one. The admin
assistant acts for the person chatting with it, who needs `CanUseAssistant`.
The player agent acts for one player and only gets `CanActAsPlayer` operations.

**Assistant roles** under **Plugin endpoint authorization** sets the roles each
agent acts with: the admin assistant has `editor` and the player agent none by
default. Neither can hold `admin`; saving it fails with "An assistant or player
agent can never hold the admin role." and Quasar drops it from a hand-edited
file. On top of its roles, a call from either agent is refused with "Assistants
cannot use this operation." when the operation needs `CanManageSecurity` or
`CanShutdownQuasar`, its plugin closed it to agents, or it belongs to security,
RBAC, auth settings, service principals, updates, Workshop credentials, the
Discord bot token or the assistant's LLM settings. The assistant also never does
more than the person asking it could do by hand.

The player agent's operations (`my.server.info`, `my.grids`, `my.grid.backups`,
`my.grid.losses`, `my.grid.locate`, `my.grid.restore`) take no player: they use
the Steam ID of the player who asked in game, or of the Steam account linked to
the Discord user with `/link`. The grid ones work where the Grid Backups plugin
is loaded.

Quasar sets no spending limit for the assistant; set one at the model provider.
The assistant plugin keeps its instructions and tool list identical between
requests, so the provider can cache them. In a test with Claude Sonnet 5.5 on
2026-11-06, 13 questions (8 from a player in game, 5 from an admin) each took
3 to 8 seconds and 1 to 3 model calls. 87% of the input tokens were read from
the cache, over 90% in most runs once it was warm, and the 13 answers cost
about USD 0.12 together. Long conversations and large log reads cost more.

## The rbac.json file

`rbac.json` lives in the Quasar data directory (the install root, `QUASAR_INSTALL_DIR`), next to `appsettings.json`. See [Configuration](Configuration.md#where-files-live) for paths.

```json
{
  "subjectRoleMappings": [
    { "provider": "Steam", "subject": "76561198000000000", "roles": ["admin"] },
    { "provider": "Steam", "subject": "76561198000000001", "roles": ["viewer"] }
  ],
  "claimRoleMappings": [],
  "policyOverrides": {},
  "agentRoles": { "assistant": ["editor"], "player": [] },
  "discordSubjects": [
    { "discordUserId": "123456789012345678", "provider": "Steam", "subject": "76561198000000000" }
  ]
}
```

Rules Quasar applies when it reads the file:

- `provider` defaults to `Steam`. `subject` is the 17-digit SteamID64. Matching is case-insensitive.
- Role names are lowercased. Unknown role names are dropped. A mapping with no valid role or an empty subject is dropped. Duplicate provider and subject pairs keep the first.
- A subject can have several roles; the highest one decides in practice, since each role grants a superset of the one below.
- `claimRoleMappings` and `policyOverrides` are read and saved but do not change who can do what. The permission table above is fixed.
- `agentRoles` keeps only `assistant` and `player`. A missing entry gets its default, and `admin` is dropped.
- `discordSubjects` entries need a numeric Discord user ID and a subject; others are dropped, and a Discord user keeps its first entry. A non-Steam provider grants nothing.

Quasar watches the file. Saving it while Quasar runs applies the change within moments, and the log shows "Reloaded RBAC config from disk after external edit." If the file is not valid JSON, Quasar logs "Failed loading RBAC config from <path>" and treats it as having no mappings, so no Steam user has a role until the file is fixed.

`QUASAR_ADMIN_STEAM_ID` creates the file with one `admin` mapping when the file does not exist; see [First setup](FirstSetup.md#option-3-quasar_admin_steam_id).

## The Security page

**Security** (`/settings/security`, admin only, under **Infrastructure & tools**) has six panels.

- **Public panel access**: **Manage access setup** opens the access setup dialog, where you choose self-deployment behind your own reverse proxy or a hosted Nebula panel. Once the installation is enrolled with Nebula, **Manage Nebula hosting and billing** opens the Nebula account page. See [Hosted access](HostedPanel.md).
- **Auth Provider**: read-only summary of the default provider, Steam login, private-LAN/public-HTTPS mode, trusted loopback, trusted private LAN and trusted proxies, and the path of the `appsettings.json` it writes.
- **Private LAN and Reverse Proxy**: checkboxes **Trust loopback access**, **Trust private LAN access**, and **Require HTTPS for non-LAN access**, the **Trusted reverse proxies** text box (one IP or CIDR per line, comma or space), **Save Security Settings** and **Apply Public Proxy Preset** (loopback and private LAN on, non-LAN HTTPS required; you still have to save). It writes the corresponding `Quasar:Auth` values in the data directory's `appsettings.json`. The trusted-address checkboxes apply immediately; HTTPS mode and trusted proxy changes need a Quasar restart. An invalid entry fails with "'<value>' is not a valid IP address or CIDR range."
- **Data Handling Consent**: the YES/NO data-sharing decision, diagnostic reports, crash dumps and listing visibility. See [Diagnostics](Diagnostics.md).
- **Discord Admin Mappings**: maps a Discord user ID to a Steam account, so that Discord user can use `/assistant` with that account's Quasar roles. Discord's own Administrator permission doesn't count. Only Steam accounts can be mapped.
- **Runtime Subject Role Mappings**: add a mapping with **Provider** (Steam), **SteamID64 or profile URL** and **Role**, then click the plus button. Each add grants one role. The delete button removes a mapping after confirmation ("Remove RBAC mapping?"). Changes are saved to `rbac.json` at once and apply to signed-in users right away.

For Steam mappings, enter a 17-digit ID or a Steam Community `/id/<vanity-name>` or `/profiles/<SteamID64>` URL. Quasar resolves vanity URLs automatically through Steam Community without an API key; numeric IDs and numeric profile URLs need no network lookup. It saves only the SteamID64. Invalid input disables the plus button. If a lookup fails, an error appears, the input stays in place and no mapping is saved. Retry or enter the numeric ID if Steam is unavailable.

Runtime RBAC saves reject new Steam subjects that are not 17-digit IDs. To fix an invalid mapping saved by an older build, add the corrected ID with the same role first, then remove the old mapping. Hand-edited `rbac.json` and `QUASAR_ADMIN_STEAM_ID` still need numeric IDs.

The role-mapping editor and first-administrator setup tell you before saving that the panel refreshes to apply permissions. You may briefly see a reconnect message after adding or removing a mapping, including with WebRTC. The change is saved before the refresh.

The **Provider** list also offers "Oidc", but such mappings have no effect, see [How Quasar identifies a caller](#how-quasar-identifies-a-caller).

### The first administrator

When `rbac.json` has no mapping with the `admin` role, the access setup dialog ("Set up administrator access") opens for anyone with admin rights. Without a mapping that means the trusted network bypass, usually a browser on the Quasar machine. Its first step asks for an **Administrator SteamID64 or profile URL**; **Save administrator and continue** resolves a profile URL and writes the numeric SteamID64 mapping to `rbac.json`. A failed lookup leaves the step open and saves nothing. A Steam sign-in alone cannot claim an installation that has no administrator, because an unmapped account has no role. The dialog also opens while no access method (self-deploy or hosted) has been chosen yet; **Cancel** dismisses it. See also [First setup](FirstSetup.md#set-the-first-administrator).

## Audit log

**Audit log** (`/audit`, admin only, under **Infrastructure & tools**) lists every change made through Quasar's operation registry, by people, the admin assistant and the player agent. Reads are not recorded.

These pages go through the registry, so their changes appear in the log:

- **Home**, **Servers** and **Chat**: start, stop, restart and save of standalone servers, and the save and player buttons in a server's details.
- The server editor: saving an existing server under its own name.
- **Configs**: saving a config profile.
- **Players**: kick, ban, unban, role and kick cooldown on standalone servers.
- **Backup**: world backups (latest save, or save and back up) and server backups.

Not recorded: Discord commands, cluster changes and cluster player actions, creating, renaming, cloning and deleting servers or profiles, killing a starting server, stop and back up, restores, configuration backups, and the other pages. The page says so at the top.

Each entry shows the time (UTC), who acted, who asked (the person behind the assistant, or the player's Steam ID and name), the operation, the server, the mode (applied, proposed by an agent, approved or rejected by an admin) and the result: ok; refused, when access was denied or the operation itself declined, such as a grid restore with no free spot (nothing changed, and the reason is shown); or failed, when something broke on the way. An approved or rejected call names the proposal it decided. **Details** shows the arguments, with passwords, tokens and keys replaced by `<redacted>`, the error, and the undo data. Filter by time range, principal, requester, operation, server and result.

**Undo** appears on changes whose operation can reverse them. It needs `CanManageSecurity` and the permissions of the original operation. A player's own action through the assistant, such as a grid restore, needs `CanControlServers` instead; undoing a restore removes the restored grids that are still there and gives the restore back to the player's limit. Quasar first checks that the current state is still what the change left. If not, the page shows the difference and offers **Undo anyway** or **Leave it**. An undo is a new entry, shown with the mode **Undo of** and the time of the change it reversed; entries are never rewritten. A change can be undone once, after which its row shows **Undone** instead of the button.

Entries and their undo files are kept for `Quasar:Audit:RetentionDays` days (default 30) in `audit/<yyyy-MM>.jsonl` and `audit/blobs/` in the data directory. An entry whose undo data has expired can no longer be undone.

## Trusted network bypass

When a request has no bearer token and no signed-in user, Quasar checks whether it comes from a trusted address. If so, it treats the caller as the user "Trusted network" with the roles in `Quasar:Auth:TrustedNetworkBypass:Roles` (default `admin`).

| Key | Default | Meaning |
| --- | --- | --- |
| `AllowLoopback` | `true` | Trust `127.0.0.1` and `::1` |
| `AllowSameSubnet` | `true` | Trust RFC 1918 private IPv4 ranges, IPv6 ULA (`fc00::/7`), and IPv4 clients on an active local interface subnet |
| `TrustedProxies` | `[]` | Reverse proxy IPs or CIDR ranges whose `X-Forwarded-*` headers Quasar accepts; loopback proxies are always trusted |
| `Roles` | `["admin"]` | Roles given to trusted callers; unknown names fall back to `admin` |

An explicit role list replaces the default. Set `Roles` to `["viewer"]` for read-only trusted access, then restart Quasar. The default `admin` role is not retained.

Treat every trusted address as a full admin unless you reduce `Roles`. Trusted addresses also need an intended destination and same-origin browser context. With `AllowSameSubnet` on (the default), LAN users at an accepted destination can manage Quasar over HTTP without signing in. This includes `192.168.1.10` connecting to `http://192.168.1.80:8080`, all `192.168.x.x`, `10.x.x.x`, `172.16.x.x` through `172.31.x.x`, and private IPv6 ULA addresses. Turn off **Trust private LAN access** to opt out; an explicit `AllowSameSubnet: false` disables LAN bypass. Browser push still requires HTTPS or localhost.

Quasar checks the destination twice: the original Host and scheme before proxy forwarding, and the effective destination after it. Both must be an accepted destination: the public BaseUrl (`QUASAR_PUBLIC_BASE_URL`, legacy `MAGNETAR_WEB_BASE_URL`), a concrete listener URL, or `localhost`, a loopback address or the socket's own destination IP at the socket's local port. A wildcard listener (`0.0.0.0`) does not accept arbitrary DNS names. To keep passwordless access through a DNS name, set the public BaseUrl to the URL browsers use.

The browser context must be same-origin too. A supplied `Origin` must match the effective scheme, host and port. A supplied `Sec-Fetch-Site` must be `same-origin` or `none`. Without `Origin`, a supplied `Referer` must be same-origin. Requests without these headers, such as direct navigation, same-origin iframes and scripts, still qualify. A request that fails any check gets no automatic roles and has to sign in. Signed-in users and service principals are not affected.

Requests through a [hosted panel](HostedPanel.md) never get the bypass, even though they arrive from the local connector on loopback.

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

## Machine endpoints

Agents of managed servers, Host executors and Quasar's own local tools use a separate set of routes: `/ws/agent`, `/api/internal/...`, `/api/discovery`, `/api/v1/hosts/...` and the one-use enrollment download `/api/v1/host-enrollment/<ticket>`. Apart from `/api/discovery`, which only local tools read, each of them requires its own credential: the per-server or per-node agent token, the Host's enrollment credential, the enrollment ticket, or the launcher secret. There is no tokenless access, not even from loopback. Signed-in users, the trusted network bypass and service principals don't grant access to them.

On top of the credential, Quasar checks every request on these routes before any proxy header is applied. It answers 403 unless:

- the socket peer address is in `Quasar:MachineEndpoints:AllowedSources`,
- the `Host` header exactly matches an entry of `Quasar:MachineEndpoints:AllowedHosts`, and
- the request has no `Origin` header at all, not even an empty one or `null`.

| Key | Environment variable | Default |
| --- | --- | --- |
| `AllowedSources` | `QUASAR_MACHINE_ALLOWED_SOURCES` | `127.0.0.1`, `::1` |
| `AllowedHosts` | `QUASAR_MACHINE_ALLOWED_HOSTS` | The public BaseUrl's authority, plus `127.0.0.1:<port>`, `localhost:<port>` and `[::1]:<port>` |

The environment variables take a semicolon-separated list and replace the whole list. Sources are IP addresses or CIDR ranges inside loopback, RFC 1918 or IPv6 ULA space, at least `/16` for IPv4 and `/48` for IPv6. Quasar refuses to start with a public, broader or malformed entry, or with an empty list. Host entries are exact authorities, compared case-insensitively, with the port when the client sends one; wildcards, `0.0.0.0` and a trailing dot are refused.

With the defaults only processes on the Quasar machine get through, which covers standalone servers and the [local Host](ClusterHosts.md#the-local-host). Before you enroll a Host executor on another machine, add its address (or its site's subnet) to `AllowedSources` and the Quasar address it uses to `AllowedHosts`:

```json
{
  "Quasar": {
    "MachineEndpoints": {
      "AllowedSources": [ "127.0.0.1", "::1", "10.42.0.12", "10.43.8.0/24" ],
      "AllowedHosts": [ "127.0.0.1:8080", "localhost:8080", "quasar.vpn:8080" ]
    }
  }
}
```

Behind a reverse proxy, Quasar sees the proxy as the source, so list the proxy's own address and the backend `Host` it sends, and don't let the proxy strip the `Origin` header: it is what keeps browser requests off these routes. A [hosted panel](HostedPanel.md) needs no entry: its traffic arrives from the local connector on loopback and counts as `127.0.0.1:<port>`.

Use HTTPS/WSS for agent and Host traffic that leaves the machine, or a private encrypted VPN. The source list does not encrypt anything. The background and the upgrade order are in `Docs/SecurityUpgradeDIS0042.md` in the Quasar repository.

## Probe endpoints

`/api/health` and `/api/ready` need no sign-in and return only a status (`ready` or `not-ready`, with 503 while not ready). `/api/version` returns the running version for the update overlay; with authentication on it needs `CanView`, and it is never cached.

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

The machine endpoint lists are under `Quasar:MachineEndpoints` instead, see [Machine endpoints](#machine-endpoints).

Environment variables use `__` as separator, for example `QUASAR__AUTH__TRUSTEDNETWORKBYPASS__ALLOWSAMESUBNET=false`.

## Common problems

| Symptom | Cause and fix |
| --- | --- |
| "this account has no Quasar role" after Steam sign-in | No mapping for that SteamID64. Add it on **Security** or in `rbac.json`. |
| Steam sign-in works locally but remote users land on the wrong URL | The reverse proxy does not send `X-Forwarded-Host`/`X-Forwarded-Proto`, or its address is not in `TrustedProxies` (restart after adding). |
| Everyone on the LAN can change settings without signing in | `AllowSameSubnet` is on. Turn it off on **Security**. |
| Local browser is asked to sign in | The request went through a proxy with a non-LAN forwarded address, used a host name that is not an accepted destination, came from another site's page, or the matching LAN trust option is off. |
| Plain text "HTTPS is required for non-LAN Quasar access." (HTTP 400) | `RequireHttpsForPublicAccess` is on and the request came over HTTP from a non-LAN address. Use the HTTPS proxy URL. |
| Agent, Host enrollment or Host connection gets 403 | The machine endpoint policy refused it: the source address or the `Host` header is not listed under `Quasar:MachineEndpoints`, or the client sent an `Origin` header. See [Machine endpoints](#machine-endpoints). |
| Quasar doesn't start after editing `MachineEndpoints` | An entry is public, broader than `/16` (IPv4) or `/48` (IPv6), malformed, or a wildcard. |
| All Steam users lost access after an edit | `rbac.json` is not valid JSON. Check the Quasar log for "Failed loading RBAC config". |
| Cluster API returns 401 with a token | Token variable missing in the Quasar service environment, shorter than 32 characters, shared by two principals, or Quasar not restarted after the change. |
| `Login unavailable` page | `Quasar:Auth:Steam:Enabled` is `false` or `DefaultProvider` is not `Steam`. |

## Hosted public access

The WebRTC development adapter uses the existing authenticated browser
session and current `CanView` policy. It does not give a native peer a Steam role
or machine API access. Hosted peers additionally need a renewable Nebula lease;
logout, permission loss or lease expiry closes access. Nebula-hosted SDK sessions select it automatically, with no separate user toggle.
Initial direct failure can use Nebula's bounded TURN fallback before any
application work runs. Established writes are never replayed, and there is no
automatic bulk HTTP fallback. Relay credentials are temporary and carry no user
or machine role; relay exhaustion remains visible. Default NAT discovery uses
`stun:rtc.se-servers.net:3478`, a DNS-only hostname separate from the proxied
dashboard. DDNS may update its public IPv4; the Nebula relay refreshes it every
minute and closes old relay allocations after a verified change. The RTC branch
also carries Blazor circuits using the same stored session and renews current
roles on reconnect after RBAC changes. Installed and native Windows acceptance
remain pending. See [direct transport](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarWebRtc.md).

If a hosted page displays its initial HTML but buttons remain inactive, check
the browser's failed RTC setup requests. A rejected peer negotiation also stops
Blazor hydration, including when opening the hosted URL on the Quasar host itself.
Keep the complete Quasar package and peer component together when updating.

**Security → Manage access setup** lets an admin enroll the installation with Nebula, which publishes it at `https://dash.se-servers.net/<panel-name>/`. Requests through that address always need a Steam sign-in with a role from `rbac.json`. They never get the trusted network bypass, and Quasar refuses them with 403 while authentication or Steam sign-in is off. Nebula platform rights, including free hosting, do not create a Quasar role. All Steam IDs mapped to `admin` can manage the panel in Nebula. See [Hosted access](HostedPanel.md).

## Sources

- `Docs/QuasarWebRtc.md`
- `Docs/QuasarWebRtcFramework.md`
- `Quasar/Services/Plugins/QuasarRtcCircuits.cs`
- `Quasar/Services/Plugins/QuasarRtcEndpoints.cs`
- `Quasar/Services/Plugins/QuasarRtcSessions.cs`
- `Quasar/Services/Plugins/QuasarPeerProcess.cs`

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
- `Quasar/Services/Auth/QuasarAuthorizationPolicyProvider.cs`
- `Quasar/Services/Plugins/QuasarOperationService.cs`
- `Quasar/Services/Plugins/QuasarSdkEndpoints.cs`
- `Quasar/Components/Shared/QuasarSdkAuthorization.razor`
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
- `Shared/Networking/MachineEndpointPolicy.cs`
- `Quasar/Services/HostedPanelRequest.cs`
- `Quasar/Components/Shared/AccessSetupDialog.razor`
- `Quasar/Services/Plugins/QuasarCoreOperations.cs`
- `Quasar/Services/Plugins/QuasarServerOperations.cs`
- `Quasar/Services/Plugins/QuasarConfigOperations.cs`
- `Quasar/Services/Plugins/QuasarPlayerOperations.cs`
- `Quasar/Services/Plugins/QuasarBackupOperations.cs`
- `Quasar/Services/Plugins/QuasarOperationService.cs`
- `Quasar/Services/Auth/QuasarAgentPrincipals.cs`
- `Quasar/Services/Auth/RbacConfig.cs`
- `Quasar/Services/Audit/QuasarAuditLog.cs`
- `Quasar/Components/Pages/Audit.razor`
- `Quasar/Components/Shared/QuasarSdkAuthorization.razor`

- `Quasar/Services/ServerWorldImportApi.cs`
- `Quasar/Components/Pages/ServerWorldImportDialog.razor`
- `Quasar/Components/Shared/DiscordSubjectMappings.razor`
- `Quasar/Services/Plugins/QuasarMyOperations.cs`
