# Windows Deployment and Updates

Quasar's Windows deployment mirrors the [Linux model](LinuxDeploymentAndUpdates.md):
a stable Bootstrap launcher layer plus a replaceable web UI worker. The only
differences are platform conventions — `.zip` archives instead of `.tar.gz`, a
`Quasar.exe` executable instead of the extension-less `Quasar`, and a Scheduled
Task keep-alive instead of a systemd service (a Windows Service is intentionally
out of scope; see `Docs/QuasarArchitecture.md`).

## Release Assets

`scripts/package-windows-release.ps1` produces the same logical assets as the
Linux packager, under `artifacts/windows/`:

- `quasar-win-x64.zip`
  - root `Quasar.exe` Bootstrap launcher
  - `install.ps1`
  - `uninstall.ps1`
  - default `appsettings.json`
- `quasar-web-win-x64.zip`
  - web worker executable `Quasar.exe`
  - Blazor/static assets
  - `Agent/Quasar.Agent.dll`
  - `Agent/Magnetar.Protocol.dll`
- `SHA256SUMS`

`SHA256SUMS` entries are written as `<lowercase-hash>  <filename>`, matching the
format the updater verifies. Archives are created with
`System.IO.Compression.ZipFile` (Optimal, no base directory) so entries sit at
the archive root, symmetric with the worker's `ZipFile.ExtractToDirectory`.

Version normalization is identical to `scripts/package-linux-release.sh`: the same
input yields the same `AssemblyVersion`/`FileVersion` (`major.minor.build`) and
the same `AssemblyInformationalVersion` (which keeps prerelease labels such as
`0.1.0-main.7` and drives update comparisons).

The GitHub workflow `.github/workflows/release-windows.yml` builds these assets on
`windows-latest`, caching only `DedicatedServer64/` keyed by the Space Engineers
Dedicated Server public build id. It installs the SE DS reference set with the
native Windows depot (no `+@sSteamCmdForcePlatformType` override) and the Magnetar
PluginSdk from the latest full `MagnetarForWindows-*.7z` release asset.

## Release Tags

Windows publishes to its own tags so each OS keeps a distinct `SHA256SUMS` and the
two workflows never race to create the same release:

- Launcher: `win/v<version>`
- UI: `quasar-ui-win/v<version>`

The `win/` prefix carries no digits, so `QuasarReleaseVersion.Normalize` yields the
same `<version>` used on Linux — version comparison stays correct. Asset discovery
is by asset name (`quasar-win-x64.zip` / `quasar-web-win-x64.zip`), not by tag, so
the Windows updater always finds the Windows assets.

## First Start

The Scheduled Task runs Bootstrap as `Quasar.exe serve --quiet` from the install
directory (default `%ProgramFiles%\Quasar`).

If Bootstrap has no usable `Updates/active-release.json` and no packaged
`WebService/Quasar.exe`, it downloads the latest Windows web asset from GitHub and
extracts it under:

```text
%APPDATA%\Quasar\Updates\Staged\<version>
```

Then it writes `Updates/active-release.json` pointing at the staged worker. The
downloaded archive must match the release's `SHA256SUMS` entry before extraction.

## UI Worker Updates

Identical to Linux: the running UI checks GitHub releases every 5 minutes by
default, downloads and stages a newer `quasar-web-win-x64.zip` (after verifying its
`SHA256SUMS` entry), and surfaces it on `/settings/updates`. Activation is
explicit; Bootstrap drains the old worker, starts the staged `Quasar.exe` on the
same port, and leaves managed Magnetar servers running.

## Bootstrap Updates

Bootstrap checks the primary Quasar release stream every 5 minutes. When it finds a
genuinely newer `quasar-win-x64.zip`, it verifies the `SHA256SUMS` entry, extracts
the archive, and replaces the installed launcher files (renaming a running `.exe`
is permitted on Windows). Existing `appsettings.json` is preserved.

Because there is no systemd on Windows, the launcher restarts itself: after
applying the update it spawns a detached `Quasar.exe serve --quiet` and exits `0`.
The Scheduled Task keep-alive (restart-on-failure) is the safety net if the
detached relaunch ever fails to come up. On Linux the launcher still exits `75` for
systemd to restart it — that path is unchanged.

## Install

```powershell
# From an extracted quasar-win-x64.zip, in an elevated PowerShell:
.\install.ps1            # install to %ProgramFiles%\Quasar and register the task
.\install.ps1 -Start     # also start the task immediately
```

`install.ps1` registers a Scheduled Task (`Quasar` by default) that starts at boot
and restarts the launcher on failure. The task action runs through `cmd.exe` so the
service-mode environment variables (`QUASAR_MODE=Service`,
`QUASAR_OPEN_BROWSER_ON_START=false`) are set for the worker, mirroring the systemd
`Environment=` lines. It runs as `SYSTEM` by default; pass `-User <name>` for a
specific service account.

```powershell
.\uninstall.ps1          # stop and remove the task
.\uninstall.ps1 -Purge   # also delete the install directory
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
  "LinuxBootstrapAssetName": "quasar-linux-x64.tar.gz",
  "WindowsWebAssetName": "quasar-web-win-x64.zip",
  "WindowsBootstrapAssetName": "quasar-win-x64.zip"
}
```

The updater resolves the Windows asset names on Windows and the Linux names
elsewhere. Environment overrides:

- `QUASAR_UPDATES_ENABLED`
- `QUASAR_UPDATES_OWNER`
- `QUASAR_UPDATES_REPOSITORY`
- `QUASAR_UPDATES_INCLUDE_PRERELEASE`
- `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS`
- `QUASAR_UPDATES_LINUX_WEB_ASSET`
- `QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET`
- `QUASAR_UPDATES_WINDOWS_WEB_ASSET`
- `QUASAR_UPDATES_WINDOWS_BOOTSTRAP_ASSET`
