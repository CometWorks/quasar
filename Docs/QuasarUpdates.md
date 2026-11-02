# Quasar application updates

Quasar now runs its web UI and server supervision in one application process.
Bootstrap is retained in the source tree for older-installation recovery, but is
not the entry point in current platform packages. Each normal platform package
contains Quasar, a small updater, the matching Host, install scripts and defaults.
Legacy `quasar-web-*` assets remain during migration; old `quasar-installer-*`
launcher assets are deliberately absent so legacy Bootstrap cannot self-activate
the incompatible layout; debug symbols remain CI
artifacts rather than another normal installation package.

The same complete package includes the [guided installer](Installation.md).
Opening `Install.cmd` or `Install.desktop` installs the main and updater together;
manual installer commands remain available. Upgrading the updater uses this full
package with both services stopped.

## Required 1.4.1 migration before 1.5

**Upgrade requirement:** install and successfully start the complete **1.4.1**
migration release before installing or activating **1.5 or later**. This requirement
also applies to prerelease and manual file-replacement paths. Start with 1.4.1 for
a new installation that will move to 1.5. An old Bootstrap installation running only
the 1.4.1 web worker has not completed the migration.

For managed installations, the verified native updater records completion after
checking the 1.4.1 main service's ownership, version, PID and authenticated health.
A complete manual 1.4.1 package records completion after successful application
startup. The durable receipt is `<data>/Updates/migration-1.4.1.json`; preserve it
with the data directory. Missing or invalid completion evidence blocks 1.5 before
staging/activation, installer file replacement, or application startup. Run the
1.4.1 installer and start 1.4.1 to recover; do not fabricate a completion receipt.

Only the 1.4.1 release line publishes legacy `quasar-web-*` bridge assets. Future
releases publish the two complete platform packages; an old Bootstrap therefore
selects the bridge rather than a 1.5 worker. Release cleanup retains the latest
full 1.4.1 bridge even after newer releases arrive. CI blocks full 1.5+ publication
until a full 1.4.1 release with both platform packages, worker bridges and checksums
exists. PR builds remain draft prereleases for review. Download their packages
directly; publish the draft as a prerelease before testing in-app prerelease
discovery. Promote and validate 1.4.1 before publishing a full 1.5 release.

## Normal operation

On **Updates**, select a version and press **Update Quasar**. The same operation is
available locally as:

```sh
./Quasar update --version 1.5.0
./Quasar update --status
./Quasar update --retry
```

Use `Quasar.exe` on Windows. Use an elevated Windows shell or the configured Linux installation account. The executable discovers `service-installation.json`; the installer metadata
resolves its data directory, service names and health endpoint. No SSH connection,
remote deployment or API authorization bypass is involved. UI administration keeps
its existing security policy; the CLI uses local filesystem permissions.

The application selects and downloads the complete release, verifies `SHA256SUMS`,
validates its layout, and performs the existing appsettings merge. A separate updater
service independently obtains the checksum from the installed trusted repository,
checks a private archive copy, and extracts that copy before changing application
files. A staged directory or a request token alone is not proof of release provenance.
The updater service's own files and protected installation metadata are excluded
from running application replacement. Upgrade those through the installer.

The updater verifies both native services, stops only Quasar, retains the complete
previous application, replaces the application, starts it, and checks the exact
requested version and service PID through an authenticated loopback health endpoint.
Writing a pointer, installing files, or obtaining a running PID does not count as
success. Application updates and restarts preserve detached game-server processes
and persist their adoption state, including when ordinary shutdown is configured to
stop them. Machine reboot cannot preserve processes; normal persisted goals apply.

## Durable status and recovery

`<data directory>/Updates/operation.json` records the operation ID, target version,
last message and stages `Queued`, `Downloading`, `Verified`, `Ready`, `Restarting`,
`Healthy`, `Failed` and `Abandoned`. The existing cross-process maintenance lease
serializes publication and replacement. Native stop/start runs outside that lease
so the application can finish shutdown and persist state.

Reopening Updates or restarting processes reads this journal. Duplicate requests
reuse the active operation. Retry creates a new identity for the same target;
periodic checks do not replay completed or failed operations. An interrupted
preparation requires retry. An interrupted replacement is recovered by the updater
before it accepts another activation. The complete previous application and config
remain available in `Updates/LauncherTransaction` under the application installation
until replacement passes health checks. Partial replacement and failed launches
restore that complete snapshot, rather than only an old executable.

A failed operation remains failed after the old service is restored. Correct its
reported cause, then use **Retry update** or `update --retry`. Do not delete the
journal, maintenance marker or retained transaction to force another activation.
Internal recovery is available through `Updater/Quasar.Updater recover
--installation <protected service-installation.json>` with both services stopped.
It restores files; start Quasar afterward and check status. Older Bootstrap
installations retain their original `activate-release` recovery behavior.

CLI exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Status read, or the requested release is verified healthy and running |
| 2 | Invalid command/version |
| 3 | Service unavailable, access denied or recovery needs operator action |
| 4 | Safely staged; manual application restart is required |
| 5 | Update failed |
| 6 | Still pending after the CLI wait; inspect status |
| 130 | Waiting was cancelled; the durable operation may continue |

## Native supervision

Updates persistently shows detected restart capability and repair instructions.

On Linux, the actual main PID must belong to the configured systemd unit. Its
executable/action and account must match; it needs `Restart=on-failure`,
`KillMode=process`, and must not treat failure exit 75 as success. The updater unit
must also run the installed updater with its configured account and recovery
policy. User units remain user units. System installs keep the configured main
account and use a narrowly scoped polkit rule permitting that account to start,
stop and restart only its Quasar unit; the updater does not run as root.

On Windows, native SCM service PID, executable paths, accounts and restart actions
are checked. New installs use `NT SERVICE\<service name>` virtual accounts, so no
password is needed. The main service owns its data; the updater has application
replacement access and query/start/stop rights for the main service. Updater files
and installation metadata have separate permissions. The updater remains outside
the main process and stops it before replacing Windows-locked files. Scheduled
Tasks are a legacy migration source, not the current supervision mechanism.

Intentional shutdown exits successfully and stays stopped. A ready update does not
start an intentionally stopped main service. Foreground/source launches can stage
without native services. Stop Quasar, run `update --retry`, then start the installed
Quasar manually; its startup confirms the requested version. If an executable cannot
load, use the retained updater/recovery tooling rather than repeatedly launching it.

## Migrating existing installations

Do not overlay files onto a running installation. Keep a copy of the complete old
installation and back up appsettings, credentials and Data Protection keys together.
Preserve the install path, data path and Linux service account/scope. Migration from
a Windows Scheduled Task can require one planned game-server restart because
existing processes may still belong to that task's job object; save worlds first.
Later native-service application updates preserve running servers.

Extract the complete new platform package to a temporary directory. Run its
installer targeting the existing installation. It preserves an existing root
`appsettings.json` and data; it does not reset worlds or credentials. The old package
must remain available for manual restoration until migration succeeds.

Linux user service, using the existing names and path:

```sh
./install.sh --no-build --install-dir /path/to/Quasar --service-name quasar --start
```

For a system unit, use `sudo`, `--system`, and the existing `--user <account>`.
For files already replaced while stopped, use `--repair-supervision` with the same
path, name and user/system flags. It repairs service registration and metadata
without rebuilding or copying application files. Both application and updater must
come from the new complete package.

Windows, from an elevated PowerShell:

```powershell
.\install.ps1 -InstallDir 'C:\Quasar' -ServiceName Quasar -MigrateScheduledTask -TaskName Quasar -Start
```

Stop the old task and its game servers first. The installer refuses a running
legacy task. It disables the old task before native activation, retains a complete backup,
and confirms the new main service with authenticated health/PID checks. On failure
it restores the old application and the task's prior enabled state. Existing named service accounts require an
explicit account migration; the virtual-account installer does not silently change
them. `-DataDir`, `-ServiceName` and `-UpdaterServiceName` support custom locations
and names. `-RepairSupervision` repairs an already copied installation. Do not enable
the old task and the new services together. Check Updates and `update --status`
before removing the retained old installation or task.

The installer grants access to the configured application and data directories.
Grant the main virtual account access to any external worlds, runtime folders,
backup paths or network resources before starting migrated servers. Existing task
credentials cannot be transferred to a service without supplying credentials;
this migration instead uses the selected passwordless virtual accounts.

## Verification scope

`Tests/Updates/systemd.py` installs isolated real systemd units through the production
installer and runs the production updater operation with a fixture application and
sealed local package provider. It covers wrong native ownership/action/account/policy,
saved-token sharing, reported background failures, launch failure, full restoration, duplicate
requests, exact-version health, updater crash/restart, permissions, intentional
shutdown and a detached game-server-process sentinel. It starts no Quasar web host
or actual Space Engineers server.

Windows native-service integration is in `Tests/Updates/windows-services.ps1` and
is configured to run in Windows CI. Shared fixtures test Windows file sharing/locking on Windows;
Linux skips those semantics. Linux execution is not evidence of Windows execution.
GitHub provenance and application preparation have separate deterministic tests;
the native fixture replaces the network package provider, not service control or
replacement/recovery. The CI workflow is `.github/workflows/update-integration.yml`.

New package configuration uses `LinuxPackageAssetName` and
`WindowsPackageAssetName` (`QUASAR_UPDATES_LINUX_PACKAGE_ASSET` /
`QUASAR_UPDATES_WINDOWS_PACKAGE_ASSET`). Legacy Bootstrap asset keys remain only
for recovery. The installer records the trusted repository and package name from
configuration; repair the installation after intentionally changing that trust
source. The updater reuses the existing saved GitHub token/keyring and token
precedence. Keep credentials and Data Protection keys backed up together.
