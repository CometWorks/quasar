# Linux Deployment and Updates

Quasar Linux deployment is split into a stable Bootstrap layer and a replaceable
web UI worker.

## Release Assets

`scripts/package-linux-release.sh` produces:

- `quasar-installer-linux.tar.gz`
  - top-level `Quasar/` directory
  - `Quasar` Bootstrap launcher
  - `install.sh`
  - `uninstall.sh`
  - default `appsettings.json`
- `quasar-web-linux-x64.tar.gz`
  - web worker executable `Quasar`
  - Blazor/static assets
  - `Agent/Quasar.Agent.dll`
  - `Agent/Magnetar.Protocol.dll`
- `SHA256SUMS`

The unified release workflow (`.github/workflows/release.yml`) builds the Linux
and Windows assets in parallel and attaches all of them to a single GitHub
release. Tag pushes and pushes to `main` publish a full release; pull requests
publish a draft prerelease for review. The release carries one combined
`SHA256SUMS` covering every archive, and the updater locates the asset it needs
by name, so all platforms share the same release.
`Version` is taken from `scripts/package-linux-release.sh` and can fall back to a git value.
For assembly/file metadata, the script always emits a valid `major.minor.build`
version even when the base version is build-number style. The public update
identity is `AssemblyInformationalVersion`, which keeps prerelease labels such
as `1.0.0-main.7`; Quasar uses that value, plus the active-release pointer, for
update comparisons instead of `AssemblyVersion`.
For NuGet/package metadata, non-tag/short-hash values are mapped to a safe
`1.0.0-<hash>` semver pre-release form so restore/publish do not fail. The
packaging script copies the published web worker, overlays the complete source
`Quasar/wwwroot/` tree, and fails if the web payload is missing the worker,
generated Blazor runtime, or generated MudBlazor assets. The full `wwwroot`
overlay keeps manually managed scripts, CSS, and library files in the release
archive even when publish output shape changes.
The bundled `Quasar.Agent.dll` is not release-version stamped. Agent deploy
drift is detected by comparing the bundled DLL SHA-256 hash with the deployed
Magnetar local-agent DLL hash, so version-only release changes do not force an
agent restart warning.
The workflow caches only the `DedicatedServer64/` reference library set by the
Space Engineers Dedicated Server public build id, so unchanged DS builds restore
without re-downloading the multi-GB depot content.

## Release Tags

The release workflow is `.github/workflows/release.yml`. Each build publishes a
single release/tag carrying both the Linux and Windows archives:

- tag push `v<version>` → full release tagged `v<version>`
- push to `main` → full release tagged `v1.0.0-main.<run-number>`
- pull request → draft prerelease tagged `pr-<number>/v1.0.0-pr.<number>.<run-number>`
- manual run (`workflow_dispatch`) → draft prerelease tagged `v1.0.0-manual.<run-number>`

The updater extracts the version from the tag with
`QuasarReleaseVersion.Normalize`, so the tag prefix does not matter. Assembly/file
metadata is normalized to `major.minor.build`.

## First Start

The default systemd user service runs Bootstrap from the extracted install root
and sets `QUASAR_DATA_DIR` to that same directory. It also sets
`QUASAR_SYSTEMD_SERVICE` and `QUASAR_SYSTEMD_SCOPE` so the web UI's **Shutdown
Quasar** action can request `systemctl --user stop quasar.service` instead of
only exiting the launcher. A machine-wide service is still available with
`install.sh --system`.

If Bootstrap has no usable `Updates/active-release.json` and no packaged
`WebService/Quasar`, it downloads the latest Linux web asset from GitHub,
extracts it under:

```text
<install-root>/ManagedRuntime/WebService/<version>
```

Then it writes `Updates/active-release.json` pointing at the managed active
worker. `Updates/Staged/` is reserved for not-yet-activated update payloads.
The downloaded archive must match the release's `SHA256SUMS` entry before it is
extracted.
When running as a systemd service, Bootstrap ignores a stale active-release
pointer that targets a random external build directory. Only packaged
`WebService/` workers, managed web releases, staged legacy workers, or explicitly
configured `QUASAR_WEB_EXE` / `QUASAR_WEB_DLL` workers are trusted. If Bootstrap
finds an older active pointer that still targets `Updates/Staged/<version>`, it
migrates that release into `ManagedRuntime/WebService/<version>` before launch.
On startup, Bootstrap also migrates a legacy default data root at
`~/.config/Quasar` into the install root unless `QUASAR_DATA_DIR` points to a
custom directory.

## UI Worker Updates

The running Quasar UI checks GitHub releases every 15 minutes by default. The
Updates page lists selectable Linux web assets from the configured release
stream, including older versions so an operator can stage a rollback. When
`AutoStageWebUpdates` is enabled and a newer web asset exists, Quasar downloads
and stages it automatically, then shows an in-app notification and the
`/settings/updates` page marks it ready. When auto-staging is disabled, releases
are only queued until the operator stages the selected version. Staging requires
a matching `SHA256SUMS` entry for the downloaded asset.

Staging also resolves `appsettings.json`. Quasar uses the stored release base in
the data directory (`$QUASAR_DATA_DIR/Updates/appsettings.base.json`) as the
merge base, applies local values from the install directory
(`<install-root>` by default), and writes the resolved file into the staged worker. If the merge
conflicts, auto-staging stops with a warning and `/settings/updates` shows a
git-style conflict editor. Resolve and save the JSON there, or choose **Force
release defaults** to stage the release file without local appsettings values.

Activation is explicit. The UI copies the staged payload into
`ManagedRuntime/WebService/<version>`, writes the active-release pointer to that
managed worker, updates the install-directory `appsettings.json` from the
resolved staged file, and clears old staged payloads. Bootstrap copies that
install-directory file into the managed worker before launch, observes the
pointer change, drains the old worker, starts the managed worker on the same
public port, and leaves managed Magnetar servers running. After a successful
cutover, Bootstrap prunes inactive managed web-release directories.

This intentionally accepts a short web/agent disconnect. `Quasar.Agent`
reconnects, and managed Magnetar processes stay alive because Quasar launches
them detached with `-daemon`. Running DS processes keep the agent assembly they
already loaded until that server process is stopped. On worker startup and each
reconcile after reconnect, the supervisor compares the bundled
`Agent/Quasar.Agent.dll` hash with the deployed Magnetar local DLL hash. When
they differ, Quasar warns that a manual server restart is required. It does not
auto-schedule that restart; the operator-triggered stop/start path runs launch
preparation and injects the bundled deployable DLL before relaunch.

## Managed Runtime Update Checks

The Updates page always shows the currently installed Quasar, Bootstrap,
Magnetar, and Space Engineers Dedicated Server versions when Quasar can resolve
them from release metadata, Dedicated Server `SE_VERSION` assembly metadata, or
non-placeholder executable file versions. It also shows the managed runtime
install paths and the most recent managed-runtime check time.

Quasar UI worker and Bootstrap checks use the Quasar release checker interval
(15 minutes by default) and the page's **Check Quasar** button. Managed Magnetar
checks run during startup readiness and then every hour while Quasar is running;
the page's **Check Magnetar** button runs the same check immediately. Managed DS
checks run during startup readiness; **Check Dedicated Server** runs SteamCMD
`app_update 298740 validate` immediately so an operator does not need to wait
for a restart to verify or refresh the DS install.

## Bootstrap Updates

Bootstrap checks the primary Quasar release stream every 15 minutes by default.
When it finds an actually newer `quasar-installer-linux.tar.gz` asset (semver
core and prerelease compared against the running launcher's release identity), it
verifies the release's `SHA256SUMS` entry, extracts the archive, strips the
single top-level `Quasar` directory, replaces the installed launcher files,
drains the UI worker, and exits with a failure code so systemd restarts the
updated launcher. Existing `appsettings.json` is preserved.
Bootstrap must not drain the worker for a release whose normalized version is
the same as the running launcher; it also skips drain/restart if the downloaded
launcher is byte-identical to the installed launcher, which prevents a repeated
self-update loop when a source-built launcher reports stale version metadata.

If `/settings/updates` has already detected a Bootstrap update and Quasar is
running under Bootstrap, the **Force activate** button writes a
`Updates/bootstrap-update-request.json` request containing the detected
version and platform asset. Bootstrap watches for that file, consumes it, and
runs the same verified self-update path for that requested release immediately
instead of waiting for the next 15-minute monitor tick. Managed Magnetar servers
stay running; the web UI reconnects after the launcher restarts.

## Install

The first install uses the Linux installer flow from an extracted
`quasar-installer-linux.tar.gz`:

If .NET 10 is missing, `install.sh` detects the available package manager (`apt`,
`dnf`, `yum`, `pacman`, or `zypper`), prints the exact commands it would run, and
asks before installing anything. The preview includes the package install command
and a conditional `/usr/local/bin/dotnet` PATH-link command in case the package
manager installs dotnet but does not expose it on `PATH`. Source installs require
the .NET 10 SDK for `dotnet publish`; no-build/package installs require the .NET
10 ASP.NET Core runtime, which includes the base .NET runtime. Declining the
prompt exits before files or services are changed. On Debian 13, the apt flow
first adds Microsoft's Debian 13 package feed with
`packages-microsoft-prod.deb`, then runs `apt-get update` and installs the
selected .NET package.

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh          # install user quasar.service
~/.local/share/Quasar/install.sh --start  # also start the user service immediately
```

For extracted release installers, `install.sh` uses the script directory as the
default install directory and the default Quasar data directory. Source installs
keep using `~/.local/share/Quasar` as the default install root, with state stored
there as well. Use `--system` with `sudo` for a machine-wide service,
`--install-dir <dir>` to copy Quasar elsewhere, or `--data-dir <dir>` to place
Quasar state elsewhere. The generated service sets `HOME` and `QUASAR_DATA_DIR`
explicitly so Bootstrap and the worker agree on the update/runtime state root.
It also records the unit name/scope in `QUASAR_SYSTEMD_SERVICE` and
`QUASAR_SYSTEMD_SCOPE`; with those set, the UI shutdown button asks systemd to
stop the installed unit. The
installer enables the service but does not start or restart it unless `--start`
is passed; start it later with `systemctl --user restart quasar.service`. When
installing from source instead of an extracted release archive, the installer
stamps the launcher with `VERSION`, an exact git tag, or a short commit-derived
prerelease identity so Bootstrap update comparisons do not fall back to plain
`1.0.0`.

If a previous `/opt/quasar` system install exists, the new user-mode installer
installs the new Bootstrap and user service first, then calls the old
`/opt/quasar/uninstall.sh --purge` through `sudo` to stop and remove the old
system service and files.

Raising managed server priority no longer requires granting `CAP_SYS_NICE` to
the whole Quasar service. The installer can build and install a narrow setuid
root helper when the feature is needed:

```bash
/tmp/Quasar/install.sh --install-renice-helper --no-build --no-enable
```

The helper is installed as `/usr/local/bin/quasar-renice`, accepts only Quasar's
known nice values, requires the target process to be owned by the caller, and
checks that the target executable basename is one of Quasar's Magnetar launcher
names before calling `setpriority`.

```bash
~/.local/share/Quasar/uninstall.sh           # remove the user systemd service
~/.local/share/Quasar/uninstall.sh --purge   # also remove the install/data root
```

`uninstall.sh` runs `systemctl stop quasar.service` before disabling and removing
the service. With `--service-name <name>`, it stops the matching `<name>.service`
unit instead. Use `sudo ./uninstall.sh --system --purge` from a system install
to remove a machine-wide service.

## Configuration

For the web UI host/port (including how to change the listening port, default
`8080`) and browser auto-open behavior, see [Configuration](Configuration.md).

Update defaults live in `Quasar:Updates`. Packaged defaults come from the install
directory, and operator overrides can live in the Quasar data directory
(`<install-root>/appsettings.json` by default for Linux systemd installs, or
`QUASAR_DATA_DIR/appsettings.json` when overridden). The worker and Bootstrap
both read that data-directory file on startup.

```json
{
  "Enabled": true,
  "Owner": "CometWorks",
  "Repository": "quasar",
  "IncludePrerelease": false,
  "AutoStageWebUpdates": true,
  "CheckIntervalSeconds": 900,
  "LinuxWebAssetName": "quasar-web-linux-x64.tar.gz",
  "LinuxBootstrapAssetName": "quasar-installer-linux.tar.gz"
}
```

Environment overrides:

- `QUASAR_UPDATES_ENABLED`
- `QUASAR_UPDATES_OWNER`
- `QUASAR_UPDATES_REPOSITORY`
- `QUASAR_UPDATES_INCLUDE_PRERELEASE`
- `QUASAR_UPDATES_AUTO_STAGE_WEB`
- `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS`
- `QUASAR_UPDATES_LINUX_WEB_ASSET`
- `QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET`

The Updates page exposes an "Include prerelease versions" switch. Enabling it
writes `Quasar:Updates:IncludePrerelease` to the data-directory `appsettings.json`
and immediately refreshes worker-side release checks so prerelease UI versions
become selectable. Bootstrap also honors the same setting after its next restart.
The page also exposes an automatic-staging checkbox backed by
`Quasar:Updates:AutoStageWebUpdates`; disabling it keeps new UI versions queued
until the operator chooses a version and presses Stage.

**Warning:** prerelease updates are for testing only and should not be used by
regular users. They may be unstable, may require manual recovery, and may update
both the UI worker and Bootstrap launcher.
