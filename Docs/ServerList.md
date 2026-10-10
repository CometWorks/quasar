# Server directory publication

Quasar's optional publisher sends public server statistics and a private Steam-admin roster to the logging backoffice at `https://log.se-servers.net`. The backoffice confirms fresh decryptable Quasar logs and actively pushes signed visibility updates to `https://se-servers.net`. The website stores local leases and creates listing records automatically. Independently operated community deployments use the same flow.

## Consent and eligibility

Publication requires policy-v2 diagnostic consent, the separate **Show configured servers on se-servers.net** switch in Security, at least one explicit Steam admin mapping in RBAC, and a configured public source/join address. Statistics-sharing consent and memory-dump consent remain independent. Legacy statistics YES never grants diagnostic or listing permission. The existing combined YES/NO actions update statistics/diagnostics; listing visibility remains its own setting.

Quasar sends no listing/admin metadata before the backoffice confirms recent logs. Public data contains the chosen name, observed time, running state, player counts and simulation speed; the validated join action provides the configured public address. Raw diagnostic content and private admin/installation identities do not enter the public feed. Steam subjects are sent privately to authorize description editing.

Turning visibility off or withdrawing diagnostic consent cancels active publication and wakes the sender to withdraw. RBAC changes also wake it. A delivered withdrawal wakes the backoffice relay immediately; network outages expire the last signed website lease within 60 seconds. No lease can outlive the last evidence's two-minute lifetime. Already displayed browser content cannot be remotely erased.

## Configuration

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

Enable the separate Security visibility switch after reviewing this configuration. No website token, installation ID or directory endpoint must be configured. The diagnostics uplink negotiates the credential and routes listing requests to its backoffice. `DirectoryUrl`/`InstallationToken` are legacy configuration fields ignored by the production publisher.

`ListingId` is optional. If absent, Quasar derives a stable ID from its installation identity, kind and case-insensitive source UniqueName. Preserve an existing assigned `ListingId` during migration to retain edited pages/votes. Preserve `Diagnostics/installation-id`, `Diagnostics/machine-key.pem` and `ServerList/epoch` across upgrades; restarting reserves a higher durable epoch before sending. Never run two publishers over the same local state or restore ordering state backwards.

Use `Kind=cluster` with the cluster unique name and its **public Steam gateway address**. The gateway's authoritative connected-client count is published once; node counts are not summed. Cluster max-player count and simulation speed remain unknown. Standalone statistics require one connected agent and observations at most 60 seconds old. Missing, ambiguous, future or stale observations stay unknown. Deleted sources are omitted from the replacement set. Configuration is read at startup. `Enabled=false` stops renewal; use the visibility switch first to withdraw promptly while online.

## Protocol and verification

The negotiated diagnostic credential authenticates `/v1/quasar/challenge`, `/publication` and `/withdraw` on the backoffice with `X-Installation-Id`. Challenge is a fresh-evidence preflight; publication rechecks evidence and replaces the complete listings/admin roster. Durable increasing source epochs/sequences reject delayed publications. Withdrawal retains a tombstone and needs no evidence. The backoffice pushes complete RSA-PSS-signed snapshots immediately on changes and every 30 seconds. Website leases last at most 60 seconds and are capped by evidence/publication expiry. The website fetches only the trusted backoffice's public key; it consumes no per-instance proof and performs no evidence calls during browsing, editing, voting or joining.

Only current explicit Steam **admin** mappings authorize description editing for the linked instance. Quasar editors, trusted-network identities and other installations inherit no directory authority. Writes retain normal CSRF/concurrency checks. Description drafts and votes survive withdrawal; see server-list's schema-3 backup/rollback instructions.

`ServerListPublisherTests` exercises consent, separate visibility, cancellation, admin changes, ordering, public payload exclusion and observation handling. The cross-repository `error-reporting/Tests/EndToEnd/run.sh` with a server-list checkout runs actual backend/website fixtures and the real uploader/publisher. It never starts Quasar's web service.
