# World import

Quasar can copy a Space Engineers world directly into an existing standalone
server without first creating a world template. Open **Import World...** in the
server's menu on Dashboard or Servers. Alternatively, open **Worlds**, then use a
row's **Import World...** action or **Import World to Server...** to pick a target.

The target must be inactive with goal **Off**, with no backup/restore/import in progress.
The action does not stop or start servers. Cluster worlds keep their existing
cluster deployment and World Authority flow.

## Folder or ZIP

1. Choose **Server Folder** or **Upload ZIP**.
2. For a folder, enter a path on the Quasar machine or use **Browse**. Confirm that
   the source is stopped or a completed save snapshot. For ZIP, choose a file from
   your browser machine. An archive may put the world at its root or inside an
   enclosing directory, but it must contain exactly one world outside Backup.
3. Click **Read World**. Quasar copies/extracts and validates a private staged
   save. Progress shows transferred MiB; **Cancel transfer** cancels the read.
4. Enter **Save folder name**. This is one new folder name, not a path.
5. Explicitly choose how to handle configuration, then **Import and Select**.

The save must contain a valid XML Space Engineers checkpoint named `Sandbox.sbc`.
All world files, including sectors, voxels and mod storage, are copied, except
historical `Backup` contents. The source is never moved or changed. Stop an
external source server yourself; Quasar also rejects known live managed sources and holds
their maintenance slots during copying so starts and restores cannot race it.

Import creates a new folder in the target's configured saves path, switches
`WorldSaveName`, clears old template provenance, and leaves the goal Off. Server
identity, port, paths and operational policy stay in place. The previous world
stays on disk. To switch back, select its save in the server editor and save the
server; select the previous profile too if import created a separate one.

## Configuration choice

- **Keep current profile**: the server keeps its profile. Its session settings and
  mod list apply on the next start, replacing the imported world's values. The
  dialog warns about this. This option also works when `Sandbox_config.sbc` is
  missing or cannot be read.
- **Create a separate profile using imported world settings and mods**: Quasar
  copies the current profile, applies the source's readable session settings and
  ordered mod list, and assigns the new profile only to this server. Plugins,
  access settings, command aliases and Online Mode are kept from the current
  profile. Missing source settings keep current values. Steam dependency
  autodetection is enabled for the imported list. Other servers sharing the old
  profile are unaffected. This option requires a readable `Sandbox_config.sbc`
  and configuration editing permission.

## Limits and failure behavior

Uploads stream to disk, with a 10 GiB ZIP limit, 50 GiB world limit and 100,000
file/folder entries. Checkpoint XML has a 256 MiB character bound; world config
XML has a 16 MiB character bound. ZIPs are ordinary world archives; use Backup
for Quasar backup packages rather than importing their full server configuration.

Paths must be safe on Windows and Linux. Quasar rejects traversal, absolute ZIP
paths, links/junctions, reserved filenames, duplicate or case-conflicting ZIP
paths, and destinations that already exist. Folder sources cannot contain the
staging destination. Custom saves paths are supported but cannot use links.

Private `.world-import-*` directories live in the destination saves directory.
ZIP uploads temporarily need disk for both the archive and extracted world,
while retaining previous saves. Template input uses private temporary staging
and a separate copy in the template library.

Starts, backups and restores cannot proceed against a server while activation is in
progress. Server edits, renames, deletion and goal changes are refused during
activation. Quasar checks the selected server revision again before publishing
its new selection. Failed or canceled operations retain the old selection and
remove owned staged files and any unselected profile created by that attempt.
A process crash or cleanup permission failure can leave private unselected
staging or an unselected complete save. Remove it only when no import is running;
Quasar never selects an incomplete copy. No automatic disk cleanup deletes old
worlds.

## Worlds and templates

**Worlds > Server worlds** lists standalone servers, selected saves, profiles,
process status and goals. Viewers can browse but cannot import. Server editing
permission is required for direct import.

**Worlds > Templates > Add Template...** opens predefined worlds and custom
folder/ZIP template import. The shared source fields use visible labels; Browse
aligns with the path input, and controls stack on narrow screens. Templates keep
clone, delete and profile-creation actions. Creating a save from a template still
works in the server editor. Both `/worlds` and the existing `/world-templates`
route open Worlds.

## API

The UI and API use the same staging and activation code. With authentication
enabled, reads require `CanView` and imports require `CanEditServers`; importing
world settings also requires `CanEditConfigs`. Use your normal authorized API
session. Only `application/json` and `application/zip` request bodies are accepted.

- `GET /api/servers/{uniqueName}/world` returns the selected save, profile, goal
  and `updatedAtUtc` revision.
- `POST /api/servers/{uniqueName}/world/import?saveName=Imported&importSettings=false&expectedUpdatedAtUtc=<URL-encoded revision>`
  imports and selects a world. A missing/stale revision returns 409.
- For folder input, use JSON with `sourcePath` and
  `sourceStoppedConfirmed: true`. Metadata is limited to 16 KiB.
- For ZIP input, use `Content-Type: application/zip` and stream the raw ZIP body.
  `importSettings=true` creates the separate profile described above. Supply
  `importSettings=true` or `false` explicitly; malformed or missing choices fail.
- Success returns the selected save/profile, Off goal, new revision and previous
  save name. No new world template is created.

Ownership is split between `WorldSaveFiles` (ingestion/validation),
`ServerWorldImportService` (profile/maintenance/rollback), and
`DedicatedServerCatalog.ActivateImportedWorldAsync` (checked atomic selection).
