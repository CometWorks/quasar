# Diagnostics, error reporting and server listing

This page covers what Quasar records about its own errors and crashes, the consent choices that control what leaves the machine, where the evidence is stored, and the optional public server listing on se-servers.net. For reading ordinary log files when something goes wrong, see [Troubleshooting](Troubleshooting.md).

## Overview

- Quasar always captures its own errors and process crashes locally. Capture does not depend on consent.
- Nothing is uploaded until an admin gives diagnostic consent. All choices are off by default.
- Quasar is the only process that talks to the diagnostics backoffice. Enrolled [Quasar.Host](ClusterHosts.md) machines hand their encrypted reports to Quasar over their existing authenticated connection, and Quasar uploads them.
- The default backoffice is `https://log.se-servers.net/`, run by CometWorks.
- Reports are encrypted on the machine that captured them with the backoffice's public key. Quasar cannot decrypt them.

## Consent choices

Consent lives in the **Data Handling Consent** section of **Security** (`/settings/security`, needs the security management permission, see [Access control](AccessControl.md)). The **Dashboard** also shows a **Data Handling Consent** prompt with YES and NO buttons while no decision is stored, or while statistics consent is on but diagnostic reports are off.

| Choice | Default | What it controls |
| --- | --- | --- |
| **YES** / **NO** buttons | no choice | YES turns on plugin usage statistics and diagnostic reports, and starts automatic enrollment with the backoffice. It does not turn on crash dumps or listings. NO turns off statistics, diagnostic reports and crash dumps. |
| **Send diagnostic reports** switch | off | Encrypted error reports and batched application logs from Quasar and all enrolled hosts. |
| **Include crash dumps** switch | off | Process memory dumps attached to crash reports. Only available while diagnostic reports are on. |
| **Show configured servers on se-servers.net** switch | off | Public server listing. See [Server listing](#server-listing-on-se-serversnet). |

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
already accepted pushes cannot be recalled. See [Monitoring](Monitoring.md#notifications)
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
| Quasar worker Error and Critical log messages | Yes, as incident files | Yes |
| Quasar fatal (unhandled) exceptions | Yes, written immediately | Yes |
| Exit of Quasar, Dedicated Servers started by Quasar, and Host-managed processes | Yes, with exit code and whether the stop was expected | Yes |
| Standard output of supervised processes (batched application logs) | Only while consent is on | Yes, as log batches |
| Recent process log attached to an incident | Yes | Yes, only for runs that started after consent |
| Crash dumps | Only for processes started while dump consent was on | Only with **Include crash dumps** |

Details:

- Quasar keeps its normal `quasar.log` separate. The diagnostics capture only listens to Error and Critical, so ordinary messages are not duplicated into reports.
- Each Quasar run keeps at most 256 incident files and accepts at most 32 ordinary error reports per minute. Fatal exceptions bypass the limit. Messages are truncated (8,192 characters for the message, 32,768 for the exception text).
- Reports contain error details, recent logs, software versions, and host, cluster and server identifiers. Logs can contain player names, chat and other private data. Crash dumps can contain process memory, credentials, player data and world content.
- Log batches from all running processes are delivered every ten minutes. The first batch after startup or after a new consent goes out right away. Error and crash reports go out on every poll.
- Crash dumps: .NET processes get a full CoreCLR dump in their run folder. On Linux, native crashes are imported from systemd-coredump (`coredumpctl`) when it is installed and readable by the service account. On Windows x64, the launcher attaches a crash monitor that writes a minidump on an unhandled exception. Quasar does not change system-wide core dump or Windows Error Reporting settings. Dumps over 2 GiB stay local and the report says so.

## Upload and queues

- Quasar polls its own collector and every connected host every 30 seconds, and immediately after a consent change.
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

Keep the whole `Diagnostics` folder (and `ServerList/epoch`, see below) when moving or upgrading Quasar. [Configuration backups](Backups.md) include `data-handling-consent.json` and `server-list.json` but not the `Diagnostics` or `ServerList` folders.

## Settings

| Setting key | Environment variable | Default | Purpose |
| --- | --- | --- | --- |
| `Quasar:Diagnostics:BackofficeUrl` | `Quasar__Diagnostics__BackofficeUrl` | `https://log.se-servers.net/` | Backoffice URL. Must be HTTPS (plain HTTP only on loopback). No user info, query string or fragment. An explicitly empty or invalid value disables uploads. |
| `Quasar:Diagnostics:UploadToken` | `Quasar__Diagnostics__UploadToken` | none | Optional legacy fixed credential. Leave unset for automatic enrollment. |
| `Quasar:Diagnostics:StorageDirectory` | `QUASAR_DIAGNOSTICS_DIRECTORY` (wins) or `Quasar__Diagnostics__StorageDirectory` | `<Quasar data directory>/Diagnostics` | Where `runs/`, `incoming/` and `outbound/` live. Must be an absolute path and not a filesystem root. |

An invalid backoffice URL logs `Diagnostic uplink disabled: configure an HTTPS backoffice URL (or loopback HTTP).` at startup.

To disable uploads entirely without touching consent, set `Quasar:Diagnostics:BackofficeUrl` to an empty string. Local capture continues either way.

### Storage on a separate disk

Set `QUASAR_DIAGNOSTICS_DIRECTORY` to an absolute path for the Quasar service. Bootstrap reads it too and passes its resolved directory to the worker, so both use the same place. `installation-id`, `machine-key.pem`, `policy.json` and the consent file stay in their normal locations.

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

## Server listing on se-servers.net

Quasar can publish standalone Magnetar servers and clusters to the public server directory at se-servers.net. The listing goes through the diagnostics backoffice, which forwards signed listing updates to the website.

### Requirements

All of these must be true before anything is published:

1. **Send diagnostic reports** is on (Security). Statistics consent is not required.
2. **Show configured servers on se-servers.net** is on. The switch is on both the Security page and the Listings page.
3. At least one Steam role mapping with the Admin role exists in Security. The SteamID must be a 17-digit SteamID64.
4. The server is selected on the Listings page with a public hostname or IP.
5. The backoffice has received current, consented Quasar logs. Quasar asks the backoffice for a challenge first and sends no server details or admin identities until the backoffice confirms recent log delivery. No crash or memory dump is needed.

If any of 1 to 3 is missing, Quasar sends a withdrawal instead.

### Configure listings

1. Open **Listings** in the main navigation (`/settings/server-list`). The page is titled **Server listing**. Viewing needs the view permission; saving needs the security management permission.
2. The page lists every standalone server and cluster. Names and game ports are pre-filled from the server's in-game name and server port. For a cluster, the port comes from its deployed Steam gateway configuration, or 28000 if that is unknown.
3. For each server to publish, tick **Publish this server**, check **Public server name** (max 128 characters), enter **Public hostname or IP address** (no scheme, no port) and confirm **Public game port**. For a cluster use the Steam gateway address and port, not the Cluster Registry admin endpoint.
4. Click **Save listings**. Changes apply without a restart. The page says delivery may take up to two minutes.

**Reload** refreshes the source list and discards unsaved edits. Unticking a server removes it from the next publication. Unticking all servers withdraws the installation's listings. Names, addresses and listing IDs stay saved.

The page warns when diagnostic consent or a Steam admin mapping is missing, and when a saved source no longer exists ("This source no longer exists. Its listing will be omitted from publication.").

### What is public

- Public: listing name, public host and port, observation time, running state, players online, max players, and simulation speed (capped at 1.00).
- Clusters: connected client count from the gateway, online and total node counts, gateway phase and health, partition and handover counts, and per-node role, state and quarantined partition count. Nodes appear as `Node 1`, `Node 2` and so on. Max players and sim speed are not sent for clusters.
- Private (sent to the backoffice, not public): the Steam admin SteamIDs. They let those admins edit the listing description on se-servers.net. Quasar roles other than Steam Admin mappings give no rights on the website.

A standalone server's statistics are sent only when exactly one connected agent reports it and the snapshot is at most 60 seconds old. Otherwise the listing shows unknown values.

### Publication timing and withdrawal

- Quasar publishes every minute, and immediately after a change to listing settings, consent, role mappings, servers or clusters.
- Turning off the listing switch, turning off diagnostic reports, or removing the last Steam admin mapping makes Quasar send a withdrawal on the next cycle, which runs immediately.
- Each publication carries an increasing epoch and sequence number. Quasar stores the epoch in `ServerList/epoch` and raises it at every start, so a delayed older update never overwrites a newer one. Do not run two Quasar instances over the same data directory.

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
- The listing switch in Security is still required.

After the first save in the UI, `server-list.json` wins over `appsettings.json`. Restart Quasar after restoring `server-list.json` from a backup or editing it by hand.

## Common problems

- **The Listings page says "Saved listing settings could not be loaded. Publishing is disabled until you save valid settings."** `server-list.json` is unreadable or invalid. Quasar does not fall back to `appsettings.json`. Fix the entries on the page and save.
- **"Listing settings changed in another session. Reload before saving."** Someone else saved in the meantime. Click **Reload** and redo the edit.
- **"Invalid server list identity, source, name or public join address."** A selected listing has an empty name, a name over 128 characters, a port outside 1 to 65535, or a host that is not a valid hostname or IP, or is localhost or loopback.
- **"You no longer have permission to manage server listings."** The signed-in account lacks the security management permission.
- **The server is selected but not on se-servers.net.** Check the four requirements above. Quasar log delivery must be working, so check the log for `Diagnostic uplink retry pending`, `Diagnostic key unavailable` and `Server list update deferred` warnings.
- **Log warnings `Diagnostic report from Host {HostId} will retry` or `Diagnostic transfer from Host {HostId} will retry`.** Quasar could not fetch reports from that host. They stay on the host and Quasar retries on the next poll.
- **`Diagnostics identity changed; restart to re-enroll.`** The `Diagnostics/installation-id` file changed while Quasar was running. Restart Quasar.

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
- `Quasar/Services/Backup/QuasarBackupService.cs`
- `Quasar/Program.cs`
- `Quasar/Components/Pages/Security.razor`
- `Quasar/Components/Pages/ServerListing.razor`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Host/Program.cs`
- `Shared/Diagnostics/DiagnosticCollector.cs`
- `Shared/Diagnostics/DiagnosticStorage.cs`
- `Shared/Diagnostics/NativeDumpCapture.cs`
- `Shared/Diagnostics/WindowsCrashMonitor.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `docker-compose.diagnostics.yml`
- `Quasar.Tests/DiagnosticsUplinkTests.cs`
- `Quasar.Tests/QuasarDiagnosticLoggerTests.cs`
- `Quasar.Tests/DiagnosticCollectorTests.cs`
- `Quasar.Tests/ServerListPublisherTests.cs`
- `Quasar.Tests/ServerListSettingsTests.cs`
- `Quasar.Tests/ServerListingPageTests.cs`

- `Docs/Announcements.md`
- `Quasar/Services/Updates/AnnouncementService.cs`
- `Quasar.Tests/AnnouncementTests.cs`
