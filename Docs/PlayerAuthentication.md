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
3. Open **Security → Player authentication**. Enter the Discord application ID and
   OAuth client secret, which differs from the bot token. Register the displayed
   exact OAuth2 redirect URL in Discord:
   - Self-hosted: `https://quasar.example.com/player-auth/signin-discord`.
   - Nebula: `https://dash.se-servers.net/<panel>/player-auth/signin-discord`.
4. Save credentials. Keep Quasar authentication and Steam sign-in enabled, with
   Steam as the default provider. Either
   activate Nebula hosting, or enter and verify your public website.
5. Select a server or cluster. Configure whitelist, required roles, priority roles
   and promotion mappings. Its guild ID must match the bot. Put a recovery
   administrator's SteamID64 in the explicit Steam allowlist.
6. Enable the policy, save and check **Refresh enforcement status**. Saving does
   not prove runtime acceptance. Older Agents and Gateways report an update
   requirement. Enable cluster access only with a compatible connected Gateway.
7. Share **Open player authentication**. Whitelist users authenticate and authorize
   on the website before their first join.

**Check player authorization** on Security shows each linked account's decision,
promotion, priority and validity against the saved policy.


The self-hosted URL must use a public HTTPS DNS hostname on port 443, without a
path, query, fragment or credentials. Equivalent root URLs are normalized.
It is separate from the local Agent/control address; do not expose machine endpoints to make it work.

Quasar serves a short-lived installation-specific proof at
`/.well-known/quasar-player-auth`. Nebula's external verifier checks public DNS,
connects to an inspected public address, validates TLS, refuses redirects and
checks the proof. Self-hosting needs the matching Nebula verifier deployed, but
does not need hosting enrollment or a subscription. Verification lasts seven
days. Enabled policies trigger renewal during the final day, retrying at most
hourly. Failure never extends the proof.

Confirmed active Nebula enrollment supplies its canonical panel address. Pending
enrollment or a saved credential file alone is insufficient. All auth callbacks,
forms and cookies retain the panel prefix.

## Player flow and migration

Open the website, choose the server, sign in with Steam, then authenticate Discord
and consent to linking. Quasar requests only Discord's `identify` OAuth scope; the
bot checks guild membership and roles. Review the instructions and choose
**Authorize this server**. The page shows effective promotion and priority access.
Account linking and authorization for a particular server are separate actions.

On an open server, `!authorize` supplies a 20-minute browser URL bound to the
requesting Steam account and logical server. Standalone servers open the Steam
overlay; cluster players receive the URL privately. Another Steam identity cannot
complete it. Successful server authorization consumes the request. Whitelist
users start from the public website because they cannot join to run the command.

The old `!link` and Discord `/link CODE` exchange is removed. Existing code-based
links remain visible on **Players** as requiring browser authentication. They do
not identify a player for Discord assistance or grant access until both accounts
are authenticated. Each Steam and Discord account has one authenticated link;
conflicts are rejected instead of silently replacing accounts.

Players can unlink on their authentication page. Users with `CanControlServers`
can remove a link on **Players**. Relinking creates a new authentication identity
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
recovers saved temporary promotions after a crash. Do not delete a journal to
clear a role; disable the policy, wait for acknowledgement and save the world.

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

Private `player-access.json` stores settings and server authorizations. OAuth
secrets use Quasar's Data Protection keys; back up those keys and private state.
`player-links.json` stores proved identity pairs and authentication IDs. OAuth
access tokens are not retained. Configure through the UI before switching to
headless mode; player auth and Steam login continue without the administrator UI.

## Validation and deployment

Automated checks cover OAuth session/origin binding, link conflicts and migration,
private persistence, role rules and expiry, current-server admission IL matching,
Gateway admission/authentication, promotion lease recovery, CLI contract parity
and verifier SSRF restrictions. Live Discord OAuth, public reverse-proxy callbacks
and real player joins still require operator acceptance with matching
Quasar/Nebula/cluster builds before production reliance on the whitelist.
