# Backups

The **Backups** page creates, schedules, stores and restores ZIP backups of Quasar's configuration, of individual servers, and of world saves. This page covers what each backup type contains, schedules and retention, where the files go, how restore works and what blocks it. Cluster backups are summarized at the end.

## The Backups page

Open **Backups** in the nav menu (Infrastructure & tools group). The URL is `/backup`. Only the Admin role can open it or download backup files.

The page has four parts:

- **Create & restore**: **Back up Quasar configuration to file** downloads a configuration backup straight to your browser. **Restore Quasar configuration from file…** uploads any Quasar backup ZIP (configuration, server or world) and restores it according to its type.
- **Version compatibility**: the restore version rules (see [Version rules](#version-rules)).
- **Server & world backups**: one row per server with four buttons: **Back up server**, **Restore latest server backup**, **Back up world**, **Restore latest world backup**. Below it, **Automatic backups** holds the three schedule rules, with **Save rules** and **Run enabled rules now**.
- **Stored backups**: the backup folder setting and a table of every ZIP in that folder, with **Download**, **Restore** and **Delete** per file.

## Backup types

Every ZIP has a `quasar-backup.json` manifest at its root with the backup type, the Quasar version it came from, the creation time, the host name, and for server and world backups the server's unique name and display name.

| Type | Contains | Does not contain |
| --- | --- | --- |
| Quasar config | Quasar's own catalogs and settings (list below) | DS app data, Magnetar app data, world saves |
| Server | One server's `server.json`, its Dedicated Server app data, its Magnetar app data, and its `SpaceEngineers-Dedicated.cfg` when that file lives outside the DS app data folder | World saves, caches (list below) |
| World | One server's world save files, plus its `server.json` for identification | `Sandbox_config.sbc` (world config) |

### Quasar config backup contents

From the install root, when present: `known-players.json`, `known-player-settings.json`, `data-handling-consent.json`, `server-list.json`, `discord.json`, `death-messages.json`, `branding.json`, `steam-workshop.json`, `rbac.json`, `dev-folders.json`, `plugin-hubs.json`, every `Magnetars/*/server.json`, every `ConfigProfiles/*/profile.json`, every `WorldTemplates/*/template.json`, and all files in `Branding/`.

It does not include `appsettings.json`, `backup-settings.json`, `github-updates.json`, the `DataProtection-Keys/` folder, server history, world template world files, or logs. Without the Data Protection keys, encrypted secrets such as the Steam Workshop API key do not decrypt on another machine. Re-enter them after moving a backup to a new machine.

### What server backups skip

- the world save folder (use a world backup for it)
- `content/` inside the Dedicated Server app data (workshop content cache)
- `GitHub/`, `NuGet/`, `Preloader/` and `Sources/Plugins/` inside the Magnetar app data
- `Local/Harmony0.dll`, `Local/Magnetar.Protocol.dll` and `Local/Quasar.Agent.dll` inside the Magnetar app data

### World backups of running servers

You can take a world backup while the server runs. If the world folder has a Space Engineers `Backup` subfolder, Quasar copies the newest snapshot folder in it that contains `Sandbox.sbc`, instead of the live save. Otherwise it copies the live world folder. The server needs a selected world save, or the backup fails with "Server '<name>' has no world save selected."

## Making backups by hand

- **Back up Quasar configuration to file** builds the ZIP in memory and downloads it. Nothing is written to the backup folder.
- **Back up server** and **Back up world** queue a job and show "Server backup queued." or "World backup queued.". The ZIP lands in the backup folder. When the job finishes, the page (if still open) shows "Server backup created in the configured backup folder." or the failure, such as "World backup failed: <reason>".
- **Run enabled rules now** queues all enabled automatic rules at once ("Enabled backup rules queued."). These count as automatic backups: they get the `-auto` suffix, count toward retention, and reset each rule's schedule from now.

Backup jobs run one at a time in the background. A queued job is Queued, then Running, then Succeeded or Failed.

## Automatic backups

**Automatic backups** has three independent rules: **Quasar config**, **Servers** and **Worlds**. All are off by default. Each rule has:

| Field | Values | Default |
| --- | --- | --- |
| Enable switch | on/off | off |
| Frequency | Hourly, Daily, Weekly | Daily |
| Time of day | Daily and Weekly only | 03:00 |
| Day of week | Weekly only | Sunday |
| Keep last N backups (per server for Servers and Worlds) | 1 to 1000 | 10 |

Press **Save rules** to apply. Quasar stores the rules in `<install root>/backup-settings.json` and reloads that file when it is edited by hand.

How scheduling works:

- The scheduler starts 10 seconds after Quasar starts and checks every minute.
- A rule that has never run is due at the next check, so the first backup comes within about a minute of enabling it.
- Hourly runs one hour after the last run. Daily runs at the next occurrence of the time of day after the last run. Weekly runs at that time on the chosen day.
- Times are the Quasar host's local time.
- The Servers and Worlds rules back up every configured server in one run.
- If Quasar was down at the scheduled time, the rule is due at the first check after startup.

### Retention

After each automatic backup, Quasar deletes the oldest automatic backups of that type beyond the rule's count. For Servers and Worlds the count is per server. Retention only touches files whose name ends in `-auto.zip`. Manual backups stay until you delete them. The exception is a deleted cluster, whose backups all go when its [retention](Clusters.md#delete-or-forget-a-cluster) ends.

## Where backup files go

The default folder is `<install root>/Backups/` (Linux default install root: `~/.local/share/Quasar`). To change it:

- On **Backups**, under **Stored backups**, type a path in **Backup folder** (or use **Browse**), then press **Save folder**. **Use default** clears it. The change applies to new backup operations immediately.
- Or set `Quasar:BackupDirectory` in `<install root>/appsettings.json` and restart Quasar.
- Or set the `QUASAR_BACKUP_DIR` environment variable. It wins over the other two, and the page shows the folder read-only with "QUASAR_BACKUP_DIR is set to <path>, so this page cannot override it."

An empty value means the default. A relative path resolves under the install root. For a network share, mount it before Quasar starts and give the Quasar service account permission to create, list, read and delete files there. Quasar does not move existing ZIPs when the folder changes. Move them by hand if they should show up in the new folder.

In Docker, the documented example sets `QUASAR_BACKUP_DIR=/data/Backups`, inside the `quasar-data` volume. See [Docker](Docker.md).

### File names

| Type | Name |
| --- | --- |
| Quasar config | `quasar-backup-yyyyMMdd-HHmmss.zip` |
| Server | `quasar-server-<unique name>-yyyyMMdd-HHmmss.zip` |
| World | `quasar-world-<unique name>-yyyyMMdd-HHmmss.zip` |

Automatic backups add `-auto` before `.zip`. If the name already exists, Quasar appends `-2`, `-3` and so on. The timestamp is local time. Quasar creates each ZIP as an owner-readable/writable `<name>.zip.tmp` on Unix and renames it when done; the completed ZIP keeps that mode. At startup it deletes leftover `*.tmp` files from interrupted backups. Uploaded restore archives use an owner-only temporary staging file too. Backups can contain tokens, server passwords, and private plugin state, so protect the backup directory on every platform.

The **Stored backups** table lists only `*.zip` files directly in the backup folder. Its Type column shows "Quasar config", "Server" or "World", with "(auto)" for automatic ones.

## Restoring

Restore from a row in **Stored backups**, from the **Restore latest ...** buttons in the server table, or by uploading a ZIP with **Restore Quasar configuration from file…** (up to 10 GiB through the browser). Quasar asks for confirmation first.

| Type | Restore behavior |
| --- | --- |
| Quasar config | Merges by ID. Files from the backup overwrite files with the same path, new IDs are added, and items missing from the backup stay. For a clean restore, delete the configs in Quasar first. The result says "Restored N configuration file(s). Restart Quasar to be sure every component picks up the change." |
| Server | Writes `server.json` and the DS and Magnetar app data back for that server. If the server no longer exists in Quasar, the backup recreates it. Result: "Restored N server backup file(s) for <name>. Restart that server before relying on restored files." |
| World | Overwrites the world save files of an existing server. The server's current `Sandbox_config.sbc` and server settings stay. Result: "Restored N world file(s) for <name>; existing world and server config was kept." |

Every restored `server.json`, from a server backup or a configuration backup, comes back with goal state Off and auto-start disabled. Start the server yourself once its world is in place.

Restore extracts and overwrites files. It does not delete files that exist on disk but not in the backup.

### Server must be stopped

Server and world restores refuse to run while that server is Starting, Running, Restarting or Stopping. The restore buttons are disabled with the tooltip "Stop the server before restoring." While a restore runs, Quasar will not start that server. Configuration restores are never blocked by running servers.

### Version rules

The manifest's Quasar version must have the same major.minor as the running Quasar. Patch differences are fine in both directions.

- Older major.minor: "Restoring a backup from older Quasar X.Y into A.B is not supported."
- Newer major.minor: "Cannot restore a backup from a newer Quasar (X.Y) into this older one (A.B). Downgrading across major.minor versions is not supported."

Take a fresh configuration backup after each Quasar update that changes the major or minor version, since older backups stop being restorable.

## Cluster backups

Managed clusters follow the **Servers** rule's schedule and retention. A cluster that is proven stopped gets a clean backup. A running cluster is copied without downtime at up to **Running cluster copy rate (MiB/s)** (default 32, range 1 to 4096) and restores to the state of a crash at the start of the copy. Retention always keeps the newest clean backup in addition to the last N. A failed scheduled cluster backup is retried one hour later.

Cluster backups are stored under `<backup folder>/Clusters/<hash of the cluster name>/<snapshot id>/` and do not appear in the **Stored backups** table. Restoring a cluster and taking manual cluster backups happen from the cluster's own page. See [Clusters](Clusters.md).

## Common problems

- **"This ZIP is not a Quasar backup (missing quasar-backup.json)."** The file was not made by Quasar, or was repacked.
- **"The selected file is not a valid ZIP archive."** The upload is damaged or not a ZIP.
- **"The backup manifest is missing version information."** or **"The backup manifest could not be read."** The manifest is damaged.
- **"Server '<name>' is running. Stop it before restoring its server backup."** (or world backup). Stop the server and try again.
- **"A restore is already in progress for '<name>'. Wait for it to finish before restoring again."**
- **"World backup targets server '<name>', but that server does not exist in Quasar."** World backups only restore into an existing server with the same unique name. Restore its server backup first, or recreate the server.
- **"Server '<name>' has no world save selected."** Select a world for the server before taking a world backup.
- **A scheduled Servers or Worlds rule keeps failing.** The rule backs up every server in one run, and one failing server (for example, one without a world) fails the run. Quasar logs "Scheduled World backup failed." (or Server), does not record the run, and tries again on the next one-minute check. Fix or remove the failing server.
- **Backups do not show up after changing the folder.** Existing ZIPs stay in the old folder. Move them.
- **"QUASAR_BACKUP_DIR is set; remove it before editing the backup folder from the web UI."** Remove the environment variable and restart Quasar, or change the variable instead.
- **Steam Workshop API key missing after restoring on another machine.** Data Protection keys are not in backups. Enter the key again.

## Sources
- `Docs/StateMachines/BackupJobs.md`
- `Docs/Configuration.md`
- `Docs/Docker.md`
- `Quasar/Services/Backup/AutomaticBackupService.cs`
- `Quasar/Services/Backup/QuasarBackupService.cs`
- `Quasar/Services/Backup/QuasarBackupSettingsService.cs`
- `Quasar/Services/Backup/BackupCompatibility.cs`
- `Quasar/Services/Backup/ServerRestoreCoordinator.cs`
- `Quasar/Models/QuasarBackupSettings.cs`
- `Quasar/Models/QuasarBackupManifest.cs`
- `Quasar/Services/ClusterBackupService.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Program.cs`
- `Quasar/Components/Pages/Backup.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
