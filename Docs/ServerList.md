# Server directory publication

Quasar sends signed listing updates directly to `https://se-servers.net/v2/listings`. Data-handling consent, diagnostic enrollment, uploaded logs and backoffice attestation are not required. Diagnostic reporting remains a separate optional feature.

## Select listings

Open **Listings**, below Discord in navigation (`/settings/server-list`). Select **Publish this server**, enter the public name, hostname/IP and game port, then **Save listings**. Keep at least one explicit Steam admin mapping in RBAC; those Steam users can edit the website description. Other identity providers and editor roles do not confer website editing authority. Viewing the page requires view permission; saving requires security-management permission.

The page discovers standalone servers and clusters and suggests names and ports. Use the cluster's public Steam gateway address, not its admin endpoint. The suggested cluster port comes from the deployed Steam configuration, otherwise it starts at 28000 for confirmation. Editing an address alone never opts in. New sources remain unselected. An earlier global `Enabled=false` remains an opt-out. The obsolete `listingGranted` data-handling field no longer controls publication.

Saved changes apply immediately without restarting. Deselect a server to omit it from the next replacement set. Deselect all servers, or remove the last Steam admin, to withdraw all listings from this installation. Names, addresses, listing IDs, website description drafts, artwork, votes and cooldowns survive withdrawal. Changing diagnostic consent does not affect publication.

The page refreshes when sources, saved settings or admins change. Source changes preserve unsaved edits. A conflicting save in another session preserves your draft and blocks overwriting; choose **Discard edits** to load the latest settings.

## Configuration and identity

The UI saves `server-list.json` in Quasar's data directory. Before its first save, `Quasar:ServerList` supplies initial defaults:

```json
{
  "Quasar": {
    "ServerList": {
      "Enabled": true,
      "Listings": [{
        "Kind": "server",
        "UniqueName": "local-server-name",
        "Name": "Public server name",
        "PublicHost": "play.example.org",
        "PublicPort": 27016
      }]
    }
  }
}
```

Use `Kind=cluster` with the cluster unique name for a cluster. Up to 100 distinct listings are supported. `ListingId` is optional; Quasar derives a stable ID from the publisher identity, source kind and case-insensitive unique name. Preserve existing assigned IDs. `DirectoryUrl` and `InstallationToken` are obsolete and ignored. No token, manual enrollment or data-handling setting is needed. Restart Quasar after restoring or externally editing `server-list.json`; UI changes apply live. Invalid saved settings disable publication instead of falling back to legacy configuration.

On first publication Quasar creates `ServerList/identity.json`, an atomic private file containing the installation ID and RSA machine key. New IDs derive from the public-key fingerprint, so another key cannot claim them. An existing diagnostic installation ID/key pair is copied locally once to preserve existing generated listing IDs; consent and the diagnostic service are not consulted. Further publication uses only the independent ServerList copy.

Keep `ServerList/identity.json` and `ServerList/epoch` together across upgrades and moves. Configuration backups include `server-list.json` but not the private ServerList directory; preserve it separately. Never run two publishers on the same local state. Corrupt saved identity fails instead of silently replacing its owner. Restore a missing identity file from backup to retain the same owner. Restoring an old epoch can cause HTTP 409; restore current ordering state rather than deleting the identity.

## Delivery and migration

Every update is a complete replacement set for this installation, authenticated by an RSA-PSS SHA256 machine signature. Increasing durable epochs and sequences reject delayed updates. Quasar sends once a minute and immediately after settings, admin or source changes. A delivered withdrawal hides listings immediately; a network outage hides them three minutes after the last update's issue time. Exact retries never extend visibility. There is no preflight challenge, enrollment request or dependency on `DiagnosticsUplinkService`.

Deploy the website's direct receiver before Quasar. Existing random installation IDs need a one-time import of their trusted public-key fingerprints from the old backoffice registry. This pins the same owners without losing pages or votes. See the [website's direct protocol and migration guide](https://github.com/CometWorks/server-list/blob/main/docs/Protocol.md). Without that import an existing publisher receives HTTP 403. Do not delete its identity or reassign its listing IDs to work around an ownership failure.

The website retains the legacy feed for older publishers during rollout. Legacy snapshots cannot modify or withdraw installations after their first direct update. This compatibility path is not used by current Quasar publication.

## Public data and checks

Public data contains the chosen name, public join action, observation time, running state, player counts and simulation speed. Private admin IDs and installation identity stay out of public responses. Raw diagnostics, world identities, private endpoints and chat are excluded.

Standalone observations require one connected agent and snapshots at most 60 seconds old; missing, ambiguous, future or stale metrics stay unknown. Simulation speed is capped at 1.00, with negative/non-finite readings unknown. Clusters publish the gateway's authoritative client count once, never a sum of nodes. The website independently expires observations after 60 seconds.

Cluster max players comes from `memberLimit` in the active deployment's saved specification, not from a node's session settings or a staged profile. It stays unknown when active deployment provenance is unavailable, the limit is invalid, or the gateway reports a different deployment revision.

Cluster simulation speed is the average of fresh, valid readings from active regular simulation nodes. World Authority, hot spares, idle nodes with paused telemetry, and nodes without a fresh unexpired lease are excluded. Each reading requires exactly one connected Agent matching the Registry's cluster, host, slot, node and epoch, a running session, and snapshot and receipt times at most 60 seconds old. Missing, ambiguous, future, stale or invalid readings are excluded; if none qualify, sim speed stays unknown. Each reading is capped at 1.00 before averaging. The public observation time is the oldest contributing snapshot or Registry status time, so polling the Registry cannot extend an older reading's lifetime.

`ServerListPublisherTests` covers direct signatures, publication without diagnostics state, live selection changes, cancellation, restart ordering, identity migration and payload exclusion. `ServerListClusterTests` covers deployed capacity, active-node sim-speed averaging, incarnation matching, freshness and topology privacy. `ServerListSettingsTests` and `ServerListingPageTests` cover persistence, discovery, permission rechecks and saving without changing data-handling consent. The `error-reporting/Tests/EndToEnd/run.sh` fixture with a server-list checkout exercises the real publisher and website before diagnostic consent and through backoffice outages. It does not start Quasar's web service.

## Public CometWorks cluster status

Configured cluster listings include `online / total nodes`, gateway phase and health,
partition/handover counts and per-node role, state, availability and quarantined
partition counts. Total counts the gateway's registered node-incarnation roster;
online requires a fresh unexpired lease and a state other than `Dead`. A missing or
stale gateway response retains the CometWorks label with unknown live counts.

Public node labels are ordinal (`Node 1`, etc.); private cluster/world/node/slot/host
identities, endpoints, executor metadata and diagnostic reason strings are excluded.
The website independently expires live cluster details after 60 seconds. Saved listing selections and Steam-admin editing rules apply. Direct updates do not
require diagnostic consent or log evidence.
Torch/Nexus publication uses the separate [Torch publisher](https://github.com/CometWorks/server-list-torch-plugin).
