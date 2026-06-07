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

The GitHub release workflow builds these assets on Linux and attaches them to
GitHub releases. Tag pushes and pushes to `main` publish both UI and Bootstrap
streams as full releases. Pull requests publish both streams as draft
prereleases for review.
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

## First Start

The systemd service runs Bootstrap from `/opt/quasar/Quasar serve --quiet`.

If Bootstrap has no usable `Updates/active-release.json` and no packaged
`WebService/Quasar`, it downloads the latest Linux web asset from GitHub,
extracts it under:

```text
~/.config/Quasar/Updates/Staged/<version>
```

Then it writes `Updates/active-release.json` pointing at the staged worker.
The downloaded archive must match the release's `SHA256SUMS` entry before it is
extracted.

## UI Worker Updates

The running Quasar UI checks GitHub releases every 5 minutes by default. When a
new Linux web asset exists, Quasar downloads and stages it automatically, then
shows an in-app notification and the `/settings/updates` page marks it ready.
Staging requires a matching `SHA256SUMS` entry for the downloaded asset.

Activation is explicit. The UI writes a new active-release pointer to the staged
worker. Bootstrap observes that pointer change, drains the old worker, starts
the staged worker on the same public port, and leaves managed Magnetar servers
running.

This intentionally accepts a short web/agent disconnect. `Quasar.Agent`
reconnects, and managed Magnetar processes stay alive because Quasar launches
them detached with `-daemon`.

## Bootstrap Updates

Bootstrap checks the primary Quasar release stream every 5 minutes by default.
When it finds an actually newer `quasar-linux-x64.tar.gz` asset (semver core and
prerelease compared against the running launcher's release identity), it verifies
the release's `SHA256SUMS` entry, extracts the archive, replaces the installed
launcher files, drains the UI worker, and exits with a failure code so systemd
restarts the updated launcher. Existing `appsettings.json` is preserved.
Bootstrap must not drain the worker for a release whose normalized version is
the same as the running launcher.

The first install still uses the Linux installer flow:

```bash
tar -xzf quasar-linux-x64.tar.gz -C /tmp/quasar
sudo /tmp/quasar/install.sh --start
```

## Configuration

Defaults live in `Quasar:Updates`:

```json
{
  "Enabled": true,
  "Owner": "viktor-ferenczi",
  "Repository": "Quasar",
  "IncludePrerelease": false,
  "CheckIntervalSeconds": 300,
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
