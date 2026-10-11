# Server directory publication

Quasar's optional publisher sends public server statistics and a private Steam-admin roster to the logging backoffice at `https://log.se-servers.net`. The backoffice confirms fresh decryptable Quasar logs and actively pushes signed visibility updates to `https://se-servers.net`. The website stores local leases and creates listing records automatically. Independently operated community deployments use the same flow.

## Consent and eligibility

Publication requires policy-v3 diagnostic consent, the separate **Show configured servers on se-servers.net** switch in Security, at least one explicit Steam admin mapping in RBAC, and a configured public source/join address. Statistics-sharing consent and memory-dump consent remain independent. This update resets and reprompts prior decisions. Accepting YES starts diagnostic sharing and automatic enrollment, while listing visibility remains off until separately enabled. The existing combined YES/NO actions update statistics/diagnostics; listing visibility remains its own setting.

Quasar sends no listing/admin metadata before the backoffice confirms recent logs. Public data contains the chosen name, observed time, running state, player counts and simulation speed; the validated join action provides the configured public address. Raw diagnostic content and private admin/installation identities do not enter the public feed. Steam subjects are sent privately to authorize description editing.

Simulation speed is capped at **1.00** in agent metrics, incoming snapshots, analytics samples and directory publication. Valid readings from 0.00 to 1.00 are preserved; negative or non-finite readings remain unknown in directory publication. Existing analytics history is capped when loaded, including minute/hour rollups; files are updated through normal persistence/compaction. The synthetic analytics generator uses the same ceiling. This does not cap process or simulation CPU percentages.

Turning visibility off or withdrawing diagnostic consent cancels active publication and wakes the sender to withdraw. RBAC changes also wake it. A delivered withdrawal wakes the backoffice relay immediately; network outages expire the last signed website lease within one hour. No lease can outlive the last evidence's one-hour lifetime. Already displayed browser content cannot be remotely erased.

## Configuration

Open **Listings**, directly below Discord in the main navigation (`/settings/server-list`). The page discovers your standalone Magnetar servers and clusters and suggests their public names and game ports. For each server you want visible, enable **Publish this server**, enter its public hostname or IP, confirm the public game port, then choose **Save listings**. For clusters, use the Steam gateway address and port, not the Cluster Registry admin endpoint. The cluster port is suggested from its deployed Steam configuration where available; otherwise it starts at 28000 for you to confirm.

The page shows missing diagnostic consent and Steam-admin mappings, with links to Security. The global **Show configured servers on se-servers.net** switch is available on both pages. Editing destinations does not grant consent or enable the global switch. Saving requires current security-management permission. Changes cancel outdated publication attempts and wake the publisher immediately, without restarting Quasar. Deselecting a server removes it from the next replacement set; deselecting all servers withdraws the installation's listings. Names, addresses and listing IDs remain saved when deselected. Conflicting edits from another session require reloading before saving.

UI settings are stored in `server-list.json` in Quasar's application-data directory. Preserve that file with the diagnostic identity and publication epoch. Existing `Quasar:ServerList` settings below are used as initial defaults only while this file is absent; the first UI save takes precedence across restarts and upgrades. Invalid saved settings disable publication rather than falling back to legacy configuration. New sources remain unselected until explicitly configured. Use **Reload** to refresh sources or discard unsaved edits.

Configuration backups include `server-list.json`. Restart Quasar after restoring a backup to load the restored listing settings.

For headless deployments, the existing `appsettings.json` configuration remains supported:

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

Enable the separate visibility switch after reviewing this configuration. No website token, installation ID or directory endpoint must be configured. The diagnostics uplink negotiates the credential and routes listing requests to its backoffice. `DirectoryUrl`/`InstallationToken` are legacy configuration fields ignored by the production publisher.

`ListingId` is optional. If absent, Quasar derives a stable ID from its installation identity, kind and case-insensitive source UniqueName. Preserve an existing assigned `ListingId` during migration to retain edited pages/votes. Preserve `Diagnostics/installation-id`, `Diagnostics/machine-key.pem` and `ServerList/epoch` across upgrades; restarting reserves a higher durable epoch before sending. Never run two publishers over the same local state or restore ordering state backwards.

Use `Kind=cluster` with the cluster unique name and its **public Steam gateway address**. The gateway's authoritative connected-client count is published once; node counts are not summed. Cluster max-player count and simulation speed remain unknown. Standalone statistics require one connected agent and observations at most 60 seconds old. Missing, ambiguous, future or stale observations stay unknown. Deleted sources are omitted from the replacement set. Legacy configuration and external edits to `server-list.json` are read at startup; UI changes apply live. `Enabled=false` withdraws on the next publication cycle. Use the visibility switch to withdraw promptly while online.

## Protocol and verification

The negotiated diagnostic credential authenticates `/v1/quasar/challenge`, `/publication` and `/withdraw` on the backoffice with `X-Installation-Id`. Challenge is a fresh-evidence preflight; publication rechecks evidence and replaces the complete listings/admin roster. Durable increasing source epochs/sequences reject delayed publications. Withdrawal retains a tombstone and needs no evidence. The backoffice pushes complete RSA-PSS-signed snapshots immediately on changes and every 30 seconds. Website leases last at most one hour and are capped by one-hour evidence/publication expiry. The website fetches only the trusted backoffice's public key; it consumes no per-instance proof and performs no evidence calls during browsing, editing, voting or joining.

Only current explicit Steam **admin** mappings authorize description editing for the linked instance. Quasar editors, trusted-network identities and other installations inherit no directory authority. Writes retain normal CSRF/concurrency checks. Description drafts and votes survive withdrawal; see server-list's schema-3 backup/rollback instructions.

`ServerListPublisherTests` exercises consent, separate visibility, live selection changes, cancellation, admin changes, ordering, public payload exclusion and observation handling. `ServerListSettingsTests` covers persistence, legacy configuration migration, validation, conflicting edits and source discovery; `ServerListingPageTests` exercises both server kinds, saving and permission rechecks. The cross-repository `error-reporting/Tests/EndToEnd/run.sh` with a server-list checkout runs actual backend/website fixtures and the real uploader/publisher. It never starts Quasar's web service.

Ordinary log delivery is batched every ten minutes after the first available batch. Server metadata/heartbeat publication remains every minute. Evidence must still be a nonempty, authenticated, consented Quasar log batch; a one-hour eligibility window tolerates ordinary delivery gaps. Deploy the website, backoffice and Quasar updates in that order. The two-minute Host sharing consent lease is independent and unchanged.

## Public CometWorks cluster status

Configured cluster listings include `online / total nodes`, gateway phase and health,
partition/handover counts and per-node role, state, availability and quarantined
partition counts. Total counts the gateway's registered node-incarnation roster;
online requires a fresh unexpired lease and a state other than `Dead`. A missing or
stale gateway response retains the CometWorks label with unknown live counts.

Public node labels are ordinal (`Node 1`, etc.); private cluster/world/node/slot/host
identities, endpoints, executor metadata and diagnostic reason strings are excluded.
The website independently expires live cluster details after 60 seconds. Existing
listing consent, verified Quasar log evidence and Steam-admin editing rules remain.
Deploy the updated website and backoffice before enabling this extended publication.
Torch/Nexus publication uses the separate [Torch publisher](https://github.com/CometWorks/server-list-torch-plugin).
