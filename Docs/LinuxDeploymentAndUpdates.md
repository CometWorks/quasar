# Linux Deployment and Updates

Quasar Linux deployment is split into a stable Bootstrap layer and a replaceable
web UI worker.

## Release Assets

`scripts/package-linux-release.sh` produces:

- `quasar-bootstrap-linux-x64.tar.gz`
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
tagged GitHub releases.

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

Bootstrap update availability is detected from the same GitHub release and shown
in the Updates page. Installing a new Bootstrap is separate from UI worker
updates because replacing `/opt/quasar/Quasar` and systemd service files may
require root privileges.

For now, install the newer Bootstrap package through the Linux installer flow:

```bash
tar -xzf quasar-bootstrap-linux-x64.tar.gz -C /tmp/quasar-bootstrap
sudo /tmp/quasar-bootstrap/install.sh --start
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
  "LinuxBootstrapAssetName": "quasar-bootstrap-linux-x64.tar.gz"
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
