# Browser SDK v1 rollout

**Prepared candidate, not a deployed release.** Ship the SDK using the current
HTTP transport. WebRTC follows in a separate release. Public panel URLs stay
`https://dash.se-servers.net/<panel-id>/`; this SDK release does not reduce the
traffic carried by Nebula/frp. Local access and companion background collection
remain independent of Nebula.

## Candidate and ordering

- Quasar base version **1.6.0** is the first release with the browser SDK v1
  contract. The release workflow appends its normal fourth build component.
- Entity Viewer **0.1.13** requires **Quasar >=1.6.0**, with
  `browserSdkVersion: 1`. Publish its exact source pin through `quasar-hub` only
  after the Quasar release and canary acceptance. Keep implicit installation off.
- The viewer includes upstream security fix `4eb0fd0`. Asset sessions originate
  from authorized companion scene/history data, never a browser mod list.
  Fifteen safe operations remain; browser-selected session creation is removed.
  Scene/history operations return `{ data, assetSession }`; update the adapter
  and browser assets together. Rendering payloads and panel path prefixes stay
  unchanged. Old direct clients must adopt that envelope.
- Existing UI plugins can retain their current registration/HTTP routes. The
  new viewer requires its matching Quasar contract assembly. Don't publish the
  catalog update to make older Quasar installations discover an unbuildable pin.

Quasar `main` merges automatically publish stable platform releases and the
container `latest` tag. A main merge is therefore a release action. Keep the PR
in draft until prepublication gates are signed off. Branch `workflow_dispatch`
produces a draft prerelease; it does not authorize production activation.

## Before publication

1. Review the SDK, viewer security merge, release notes and catalog minimum/pin.
   SDK-only work needs no Nebula manager/gateway deployment or new port/firewall
   rules. Never expose management/frps/Caddy listeners for this rollout.
2. Build Linux and Windows platform candidates from the reviewed source SHA.
   CI uses released Magnetar SDK **v2.4.3.9**, which contains the command-alias
   interface required to compile the complete test project. Do not use the older
   local SDK or a source selector to claim full-project compilation.
3. Run the selected SDK/core/RBAC, hosted-prefix, machine/trusted-network and
   console checks, browser suites, viewer streaming/replay/asset-boundary checks,
   preview/UI checks and package guards. Both platform archives must contain
   `wwwroot/_quasar/sdk/v1/client.js` and `wwwroot/quasar-sdk.js`. Retain source
   SHAs, workflow runs and SHA256SUMS with the candidate.
4. Back up the target installation's data/config using its established backup
   procedure. Include `rbac.json`, data-protection keys, plugin configuration,
   installed plugin pins, hosted-panel identity and native updater configuration.
   Keep these private. Verify a usable previous package and recovery procedure.
5. Review existing `policyOverrides` in `rbac.json`: older Quasar stored them
   without enforcing them; this release enforces them. Resolve unintended role
   grants before activation. Missing/empty/unknown SDK rules deny; restore a valid
   file if malformed. Don't weaken machine policies to make a test pass.
6. Record the canary's current versions, automatic application/plugin-update
   settings and expected permissions. Disable automatic application/plugin updates
   for the controlled canary window, then restore its chosen policy afterward.
   Legacy installs must complete/start full Quasar 1.4.1 migration before 1.6.

## Canary acceptance

Use explicitly authorized isolated installs, one Linux and one native Windows.
Preparing this candidate does not launch or update the user's Quasar service.
A test fixture passing is not evidence of real Steam or installed-plugin behavior.

1. Install the reviewed Quasar candidate through the existing native installer
   and updater process. Verify its exact version and healthy local UI. Leave the
   old viewer installed initially and confirm it still loads.
2. Install the pinned viewer candidate together with its browser assets and
   companion; restart Quasar/plugin hosts through their existing controls.
   Close old viewer windows and reload the shell to replace cached modules.
3. Use a real Steam administrator and a separate viewer account. Test local and
   hosted prefixed URLs: scene load, streaming consent, roots/installer controls,
   exact model/texture bytes, large 64-bit entity IDs, replay queries and recording
   changes. Viewers must remain denied admin-only paths; anonymous and machine
   credentials must not grant browser SDK access. No private paths appear in
   ordinary viewer status. Local-folder/preview fallback still works.
4. Try a guessed mod absent from the actual scene and a foreign user's token.
   Both must fail. Check byte/session/rate limits, unsupported archive ranges and
   cancellation while a download remains unread. The unsafe session-creation
   endpoint/operation must remain absent. Never log grants or tokens as evidence.
5. Change a registered operation policy/disablement from Security and verify both
   SDK and adopted legacy URLs follow it. Confirm active streams stop after
   logout, expiry, policy loss or consent removal. Recheck uploads with an isolated
   fixture: interrupted/oversized bodies leave no feature commit or partial file.
6. Test browser back/forward restoration and closing a viewer during transfer.
   Background recording must continue with all browser views closed. Suspend
   hosting or make Nebula unavailable in the test environment; normal local UI
   and companion collection must continue.
7. Record CPU/memory, concurrent download responsiveness and network bytes during
   this canary. Expect HTTP payloads to continue through Nebula. Mark WebRTC and
   SignalR migration out of scope; don't claim direct connections are live.

## Promotion

After Linux/Windows candidate CI and installed canaries pass, release Quasar first.
Verify downloadable Linux/Windows archives, checksum file, exact release identity
and existing 1.4.1 migration asset retention. The existing container workflow also
updates `latest`; account for installations that follow that tag.

Then publish/merge the exact viewer source and the `quasar-hub` catalog pin with
minimum `>=1.6.0`. The candidate pin must exist in the public source repository
before the catalog is merged. Keep `ImplicitLoading=false`. Upgrade the first
small group, observe the same checks, then expand. Record failures before changing
policies or transport; SDK auth errors must not trigger a legacy bypass.

## Rollback

Stop promotion on permission regressions, foreign-resource access, path disclosure,
partial writes, unresolved installer failures or broken local/offline behavior.

1. Stop further catalog rollout and disable the new viewer in affected installs
   using UI Plugins, then restart via the existing control. Quasar 1.6 can remain
   installed while the plugin issue is investigated. Preserve private evidence.
2. For a viewer rollback, use reviewed upstream security revision
   `4eb0fd01a4b21fcc26c337e0cc89498d1f219a48`, not the older catalog pin that lacks
   the session/resource fix. Restore its matching adapter/browser assets/companion
   together and refresh the shell. Verify old history/settings remain usable.
3. If Quasar itself fails activation, use the native updater's documented recovery
   and retained previous application/config; see [status and recovery](QuasarUpdates.md#durable-status-and-recovery).
   Don't overwrite a running executable or edit updater journals by hand. Review
   the private preupgrade `rbac.json` snapshot before restoring it: post-upgrade
   rule edits may differ, and old versions don't enforce the same overrides.
4. Confirm local permissions, game processes, worlds, background collection and
   existing public panel identity. Re-enable only the agreed update policy after
   the installation is healthy. A rollback does not rotate or publish credentials.

No database/world conversion is introduced by SDK v1. Never restore live world
files merely to reverse this UI/SDK change.
