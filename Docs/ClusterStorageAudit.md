# Deployed filesystem audit — 2026-10-11

A full scan of the deployed Quasar directory after cleanup found **61.64 GiB,
211,058 regular files and 7,467 subdirectories**. The scan used `lstat`, followed no
symlinks, and reported no read errors. It found no symlinks or duplicate hard-link
inodes in this deployment. These are allocated-block measurements, not exclusive
Btrfs extent usage. Files may change during a live scan. Nested rows below overlap;
do not add them together.

## Cleanup performed

Moved 22 obsolete items to desktop Trash using `gio trash`; no permanent removal:

| Content | Items | Allocated GiB moved |
| --- | ---: | ---: |
| Unreferenced dependency snapshots | 11 | 87.81 |
| Unreferenced Host installations | 7 | 56.55 |
| Completed setup installation tree and transfer archives | 3 | 16.14 |
| Interrupted update workspace | 1 | 0.06 |
| Total | 22 | 160.57 |

Rechecked current cluster metadata, managed backup/recovery references, Host
attachments and deployment receipts, referenced bundle manifests, and user-owned
process command lines, memory maps and open paths before moving anything. An older
Host receipt retained one additional dependency snapshot beyond the initial audit.
Active, prepared and recovery content, standalone servers and world data remain.
Every move has a verified original-path/Trash-path mapping in
`Backups/storage-trash-20261011.json`. Trash is on the same filesystem, so this
does **not** free disk space until the operator empties Trash.

The cluster remained Serving/Healthy with nine fresh node leases after cleanup.
The running services have not been upgraded with this source change; live combined
release/content staging and the automatic migration still require rollout validation.

## Remaining size and file-count hotspots

Paths are relative to the deployed Quasar directory unless labeled Host state.

| Directory | Allocated size | Files | Assessment and next action |
| --- | ---: | ---: | --- |
| `ManagedRuntime` | 49.57 GiB | 182,143 | Dominated by the next two rows. |
| `ManagedRuntime/Tools/ClusterDependencies` | 39.92 GiB | 150,568 | Five retained legacy snapshots, about 7.98 GiB each. The new content store migrates retained copies into shared immutable objects; snapshot paths remain references. |
| `ManagedRuntime/Tools/SpaceEngineersDedicatedServer` | 8.09 GiB | 29,999 | Managed source installation; needed for provisioning. Its mutable updater must not share writable inodes with pinned snapshots. |
| `ManagedRuntime/Tools/Cluster` | 747.3 MiB | 844 | Eight cached release versions. Add retention based on selected, active, prepared and recovery package references; download caches are outside this collector. |
| `ManagedRuntime/Tools/SteamCMD` | 195.0 MiB | 179 | Installed tooling, not historical snapshots. |
| `Magnetars` | 9.22 GiB | 24,010 | Standalone instance data; excluded from cluster cleanup. |
| `Magnetars/sigma-repro` | 6.22 GiB | 16,795 | Mostly extracted Workshop content: 5.99 GiB and 16,208 files. Review instance retirement separately; share immutable Workshop versions with independent writable worlds. |
| `Magnetars/clangy` | 2.10 GiB | 5,080 | Same standalone version-cache opportunity; do not infer that an instance is disposable from its name. |
| `Magnetars/newtest` | 0.89 GiB | 2,104 | Preserved standalone server. |
| `Backups` | 0.97 GiB | 1,367 | Almost entirely the original deployment recovery backup. Preserve until recovery retention is deliberately released. |
| `ClusterSetup/test-v3` | 0.82 GiB | 860 | Transfer artifacts removed. Remaining preparation 460 MiB, source world 299 MiB, plugins 78.5 MiB and split world 6.6 MiB. Future terminal setup cleanup can retain a recovery manifest and references instead of these derived trees. |
| `WorldTemplates` | 0.56 GiB | 351 | User-owned templates. Content-address immutable seeds; copy only at the mutable runtime boundary. |
| `GridBackups` | 171.9 MiB | 44 | Recovery content; enforce explicit backup retention rather than treating it as cache. |
| `Logs` | 57.7 MiB | 2 | Byte/age rotation is sufficient; no file-count problem here. |
| `Updates` | 49.3 MiB | 8 | Small staging area; existing updater cleanup handles completed staging. |
| `Caches/ui-plugins` | 33.4 MiB | 500 | Shadow-copy versions have no old-version pruning in the catalog. Retain loaded versions until unload/restart, then remove unreferenced hashes. Avoid recopying an already verified version. |
| `Diagnostics/runs` | 4.7 MiB | 1,186 | Small files across 49 run directories. Review completed-run retention in addition to incident-file retention. |
| `PluginConfigSnapshots` | Less than 1 MiB | 1 | Already one atomically replaced snapshot per server, not one copy per observation. No storage redesign justified by this installation. |

Outside the deployed directory, Host `state/conversions` still occupies **81.81 GiB**,
including **40.45 GiB** in five retained installations. The remainder includes
configuration, world and runtime/recovery trees. New registered configurations and
installation references have managed lifetimes; old unregistered recovery/runtime
directories intentionally need an ownership audit before pruning.

The largest *direct* file-count hotspot is the dedicated-server
`Content/Models/Cubes/large` directory: **13,396 files**, repeated in each legacy
dependency snapshot. These are game assets, not Quasar metadata. Retained snapshot
references will still have directory entries, but no duplicate payload bytes.

After cleanup, the deployed tree contains ten `ClusterRegistry.AdminContract.dll`
copies and six `ClusterGateway.AdminContract.dll` copies: **1.90 MiB combined**.
They are versioned package/dependency artifacts. Shared content and package retention
address their duplication; they are not a significant source of disk usage.

## Where filesystem use can be reduced

1. **Use manifests for version identity and references for immutable bytes.** This
   change does that for cluster dependencies, installations, mod versions and generated
   configuration. The next extensions are package caches, completed setup intermediates
   and standalone Workshop content, each with explicit live/recovery ownership.
2. **Keep game-facing payloads as files.** Space Engineers, Magnetar and the packaged
   generator expect directory layouts. A database of large binary assets would still
   require extraction. Hard links preserve those interfaces without repeating bytes.
3. **Avoid full trees for metadata-only snapshots.** Store inventories once per version
   and materialize reference trees only for consumers that need paths. This could reduce
   directory-entry counts further, but needs coordinated package/runtime changes.
4. **Use a transactional metadata store only where it removes real coordination cost.**
   A future SQLite catalog could combine ownership receipts and operation/run metadata
   with transactions and indexes, avoiding directory scans and scattered atomic JSON
   replacements. The measured tiny-file volume does not justify a broad database
   migration in this patch. Keep recovery/export manifests portable.
5. **Bound caches at their owning lifecycle.** Loaded UI assemblies, retained release
   packages, completed diagnostics and setup workspaces need distinct release rules.
   Age-only sweeping cannot safely distinguish live payloads from obsolete versions.

The full per-directory inventory and large-file list are saved locally in
`Backups/filesystem-audit-20261011.json`. The inventory predates writing this report
and the inventory file itself, so those reporting artifacts are not included in its totals.
