# Diagnostics, error reporting and server listing

This page covers what Quasar records about its own errors and crashes, the consent choices that control what leaves the machine, where the evidence is stored, and the optional public server listing on se-servers.net. For reading ordinary log files when something goes wrong, see [Troubleshooting](Troubleshooting.md).

## Overview

- Quasar always captures its own errors and process crashes locally. Capture does not depend on consent.
- Diagnostic reports are uploaded only after diagnostic consent. Public listings have their own saved publish selections and do not depend on that consent. All choices are off by default.
- Quasar is the only process that talks to the diagnostics backoffice. Enrolled [Quasar.Host](ClusterHosts.md) machines hand their encrypted reports to Quasar over their existing authenticated connection, and Quasar uploads them.
- The default backoffice is `https://log.se-servers.net/`, run by CometWorks.
- Reports are encrypted on the machine that captured them with the backoffice's public key. Quasar cannot decrypt them.

## Consent choices

Consent lives in the **Data Handling Consent** section of **Security** (`/settings/security`, needs the security management permission, see [Access control](AccessControl.md)). The **Dashboard** also shows a **Data Handling Consent** prompt with YES and NO buttons while no decision is stored, or while statistics consent is on but diagnostic reports are off.

The Dashboard prompt keeps its text and buttons inside the standard panel padding;
the buttons wrap below the text when space is limited.

| Choice | Default | What it controls |
| --- | --- | --- |
| **YES** / **NO** buttons | no choice | YES turns on plugin usage statistics and diagnostic reports, and starts automatic enrollment with the backoffice. It does not turn on crash dumps or listings. NO turns off statistics, diagnostic reports and crash dumps. |
| **Send diagnostic reports** switch | off | Encrypted error reports and batched application logs from Quasar and all enrolled hosts. |
| **Include crash dumps** switch | off | Process memory dumps attached to crash reports. Only available while diagnostic reports are on. |
| **Publish this server** on Listings | off for new servers | Per-server public listing. Select and save to opt in; there is no global listing switch. See [Server listing](#server-listing-on-se-serversnet). |

The status chip next to the heading shows `YES`, `NO`, `Logging consent required` (statistics on, reports off) or `No choice stored`.

### Plugin usage statistics

The statistics choice goes to Magnetar when a server starts. Quasar adds `-consent accept` or `-consent deny` to the Magnetar launch arguments (Magnetar older than 2.3.3.0 gets `-consent` or `-noconsent`). No stored decision counts as deny. Quasar removes any `-consent`, `-noconsent` or `-withdraw-consent` that an admin put into the server's own launch arguments, so the Security page is the only place to set it.

Running servers keep their current statistics setting until they restart. The UI says: "Running servers keep their current Magnetar consent state until they are restarted."

### What turning a choice off does

- Turning diagnostic reports or crash dumps on or off starts a new consent generation. Reports captured before the change can never be uploaded under the new choice, including queued ones. Only incidents captured after enabling are eligible.
- Turning diagnostic reports or crash dumps off cancels active transfers and deletes queued reports.
- Already delivered reports cannot be recalled.
- Hosts get a two-minute consent lease, renewed every 30 seconds for the connected ones. An offline host stops sharing when its lease expires and gets a new one at the first renewal after it connects.
- A crash dump setting applies to a process from its next start. Consent still gates packaging and upload of any dump already on disk.

### Where consent is stored

`data-handling-consent.json` in the Quasar data directory. The Security page shows the full path. Quasar watches the file and reloads it when it changes on disk. Decisions stored under an older policy version are reset, so admins get the prompt again after upgrading to a version with a new policy (the current policy version is 3).

## CometWorks announcements

Current-policy **YES** and enabled **Diagnostic reports** also allow Quasar to
check CometWorks announcements every five minutes, with 30 seconds of jitter.
They appear in the existing bell and Notifications page, including for operators
of linked Hosts in this central UI. There is no Host relay, game broadcast,
command or automatic restart. All modes poll while consent permits it. Headless
Quasar writes notices to its existing service logs with Information, Warning or
Critical severity; it does not register browser notification services.

Turning YES or diagnostic reports off cancels pending fetches and push delivery,
clears displayed notices, and stops network polling. The backend only knows
enrollment, not current local consent. Successful snapshots remove withdrawn
notices; notices also disappear at expiry or six minutes after the last successful
fetch. An independent one-second timer invalidates idle UIs even while a network
fetch is stalled or fails. A restart preserves active display only within that deadline and under
the same consent. Browser push uses existing subscriptions, with one durable
attempt per browser and announcement ID; failed attempts can be missed and
already accepted pushes cannot be recalled. Browser admission stops at 4,096 IDs
per subscription without evicting accepted history. Protected browser state has
receipt-count and 8 MiB representation bounds. Invalid/oversized state is kept
for repair; the bell/page remain available and readable legacy state can still
support ordinary push with session-only receipts. See [Monitoring](Monitoring.md#notifications)
and [the delivery contract](https://github.com/CometWorks/quasar/blob/main/Docs/Announcements.md).

Headless log attempts are deduplicated in `Announcements.log-receipts.json`, even
across restart, regrant and withdrawal. Receipts are reserved before logging, so
an interrupted attempt can be missed. The file retains up to 4,096 IDs until their
original exclusive expiry and is bounded to 1 MiB. At capacity or on invalid or
unwritable state, delivery pauses rather than losing live deduplication history.
Expired receipts free space; deliberately reused IDs after expiry can log again.
Only a current, nonexpired snapshot within its six-minute freshness window can be
logged. Text is structured and line breaks are escaped. Already written service
logs cannot be recalled. See the delivery contract for failure/recovery limits.

## What is collected

| Source | Captured locally | Uploaded with diagnostic consent |
| --- | --- | --- |
| Quasar's own Error and Critical log messages | Yes, as incident files | Yes |
| Quasar fatal (unhandled) exceptions | Yes, written immediately | Yes |
| Exit of Dedicated Servers started by Quasar and of Host-managed processes | Yes, with exit code and whether the stop was expected | Yes |
| Hang evidence of a cluster node a Host kills for hanging | Yes, see [Hung cluster nodes](#hung-cluster-nodes) | Yes; the minidump only with **Include crash dumps** |
| Standard output of supervised processes (batched application logs) | Only while consent is on | Yes, as log batches |
| Recent process log attached to an incident | Yes | Yes, only for runs that started after consent |
| Crash dumps | Only for processes started while dump consent was on | Only with **Include crash dumps** |

Details:

- Quasar keeps its normal `quasar.log` separate. The diagnostics capture only listens to Error and Critical, so ordinary messages are not duplicated into reports.
- Each Quasar run keeps at most 256 incident files and accepts at most 32 ordinary error reports per minute. Fatal exceptions bypass the limit. Messages are truncated (8,192 characters for the message, 32,768 for the exception text).
- Reports contain error details, recent logs, software versions, and host, cluster and server identifiers. Logs can contain player names, chat and other private data. Crash dumps can contain process memory, credentials, player data and world content.
- Log batches from all running processes are delivered every ten minutes. The first batch after startup or after a new consent goes out right away. Error and crash reports go out on every poll.
- Quasar records its own process as a run too, with its fatal exceptions and a fatal exit. Since 1.5 Quasar runs as the service itself rather than under a launcher, so nothing starts it with the dump settings below and its own crashes leave no CoreCLR dump.
- Crash dumps: .NET processes that Quasar or a Host starts get a full CoreCLR dump in their run folder. On Linux, native crashes are imported from systemd-coredump (`coredumpctl`) when it is installed and readable by the service account. On Windows x64, the process that starts a server or node attaches a crash monitor that writes a minidump on an unhandled exception. Quasar does not change system-wide core dump or Windows Error Reporting settings. Dumps over 2 GiB stay local and the report says so.

## Upload and queues

- Quasar polls its own collector and every connected host every 30 seconds, and immediately after a consent change. A host that is not connected is skipped, so polling never starts the local Host; its reports wait there until it connects.
- Each report is copied into Quasar's outbound queue, checked against its declared length and SHA-256, and only then acknowledged to the source. The source deletes it after the acknowledgment.
- Uploads use `PUT /v1/reports/{reportId}` on the backoffice. A failed upload stays queued and retries with the same report ID.
- If the backoffice rejects one report (HTTP 400, 409, 413, 415, 422 or 507), Quasar keeps it and moves on to the next one.
- A corrupt archive in the queue is deleted with the warning `Discarded corrupt diagnostic archive {ReportId}.`

| Queue | Limit |
| --- | --- |
| Quasar outbound queue (`outbound/`) | 128 reports, 8 GiB, 7 days |
| Sealed reports waiting on a machine (`incoming/`) | 256 reports, 8 GiB, 7 days. Log batches can fill at most 192 of the 256 slots so incidents always have room. |
| Finished process runs (`runs/`) | Kept 7 days, cleanup target 8 GiB. A running process keeps its run folder until it exits. |

Collection pauses when free disk space on the storage drive is less than the estimated archive size plus 1 GiB.

## Enrollment and identity

After diagnostic consent, Quasar enrolls itself automatically. No token or installation ID needs configuring.

- `Diagnostics/installation-id` is a random ID for this Quasar installation. It contains no user account identity.
- `Diagnostics/machine-key.pem` is a 3072-bit RSA private key that Quasar creates on first enrollment. It never leaves the machine. The backoffice binds the installation ID to this key.
- `Diagnostics/policy.json` holds the current consent lease for the local collector.

Keep the whole `Diagnostics` folder (and the separate `ServerList/identity.json` and `ServerList/epoch`, see below) when moving or upgrading Quasar. [Configuration backups](Backups.md) include `data-handling-consent.json` and `server-list.json` but not the `Diagnostics` or `ServerList` folders.

## Settings

| Setting key | Environment variable | Default | Purpose |
| --- | --- | --- | --- |
| `Quasar:Diagnostics:BackofficeUrl` | `Quasar__Diagnostics__BackofficeUrl` | `https://log.se-servers.net/` | Backoffice URL. Must be HTTPS (plain HTTP only on loopback). No user info, query string or fragment. An explicitly empty or invalid value disables uploads. |
| `Quasar:Diagnostics:UploadToken` | `Quasar__Diagnostics__UploadToken` | none | Optional legacy fixed credential. Leave unset for automatic enrollment. |
| `Quasar:Diagnostics:StorageDirectory` | `QUASAR_DIAGNOSTICS_DIRECTORY` (wins) or `Quasar__Diagnostics__StorageDirectory` | `<Quasar data directory>/Diagnostics` | Where `runs/`, `incoming/` and `outbound/` live. Must be an absolute path and not a filesystem root. |

An invalid backoffice URL logs `Diagnostic uplink disabled: configure an HTTPS backoffice URL (or loopback HTTP).` at startup.

To disable uploads entirely without touching consent, set `Quasar:Diagnostics:BackofficeUrl` to an empty string. Local capture continues either way.

### Storage on a separate disk

Set `QUASAR_DIAGNOSTICS_DIRECTORY` to an absolute path in the Quasar service's environment, or `Quasar:Diagnostics:StorageDirectory` in `appsettings.json`. The variable wins. `installation-id`, `machine-key.pem`, `policy.json` and the consent file stay in their normal locations.

Quasar does not move existing evidence when the directory changes. To move it: stop Quasar and its servers, copy `runs/`, `incoming/` and `outbound/` to the new directory keeping permissions, set the variable, start again.

On Unix, Quasar sets the state directory and private key to owner-only permissions.

For Docker, `docker-compose.diagnostics.yml` is an optional override that bind-mounts an existing host directory at `/diagnostics` and sets `QUASAR_DIAGNOSTICS_DIRECTORY=/diagnostics` and `QUASAR_LOG_DIR=/diagnostics/application`:

```bash
QUASAR_DIAGNOSTICS_HOST_DIRECTORY=/mnt/diagnostics/quasar \
  docker compose -f docker-compose.yml -f docker-compose.diagnostics.yml up -d
```

The host directory must already exist. See [Docker](Docker.md).

### Quasar.Host machines

Each Host captures its own processes into `<stateDirectory>/diagnostics` by default. Override it with `diagnosticsDirectory` in the Host's JSON configuration (a relative path resolves against the config file's folder) or with the `QUASAR_HOST_DIAGNOSTICS_DIRECTORY` environment variable, which wins. See [Cluster hosts](ClusterHosts.md).

### Hung cluster nodes

Before a Host kills a cluster node for hanging, it collects evidence. That happens when the Cluster Registry declared the node dead, and when the node missed its startup deadline. A planned stop, an operator's kill or an emergency shutdown collects nothing.

- The Host writes `hang-evidence.txt` in the node's run folder under `runs/` and names the file in its log.
- On Linux the file has each thread's state, kernel wait channel, CPU use over one second and stack, plus the process memory and the machine load. Stacks come from the `createdump` next to the node's runtime and hold method names only, so they need no dump consent. If the OS refuses the tool, as Ubuntu's default `kernel.yama.ptrace_scope=1` does, the Host asks the node's runtime to run it. A runtime frozen as a whole can't answer, and then the file has thread states only.
- With **Include crash dumps** on, a minidump (`hang.dmp`, under 2 GiB, with enough free disk) is kept as well.
- The incident report is `node_hung` or `ready_timeout`. It carries the start of the text and the whole file as `log-002.txt`, and the minidump only under dump consent.
- The capture runs in the background for at most 20 seconds while the slot reports `Spawning`, then the kill follows with whatever it got.
- On other operating systems the file and the report only say that the node hung.

## Server listing on se-servers.net

Quasar publishes standalone Magnetar servers and clusters directly to se-servers.net. Each update is signed by this installation's own machine key. There is no backoffice challenge, diagnostics enrollment or log-attestation step.

### Requirements

1. Select and save **Publish this server** on Listings.
2. Add at least one explicit Steam Admin mapping in Security, using a 17-digit SteamID64.
3. Configure the public hostname/IP and game port on the selected source.

Data-handling consent, diagnostic reports and crash dumps are independent. Turning them off does not hide listings. Removing the last Steam admin or deselecting every server sends a withdrawal.

### Configure listings

1. Open **Listings** in the main navigation (`/settings/server-list`). The page is titled **Server listing**. Viewing needs the view permission; saving needs the security management permission.
2. The page lists every standalone server and cluster. Names and game ports are pre-filled from the server's in-game name and server port. For a cluster, the port comes from its deployed Steam gateway configuration, or 28000 if that is unknown.
3. For each server to publish, tick **Publish this server**, check **Public server name** (max 128 characters), enter **Public hostname or IP address** (no scheme, no port) and confirm **Public game port**. For a cluster use the Steam gateway address and port, not the Cluster Registry admin endpoint.
4. Click **Save listings**. This records publication consent for the selected servers. Changes apply without a restart.

The page refreshes automatically as servers, clusters, saved settings or Steam-admin mappings change. Source changes preserve unsaved edits. If another session saves settings while you have edits, a warning preserves your draft and blocks saving over the newer settings. Choose **Discard edits** in that warning to load the latest settings. There is no Reload button. **Save listings** sits in a rounded, bordered action panel below the cards, aligned with the listing grid.

Unticking a server removes it from the next publication. Unticking all servers withdraws the installation's listings. Names, addresses and listing IDs stay saved.

An earlier global `Enabled=false` keeps servers unselected until selected and saved again. The old data-handling `listingGranted` flag is ignored. Saving a publish selection does not change diagnostic or statistics consent.

The page warns about missing Steam admin mappings, invalid settings and sources that no longer exist. Diagnostic consent is not a publication requirement.

### What is public

- Public: listing name, public host and port, observation time, running state, players online, max players, and simulation speed (capped at 1.00).
- Clusters: connected client count from the gateway, max players from the active deployment's admission limit, and average sim speed across fresh active simulation-node readings. World Authority, hot spares and idle nodes are excluded from the average. Also includes online and total node counts, gateway phase and health, partition and handover counts, and per-node role, state and quarantined partition count. Nodes appear as `Node 1`, `Node 2` and so on.
- Private (sent to the directory, excluded from public responses): the Steam admin SteamIDs. They let those admins edit the listing description on se-servers.net. Quasar roles other than Steam Admin mappings give no rights on the website.

A standalone server's statistics are sent only when exactly one connected agent reports it and the snapshot is at most 60 seconds old. Otherwise the listing shows unknown values.

For clusters, max players stays unknown if the active deployment's saved specification is unavailable or invalid, or the gateway reports a different deployment revision. Editing a profile or staging an update does not change the public limit before activation. Sim speed excludes stale, disconnected, ambiguous and invalid readings and stays unknown if no eligible node reports. Node readings must match the gateway's current node identity and epoch, with fresh snapshots and leases. The observation time includes the oldest contributing snapshot so older readings expire normally.

### Publication timing and withdrawal

- Quasar publishes every minute, and immediately after a change to listing settings, role mappings, servers or clusters.
- Deselecting all servers and saving, or removing the last Steam admin mapping makes Quasar send a withdrawal on the next cycle, which runs immediately.
- Each publication carries an increasing epoch and sequence number. Quasar stores the epoch in `ServerList/epoch` and raises it at every start, so a delayed older update never overwrites a newer one. Do not run two Quasar instances over the same data directory.
- The website expires listings three minutes after the last signed update. Observed live statistics expire separately after 60 seconds.
- Keep the private `ServerList/identity.json` with `ServerList/epoch`. The identity file stores this publisher's ID and machine key atomically. Fresh publishers need no diagnostics files. Existing diagnostic ID/key pairs are adopted locally once to preserve old listing IDs.
- Deploy the direct website receiver first and import existing publisher public-key fingerprints as described in `Docs/ServerList.md`. This preserves ownership, edited pages and votes. Old backoffice snapshots cannot overwrite publishers after they switch.


### Listing settings file

The Listings page saves to `server-list.json` in the Quasar data directory. While that file does not exist, Quasar takes initial values from `Quasar:ServerList` in `appsettings.json`, which is useful for headless installs:

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

- `Kind` is `server` or `cluster`. `UniqueName` is the server's or cluster's unique name. `PublicPort` defaults to 27016.
- `ListingId` is optional. Without it, Quasar derives a stable ID from the installation ID, kind and unique name. Keep an existing `ListingId` to keep the page's edited description and votes.
- Up to 100 listings. `PublicHost` cannot be `localhost`, `0.0.0.0`, `::` or a loopback IP.
- `DirectoryUrl` and `InstallationToken` are ignored.
- Selecting **Publish this server** and saving is sufficient. Headless configuration uses `Enabled` and the selected `Listings`; no data-handling flag is required.

After the first save in the UI, `server-list.json` wins over `appsettings.json`. Restart Quasar after restoring `server-list.json` from a backup or editing it by hand.

## Common problems

- **`Failed to bind to address ...: address already in use.`** Another process is using the configured port. Stop that process or change Quasar's listening address/port. Quasar still fails startup and keeps the error in local logs and diagnostic files. The collector excludes this failure and its matching fatal-exit incident from exception uploads. Ordinary diagnostic log batches continue, so their console text can still include the error. Other bind errors and unrelated crashes still report normally. Archives already sealed by an older version remain queued.
- **The Listings page says "Saved listing settings could not be loaded. Publishing is disabled until you save valid settings."** `server-list.json` is unreadable or invalid. Quasar does not fall back to `appsettings.json`. Fix the entries on the page and save.
- **"Listing settings changed in another session."** Someone else saved in the meantime. The page keeps your edits but blocks saving. Click **Discard edits** to load the latest settings, then redo the edit.
- **"Invalid server list identity, source, name or public join address."** A selected listing has an empty name, a name over 128 characters, a port outside 1 to 65535, or a host that is not a valid hostname or IP, or is localhost or loopback.
- **"You no longer have permission to manage server listings."** The signed-in account lacks the security management permission.
- **The server is selected but not on se-servers.net.** Check the three requirements above and `Server list update deferred` warnings. HTTP 403 for a migrated installation usually means its trusted publisher key has not been imported on the directory. HTTP 409 means stale ordering state. Keep the identity and restore current state; diagnostic delivery is unrelated.
- **Log warnings `Diagnostic report from Host {HostId} will retry` or `Diagnostic transfer from Host {HostId} will retry`.** Quasar could not fetch reports from that host. They stay on the host and Quasar retries on the next poll.
- **`Invalid publisher identity.`** The private `ServerList/identity.json` is invalid. Restore its matching backup instead of creating a different owner.

## Sources
- `Docs/DiagnosticsUplink.md`
- `Docs/ServerList.md`
- `Docs/Docker.md`
- `Quasar/Services/DiagnosticsUplinkService.cs`
- `Quasar/Services/QuasarDiagnosticLoggerProvider.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/ServerList/ServerListOptions.cs`
- `Quasar/Services/ServerList/ServerListProtocol.cs`
- `Quasar/Services/ServerList/ServerListPublisher.cs`
- `Quasar/Services/ServerList/ServerListSettingsCatalog.cs`
- `Quasar/Services/ClusterOverview.cs`
- `Quasar/Services/ClusterFleetService.cs`
- `Quasar/Services/Backup/QuasarBackupService.cs`
- `Quasar/Program.cs`
- `Quasar/Components/Pages/Security.razor`
- `Quasar/Components/Pages/ServerListing.razor`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar.Host/Program.cs`
- `Quasar.Host/HungNodeEvidence.cs`
- `Quasar.Host/NodeActualizer.cs`
- `Shared/Diagnostics/DiagnosticCollector.cs`
- `Shared/Diagnostics/IncidentClassification.cs`
- `Shared/Diagnostics/DiagnosticStorage.cs`
- `Shared/Diagnostics/NativeDumpCapture.cs`
- `Shared/Diagnostics/WindowsCrashMonitor.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `docker-compose.diagnostics.yml`
- `Quasar.Tests/DiagnosticsUplinkTests.cs`
- `Quasar.Tests/QuasarDiagnosticLoggerTests.cs`
- `Quasar.Tests/DiagnosticCollectorTests.cs`
- `Quasar.Tests/IncidentClassificationTests.cs`
- `Quasar.Tests/ServerListPublisherTests.cs`
- `Quasar.Tests/ServerListClusterTests.cs`
- `Quasar.Tests/ServerListSettingsTests.cs`
- `Quasar.Tests/ServerListingPageTests.cs`

- `Docs/Announcements.md`
- `Quasar/Services/Updates/AnnouncementService.cs`
- `Quasar.Tests/AnnouncementTests.cs`
