# Cluster input storage and retention

See the [deployed filesystem audit](ClusterStorageAudit.md) for measured storage,
the reversible legacy cleanup and remaining retention opportunities.

Deployment snapshots are pinned manifests plus filesystem references to content objects.
Payload bytes are keyed by SHA-256 and executable mode; identical files across releases,
dependency snapshots, mod versions and generated configurations share their objects.
Linux hard links preserve the existing package directory layout without following symbolic
links. The objects and references are read-only. Mutable runtime worlds and configuration
receive independent writable copies; saving a game cannot modify a deployment snapshot.
Node mods are the exception: a server process only reads them, so each node's `Mods`
folder (and the runtime `world/Mods`) holds hard links to the read-only objects instead of
a 1.7 GB copy per process. Files a server adds there (`.sbcB5` prefab caches, extracted
legacy Workshop archives) are new files of that node; a read-only object refuses writes.
On another filesystem, or for a writable seed file, the Host copies as before.

Quasar uses `ManagedRuntime/Tools/.objects`; each Host uses
`state/conversions/.objects`. These are separate distribution endpoints. A different
filesystem gets a local object store because hard links cannot cross devices. Offline
Host commands use a store beside the requested installation root.

## Reference lifetime

Catalog selection, active deployment provenance, prepared changes, the previous rollback
revision and an interrupted update retain their inputs. Retained managed backups retain
their saved deployment inputs too. Historical operation receipts alone do not retain
payloads. Completed updates do not keep their old workflow reference indefinitely.

Recovery/export backups outside the managed backup format can include a
`deployment-references.json` file under the backup directory. Its value is an array of
cluster-definition storage roots: `uniqueName`, `dependencyManifestSha256`, and optionally
`preparation`. It records storage dependencies, not active runtime state. Removing the
backup also releases these roots. Unreadable backup/reference metadata or missing
provenance blocks collection rather than being interpreted as an empty reference set.

Catalog changes and completion of storage work trigger collection. There is no age-based
retention window for unused versions. Collection waits for in-progress workflows, including
the interval between uploading files and committing the prepared deployment. Once a
snapshot is released, its hard links are removed; an object whose last snapshot reference
has disappeared is unlinked immediately. Multiple clusters can retain the same world/mod
input. A Host independently protects the artifact and configuration roots of its active
attachments. Offline/unreachable Hosts defer cleanup with a bounded request and retry.

Existing dependency and Host installation copies are migrated by verified, atomic file
replacement, preserving the approved paths and hashes. Generated configurations are pruned
only after preparation registered ownership. Unregistered operator recovery directories,
runtime directories and standalone servers are never swept. An audit is still required
for old unregistered conversion/configuration directories.

Completed setup/conversion deletes temporary installation trees and transfer archives.
Abandoned `ClusterUpdates` work directories are removed when no storage workflow is active.
Published package download caches and deliberately retained runtime recovery copies have
separate lifetimes; this collector does not delete them.

## Cluster backups

A Host snapshot leaves out what a restore does not need: the profile-pinned `Mods` folder of
every node, which activating the restored deployment writes again from its bundle (a
restore always activates a newly prepared revision), and the cluster plugin's
`data/cluster/handover-diff` dumps, which are diagnostics. The snapshot manifest lists the
left-out mod folders (`rebuiltMods`); a restore refuses a candidate deployment that does not
pin mods for those nodes.

Scheduled backups follow the Servers backup rule's retention. The pre-update backup of each
managed update and the backup a conversion takes are recorded with their purpose
(`purpose`: `update` or `conversion`) and Quasar keeps the newest 3 of each per cluster.
Backups taken before this, and manual backups, are never pruned.

## Combined updates and transfer

Staging a release with queued content builds one candidate using the selected release,
resolved profile, exact queued versions and preserved topology/world seed. One managed
apply performs the existing backup, shutdown, migration and restart. Queues are cleared
only when every Host prepares successfully and the queue/profile still match.

The UI reports release verification, individual mod reuse/download, plugin preparation,
changed-file packaging, transfer size and Host preparation. Workshop versions are cached
only with a version receipt and verified payload inventory. Versions are observed before
and after download; missing receipts require one download to establish provenance.
Magnetar's implicit Steam companion is included in the managed mod payload when crossplay
is disabled. Only references from retained deployment specifications keep mod versions.

An authenticated Host inventory permits sparse installation/mod archives. Received entries
and reused content are checked against the new pins before atomic publication. Reuse is
allowed only for matching path, SHA-256, byte count and executable mode. Removed files
are absent from the candidate. Old Hosts without inventory endpoints receive full archives
and retain their old storage behavior until upgraded. Manifests remain compatible with the
existing packaged deployment generator; its writable working copies are disposable.
