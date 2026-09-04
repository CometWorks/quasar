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
  - `Quasar.Plugin.Abstractions.dll` for Quasar UI plugin adapter builds
  - Blazor/static assets
  - `Agent/Quasar.Agent.dll`
  - `Agent/Magnetar.Protocol.dll` for Quasar.Agent and UI-plugin-owned
    Magnetar companion builds
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
as `1.0.0.123-pr.45`; Quasar uses that value, plus the active-release pointer, for
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
- push to `main` → full release tagged `v<base>.<build-number>`
- pull request → draft prerelease tagged `pr-<number>/v<base>.<run-number>-pr.<number>`
- manual run (`workflow_dispatch`) → draft prerelease tagged `v<base>.<run-number>-manual`

The updater extracts the version from the tag with
`QuasarReleaseVersion.Normalize`, so the tag prefix does not matter. Assembly/file
metadata is normalized to `major.minor.build`. PR and manual prerelease tags keep
their numeric run number before the channel label, so update checks and release
retention compare them by numeric build first instead of treating `-pr` or
`-manual` as older than the base release.

After publishing, the workflow prunes older GitHub releases with their tags. It
keeps the newest two full active releases, plus the newest two draft/prerelease
review builds per PR/manual stream. Closing or merging a pull request cancels any
in-progress release build for that PR, then deletes all remaining draft releases
and tags whose names begin with that PR's exact `pr-<number>/` prefix.

## First Start

The default systemd user service runs Bootstrap from the extracted install root
and sets `QUASAR_INSTALL_DIR` to that same directory. A machine-wide service is
still available with `install.sh --system`.

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
`WebService/` workers, managed web releases, or explicitly configured
`QUASAR_WEB_EXE` / `QUASAR_WEB_DLL` workers are trusted.

Bootstrap always captures the managed web UI worker's stdout/stderr and mirrors
it to its own console. For systemd installs, Quasar web UI warnings and errors
therefore appear in the service journal as well as in the configured Quasar log
files.

The UI **Shutdown Quasar** action drains the web worker, preserves managed
servers, and leaves Bootstrap running without a worker. Because Bootstrap is
still alive and exits successfully only when the service is stopped, systemd does
not restart the worker by itself. Run `systemctl --user restart quasar.service`
for the default user service, or `sudo systemctl restart quasar.service` for a
system service, to start the UI and supervisor again.

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
the install root (`$QUASAR_INSTALL_DIR/Updates/appsettings.base.json`) as the
merge base, applies local values from the install directory, and writes the
resolved file into the staged worker. If the merge
conflicts, auto-staging stops with a warning and `/settings/updates` shows a
three-pane resolver: current and incoming files side-by-side, with an editable
final file below. **Take current** and **Take incoming** copy into the final
editor without saving; review or edit that result, then choose **Save
resolution**.

Activation is explicit and requires the worker to be running under Bootstrap.
The UI copies the staged payload into
`ManagedRuntime/WebService/<version>`, writes the active-release pointer to that
managed worker, updates the install-directory `appsettings.json` from the
resolved staged file, and clears old staged payloads. Bootstrap copies that
install-directory file into the managed worker before launch, observes the
pointer change, drains the old worker, starts the managed worker on the same
public port, and leaves managed Magnetar servers running. The browser shows a
restart progress overlay, polls `/api/health` until the activated UI version is
serving, then reloads the Updates page. After a successful cutover, Bootstrap
prunes inactive managed web-release directories.

This intentionally accepts a short web/agent disconnect. `Quasar.Agent`
reconnects, and managed Magnetar processes stay alive because Quasar launches
them detached with `-daemon`. Reconnect is startup-pending until the first
telemetry snapshot arrives, so health recovery uses the startup grace instead of
the shorter heartbeat timeout during rollover. Running DS processes keep the
agent assembly they already loaded until that server process is stopped. On
worker startup and each reconcile after reconnect, the supervisor compares the
bundled `Agent/Quasar.Agent.dll` hash with the deployed Magnetar local DLL hash. When
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

Packaged installs can run Quasar with only the runtime. `install.sh` does not
proactively install an SDK for QuasarHub. When an administrator installs or
updates a source-built UI plugin, Quasar first uses a compatible .NET 10 SDK on
`PATH`, then a previously downloaded private SDK. If neither exists, the UI asks
whether to download the pinned SDK into
`{Quasar data}/ManagedRuntime/Tools/DotNetSdk/{version}`. The administrator can
instead cancel, install the SDK through the system package manager, and retry.
The private install needs no `sudo`, does not alter `PATH`, and does not modify
the system package database. Plugin sources are downloaded as pinned GitHub
archives; Git is not required on the host.

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh          # install user quasar.service
~/.local/share/Quasar/install.sh --start  # also start the user service immediately
```

For extracted release installers, `install.sh` uses the script directory as the
install root. Source installs keep using `~/.local/share/Quasar` as the default
install root. Use `--system` with `sudo` for a machine-wide service or
`--install-dir <dir>` to install Quasar elsewhere. The generated service sets
`HOME` and `QUASAR_INSTALL_DIR` explicitly so Bootstrap and the worker agree on
the unified update/runtime state root.
The installer enables the service but does not start or restart it unless
`--start` is passed; start it later with
`systemctl --user restart quasar.service`. When installing from source instead
of an extracted release archive, the installer stamps the launcher with
`VERSION`, an exact git tag, or a short commit-derived prerelease identity so
Bootstrap update comparisons do not fall back to plain `1.0.0`.

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

## Relocating the install root

Stop Quasar and its managed servers, then copy the complete install root to the
new location and rerun `install.sh --install-dir <new-root>` so systemd receives
the new `QUASAR_INSTALL_DIR`. Blank managed server paths and relative overrides
follow the new root automatically. Absolute overrides remain at their configured
external locations.

Older `server.json` files may contain materialized absolute defaults. Opening
and saving the server canonicalizes defaults under the current root. If both old
and new server-data trees exist, first copy the authoritative tree manually;
then use **Edit Server -> Paths -> Use Managed Defaults**. Quasar deliberately
does not choose between, merge, move, or delete two existing trees.

## Configuration

For the web UI host/port (including how to change the listening port, default
`8080`) and browser auto-open behavior, see [Configuration](Configuration.md).

Update defaults live in `Quasar:Updates`. Packaged defaults and operator
overrides live in the install root (`$QUASAR_INSTALL_DIR/appsettings.json`).
The worker and Bootstrap both read that install-root file on startup.

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

## GitHub Token for Update Checks

Quasar can check GitHub releases without a token, but hosts on shared servers,
NAT gateways, and public cloud IP ranges can hit GitHub's unauthenticated rate
limit. The Updates page lets an admin save a GitHub token for release checks.
It is stored in `github-updates.json` under the Quasar install directory with the
same Data Protection encryption model and owner-only Unix permissions used for
the Steam Workshop API key.

The same token is handed to every managed Magnetar start through the
`PULSAR_GITHUB_TOKEN` environment variable so Pulsar's plugin-hub downloads are
authenticated too. It is never placed on the server command line, and a
`-github-token` flag typed into a server's launch arguments is removed before
start. The token is masked in the launch-environment log that
`LogLaunchEnvironment` prints. Magnetar builds older than 2.3.3.0 still receive
the token as `-github-token` on the command line; this fallback is temporary and
will be removed in the first Quasar release of 2027.

All GitHub update, runtime, and plugin-catalog GET requests retry transient
network failures and HTTP `408`, `429`, or `5xx` responses up to four times.
Backoff starts at one second, honors `Retry-After`, and is capped at 30 seconds.
A persistent outage is reported without stopping Quasar; scheduled checks can
recover when connectivity returns.

Use a classic personal access token without any permissions:

1. Open GitHub's classic token creation page:
   <https://github.com/settings/tokens/new>.
2. Set Note to something recognizable, such as `Quasar update checks`.
3. Set Expiration to a date you can renew before it ends.
4. Do not select any scopes or permissions. Quasar reads public release metadata
   and public release assets; the token is only needed so GitHub treats the
   requests as authenticated for rate limiting.
5. Generate the token, copy it once, paste it into Settings, Updates, GitHub
   token, then press Save token.
6. Press Check Quasar to verify the token and update status.

GitHub documents that unauthenticated REST API requests are rate limited by IP,
while authenticated requests use the authenticated user's primary rate limit:
<https://docs.github.com/rest/using-the-rest-api/rate-limits-for-the-rest-api>.

When GitHub reports a token expiration, Quasar records it in memory. If the
token is expired, expiring soon, or rejected during an update check, the Updates
page shows a warning and the notification icon in the app bar gets a warning
badge. Save a fresh token before the old one expires to keep automatic checks
working.

**Warning:** prerelease updates are for testing only and should not be used by
regular users. They may be unstable, may require manual recovery, and may update
both the UI worker and Bootstrap launcher.
