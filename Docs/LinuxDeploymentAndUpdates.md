# Linux Deployment and Updates

Quasar Linux deployment is split into a stable Bootstrap layer and a replaceable
web UI worker.

## Release Assets

`scripts/package-linux-release.sh` produces:

- `quasar-linux-x64.tar.gz`
  - root `Quasar` Bootstrap launcher
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
as `0.1.0-main.7`; Quasar uses that value, plus the active-release pointer, for
update comparisons instead of `AssemblyVersion`.
For NuGet/package metadata, non-tag/short-hash values are mapped to a safe
`0.1.0-<hash>` semver pre-release form so restore/publish do not fail. The
packaging script copies the published web worker, overlays the complete source
`Quasar/wwwroot/` tree, and fails if the web payload is missing the worker,
generated Blazor runtime, or generated MudBlazor assets. The full `wwwroot`
overlay keeps manually managed scripts, CSS, and library files in the release
archive even when publish output shape changes.
The workflow caches only the `DedicatedServer64/` reference library set by the
Space Engineers Dedicated Server public build id, so unchanged DS builds restore
without re-downloading the multi-GB depot content.

## Release Tags

The release workflow is `.github/workflows/release.yml`. Each build publishes a
single release/tag carrying both the Linux and Windows archives:

- tag push `v<version>` → full release tagged `v<version>`
- push to `main` → full release tagged `v0.1.0-main.<run-number>`
- pull request → draft prerelease tagged `pr-<number>/v0.1.0-pr.<number>.<run-number>`
- manual run (`workflow_dispatch`) → draft prerelease tagged `v0.1.0-manual.<run-number>`

The updater extracts the version from the tag with
`QuasarReleaseVersion.Normalize`, so the tag prefix does not matter. Assembly/file
metadata is normalized to `major.minor.build`.

## First Start

The systemd service runs Bootstrap from `/opt/quasar/Quasar serve --quiet`.

If Bootstrap has no usable `Updates/active-release.json` and no packaged
`WebService/Quasar`, it downloads the latest Linux web asset from GitHub,
extracts it under:

```text
~/.config/Quasar/ManagedRuntime/WebService/<version>
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

## UI Worker Updates

The running Quasar UI checks GitHub releases every 15 minutes by default. When a
new Linux web asset exists, Quasar downloads and stages it automatically, then
shows an in-app notification and the `/settings/updates` page marks it ready.
Staging requires a matching `SHA256SUMS` entry for the downloaded asset.

Activation is explicit. The UI copies the staged payload into
`ManagedRuntime/WebService/<version>`, writes the active-release pointer to that
managed worker, and clears old staged payloads. Bootstrap observes the pointer
change, drains the old worker, starts the managed worker on the same public port,
and leaves managed Magnetar servers running. After a successful cutover,
Bootstrap prunes inactive managed web-release directories.

This intentionally accepts a short web/agent disconnect. `Quasar.Agent`
reconnects, and managed Magnetar processes stay alive because Quasar launches
them detached with `-daemon`.

## Bootstrap Updates

Bootstrap checks the primary Quasar release stream every 15 minutes by default.
When it finds an actually newer `quasar-linux-x64.tar.gz` asset (semver core and
prerelease compared against the running launcher's release identity), it verifies
the release's `SHA256SUMS` entry, extracts the archive, replaces the installed
launcher files, drains the UI worker, and exits with a failure code so systemd
restarts the updated launcher. Existing `appsettings.json` is preserved.
Bootstrap must not drain the worker for a release whose normalized version is
the same as the running launcher; it also skips drain/restart if the downloaded
launcher is byte-identical to the installed launcher, which prevents a repeated
self-update loop when a source-built launcher reports stale version metadata.

## Install

The first install uses the Linux installer flow from an extracted
`quasar-linux-x64.tar.gz`:

If .NET 10 is missing, `install.sh` detects the available package manager (`apt`,
`dnf`, `yum`, `pacman`, or `zypper`), prints the exact commands it would run, and
asks before installing anything. The preview includes the package install command
and a conditional `/usr/local/bin/dotnet` PATH-link command in case the package
manager installs dotnet but does not expose it on `PATH`. Source installs require
the .NET 10 SDK for `dotnet publish`; no-build/package installs require the .NET
10 ASP.NET Core runtime, which includes the base .NET runtime. Declining the
prompt exits before files or services are changed.

```bash
tar -xzf quasar-linux-x64.tar.gz -C /tmp/quasar
sudo /tmp/quasar/install.sh          # publish to /opt/quasar and install quasar.service
sudo /tmp/quasar/install.sh --start  # also start the service immediately
```

`install.sh` publishes Quasar to `/opt/quasar` and installs `quasar.service`. The
service grants `CAP_SYS_NICE` through systemd ambient capabilities so Quasar can
raise managed server priority via `renice`. The installer enables the service but
does not start or restart it unless `--start` is passed; start it later with
`sudo systemctl restart quasar.service`. When installing from source instead of
an extracted release archive, the installer stamps the launcher with `VERSION`,
an exact git tag, or a short commit-derived prerelease identity so Bootstrap
update comparisons do not fall back to plain `0.1.0`.

```bash
sudo ./uninstall.sh           # remove the systemd service
sudo ./uninstall.sh --purge   # also remove /opt/quasar
```

`uninstall.sh` runs `systemctl stop quasar.service` before disabling and removing
the service. With `--service-name <name>`, it stops the matching `<name>.service`
unit instead.

## Configuration

For the web UI host/port (including how to change the listening port, default
`8080`) and browser auto-open behavior, see [Configuration](Configuration.md).

Update defaults live in `Quasar:Updates`. Packaged defaults come from the install
directory, and operator overrides can live in the Quasar data directory
(`~/.config/Quasar/appsettings.json`, or `QUASAR_DATA_DIR/appsettings.json` when
overridden). The worker and Bootstrap both read that data-directory file on
startup.

```json
{
  "Enabled": true,
  "Owner": "viktor-ferenczi",
  "Repository": "Quasar",
  "IncludePrerelease": false,
  "CheckIntervalSeconds": 900,
  "LinuxWebAssetName": "quasar-web-linux-x64.tar.gz",
  "LinuxBootstrapAssetName": "quasar-linux-x64.tar.gz"
}
```

Environment overrides:

- `QUASAR_UPDATES_ENABLED`
- `QUASAR_UPDATES_OWNER`
- `QUASAR_UPDATES_REPOSITORY`
- `QUASAR_UPDATES_INCLUDE_PRERELEASE`
- `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS`
- `QUASAR_UPDATES_LINUX_WEB_ASSET`
- `QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET`

The Updates page exposes an "Include prerelease versions" switch. Enabling it
writes `Quasar:Updates:IncludePrerelease` to the data-directory `appsettings.json`
and immediately affects worker-side release checks. Bootstrap also honors the
same setting after its next restart.

**Warning:** prerelease updates are for testing only and should not be used by
regular users. They may be unstable, may require manual recovery, and may update
both the UI worker and Bootstrap launcher.
