# Player authentication

Quasar authenticates players through Steam and Discord, can require authorization
before joining a whitelist, assign role-based game promotions, and grant priority
joining through reserved-slot access. The existing Agent enforces standalone
policies. Compatible cluster packages enforce admission at the Gateway and
promotions at the World Authority. The feature is opt-in and grants no dashboard
permissions. It contains no tokens, credits, purchases, other rewards or join queue.

## Administrator setup

1. Update Quasar and restart game servers to load its bundled Agent. Remove the
   separate GameServerAuth plugin before enabling built-in access. Clusters need a
   package advertising `PUT /admin/v1/players/access` and World Authority promotion
   lease support. Existing published packages may lack this capability until the
   matching cluster change is released. Update the Gateway's Quasar Host too;
   cluster authentication requires a Quasar-managed Gateway and a Host advertising
   player access launch fencing.
2. Configure and enable the existing Discord bot and guild. Enable the **Server
   Members** privileged intent in the Discord Developer Portal, then enable
   **Track member and role changes for player authentication** on **Discord**.
   Without tracking, bot REST checks normally refresh membership every 30 seconds.
3. Open **Access** in the main Control plane section. The **Server access** tab
   contains server policies; **Linked accounts** lists player Steam/Discord links.
   Quasar needs outbound HTTPS to `https://auth.se-servers.net` and its existing
   Discord bot connection. The Discord flow works with a private Quasar panel,
   without Nebula hosting, a public URL or per-panel OAuth credentials.
4. Select a server or cluster. Configure whitelist, required roles, priority roles
   and promotion mappings using the role names loaded from Quasar's existing bot.
   The policy uses the bot's configured guild automatically. Saved roles remain
   visible by ID when the bot is offline or a role is unavailable. Put a recovery
   administrator's SteamID64 in the explicit Steam allowlist.
5. Enable the policy, save and check **Refresh enforcement status**. Saving does
   not prove runtime acceptance. Older Agents and Gateways report an update
   requirement. Enable cluster access only with a compatible connected Gateway.
6. Tell players to run **/authorize** in the server's Discord and select the game
   server or cluster by name. Test with a player who isn't on the explicit Steam
   allowlist before relying on the whitelist.

**Check player authorization** on Access shows each linked account's decision,
promotion, priority and validity against the saved policy.


## Optional public player website

To offer a browser entry point on Quasar as well, keep Quasar authentication and
Steam sign-in enabled with Steam as the default provider. Activate Nebula hosting
or enter and verify a self-hosted public website. The **Open player authentication**
link appears when that address is ready.

The self-hosted URL must use a public HTTPS DNS hostname on port 443, without a
path, query, fragment or credentials. Equivalent root URLs are normalized.
It is separate from the local Agent/control address; do not expose machine endpoints to make it work.

Quasar serves a short-lived installation-specific proof at
`/.well-known/quasar-player-auth`. The verifier at `auth.se-servers.net`
checks public DNS, connects to an inspected public address, validates TLS,
refuses redirects and checks the proof. Verification lasts seven
days. Enabled policies trigger renewal during the final day, retrying at most
hourly. Failure never extends the proof.

Confirmed active Nebula enrollment supplies its canonical panel address. Pending
enrollment or a saved credential file alone is insufficient. All auth callbacks,
forms and cookies retain the panel prefix.

## Player flow and migration

Run `/authorize` in the game's Discord and enter the server or cluster name. Open
the private response from Quasar's bot, check the destination and instructions,
then sign in with Steam and the same Discord account that ran the command. Discord
uses the `identify` scope. Return to Discord for the result. The link expires after
five minutes. When the bot reports that access is authorized, allow a moment for
the game server to receive the policy before joining.

If using a public Quasar player page, choose the server, sign in with Steam, then authenticate Discord
and consent to linking. The shared authentication website displays the requesting
panel before opening Discord consent. It requests only Discord's `identify`
OAuth scope, then returns to the original panel. Quasar's own bot checks guild
membership and roles; it does not need to be the shared OAuth application's bot.
Review the instructions and choose
**Authorize this server**. The page shows effective promotion and priority access.
Account linking and authorization for a particular server are separate actions.

On an open server, `!authorize` directs players to Discord if a public player page
is unavailable. With a ready public page, it supplies a 20-minute browser URL
bound to the requesting Steam account and logical server. Standalone servers open
the Steam overlay; cluster players receive the URL privately. Whitelist players
can start in Discord or on a public player page before joining.

The old `!link` and Discord `/link CODE` exchange is removed. Existing code-based
links remain visible on **Access → Linked accounts** as requiring browser authentication. They do
not identify a player for Discord assistance or grant access until both accounts
are authenticated. Each Steam and Discord account has one authenticated link;
conflicts are rejected instead of silently replacing accounts.

Players can run `/unlink-accounts` in the server's Discord or unlink on the public player page. Users with `CanControlServers`
can remove a link on **Access → Linked accounts**. Relinking creates a new authentication identity
and requires fresh server authorization. Discord administrator-to-Steam mappings
for dashboard commands remain separate.

## Policy rules

- Whitelist access requires an authenticated account pair, guild membership, any
  one required role when roles are listed, and server authorization. An empty role
  list still requires membership. Explicit Steam allowlist entries bypass these
  whitelist requirements and provide recovery access without Discord.
- Without whitelist, other players join normally. Role promotions and priority
  still require authenticated eligibility and server authorization. Steam
  allowlist members can receive these benefits after normal browser authorization.
- The highest matching promotion wins: None, Scripter, Moderator, Space Master or
  Admin. Owner cannot be assigned or demoted by this feature.
- Any priority role bypasses the normal player cap. It does not override bans,
  kick cooldowns or the player whitelist. Native password and group restrictions
  retain their normal game/Gateway behavior. No player is evicted to make room.
- Role promotions overlay the manual baseline. Removing a grant restores that
  baseline. Explicit manual roles at least as high as an active grant become the
  baseline. Lower assignments through Quasar are rejected until the Discord
  mapping is lowered or removed. Existing manual reserved entries remain.

## Freshness, restarts and recovery

Quasar checks decisions every five seconds and renews unchanged active policies
every 30 seconds. Membership-derived grants last at most two minutes, including
during outages. Unknown membership differs from a confirmed departure or missing
role. No network lookup runs on an admission path.

A stale enabled whitelist denies new joins. Infrastructure outages alone do not
mass-kick existing sessions; confirmed authorization removal can disconnect a
player. Temporary promotions and priority grants expire. Bans remain authoritative.

Standalone launch settings close a configured whitelist while the Agent waits for
its first policy. A configured whitelist refuses startup if its admission hooks
cannot be installed. Per-server marker and promotion journals are named
`.quasar-player-access-<hash>.json` and `.quasar-player-promotions-<hash>.json` under
DS AppData. The hash separates servers sharing an AppData root. Ownership history
recovers saved temporary promotions after a crash. Each journal is replaced in
one step, so a crash leaves the old or the new journal, never none. On a
filesystem that can't sync a folder, saves still succeed and the Agent logs that
once, since a journal is then less certain to survive a power cut. Do not delete
a journal to clear a role; disable the policy, wait for acknowledgement and save
the world.

Clusters persist admission beside Registry state. The Gateway Host also retains
the latest player access intent in its launch specification. It starts an enabled
whitelist closed before the first Quasar heartbeat, even with fresh or older
restored Registry state. Missing intent preserves the Host's previous setting;
older revisions cannot disable it. Launch metadata changes do not restart a live
Gateway. Only a fresh published policy removes a persisted Gateway restriction.
Hosts refuse to launch an enabled policy with a package lacking the capability.
The private Host route `PUT /host/v1/gateways/{cluster}/player-access` updates only
launch intent; it cannot start, stop or change a recovery request. Quasar derives
intent from its private policy catalog rather than accepting it from Gateway API callers.

Promotion baselines,
entitlements and expirations travel in the World Authority promotion save/journal
section, allowing a replacement authority to expire grants without Quasar online.
Do not downgrade an enabled target to a release without support. Before downgrade
or server/cluster conversion, disable access, wait for world readiness and
revocation acknowledgement, save and stop the world. Configure the destination policy separately after conversion. Missing
settings do not disable an existing whitelist. After restoring an older Quasar
settings backup, save player authentication settings once to advance its policy
revision before applying changes.

Changing a standalone server's canonical identity requires disabling its active
policy first. Display name changes do not affect authorization.

Private `player-access.json` stores settings and server authorizations. Legacy
per-panel OAuth fields are preserved for compatibility but are unused. Their
stored secrets remain encrypted with Quasar's Data Protection keys; back up those
keys and private state.
`player-links.json` stores proved identity pairs and authentication IDs. OAuth
access tokens are not retained. Configure through the UI before switching to
headless mode; player auth and Steam login continue without the administrator UI.

## Shared authentication boundary

`https://auth.se-servers.net` runs separately from Nebula hosting. The operator
configures one Discord application with callback
`https://auth.se-servers.net/discord/callback`. See
[auth service deployment](https://github.com/CometWorks/nebula/blob/main/docs/player-authentication.md).

For Discord-initiated requests, Steam and Discord return to the auth service.
Quasar retrieves the verified identity pair over outbound HTTPS using a one-time
request and a verifier kept on Quasar. The service receives the player-facing
destination and instructions. Quasar stores links, checks current guild roles,
and authorizes access for the selected server.

For the optional public player page, both OAuth legs use PKCE. The broker accepts only exact HTTPS Quasar callback
paths on port 443, including the assigned panel prefix on `dash.se-servers.net`.
It binds a one-minute, single-use authorization code to the exact callback and
the requesting panel's PKCE challenge. Quasar exchanges it over HTTPS and obtains
a short-lived identity token; Discord tokens never reach the panel. Quasar also
checks its OAuth correlation cookie, current Steam session and canonical public
address before linking. The broker never creates a Nebula login or grants panel
permissions. Only Steam identifies human Nebula accounts.

New links depend on the shared service. Existing links, server authorizations
and bot role checks remain usable if the relay is unavailable or the optional
public website's verification expires. A broker restart invalidates pending
requests. Players can start again from Discord or the public page.

## Validation and deployment

Automated checks cover OAuth session/origin binding, link conflicts and migration,
private persistence, role rules and expiry, current-server admission IL matching,
Gateway admission/authentication, promotion lease recovery, CLI contract parity
and verifier SSRF restrictions. Broker checks cover consent antiforgery, Steam
and Discord session binding, exact callbacks, PKCE, single-use and concurrent
redemption, and expiry. Before relying on a whitelist in production, verify
that a new player can authorize and join with Quasar reachable only on the LAN
and Nebula hosting disabled. Test public callbacks separately if you offer them.
