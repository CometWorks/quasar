# Docker

How to run Quasar from the official container image with Docker Compose: the image and its tags, the `.env` settings, persistent state, networking for game servers, upgrades, backups, and what differs from a native install.

## The image

- Name: `ghcr.io/cometworks/quasar`. Platform: `linux/amd64` only. There is no ARM or Windows image.
- Tags: `latest`, `<version>` (for example `1.1.0.31`) and `v<version>`. Quasar publishes them for full releases built from `main`. Pull-request and manual draft releases get no image.
- Contents: the checksummed `quasar-web-linux-x64.tar.gz` worker from the matching GitHub release, on top of `mcr.microsoft.com/dotnet/sdk:10.0`, plus `curl`, `procps` and the 32-bit libraries `lib32gcc-s1` and `lib32stdc++6`.
- The base is the .NET 10 SDK, so QuasarHub UI plugin builds use that SDK and never ask to download Quasar's private SDK.
- The image runs the web worker (`/opt/quasar/Quasar`) directly. It has no Bootstrap launcher.
- It has no local Host executor, so clusters need an enrolled Linux host machine. See [Cluster hosts](ClusterHosts.md#the-local-host).
- The Dockerfile has no `USER` line, so Quasar runs as root inside the container.
- Built-in health check: `curl http://127.0.0.1:8080/api/health` every 30 seconds.

## Start with Docker Compose

Requirements: Docker Engine on Linux and Docker Compose 2.24 or newer. Use the `docker-compose.yml` and `.env.example` from the Quasar repository.

```bash
cp .env.example .env
# edit .env: replace YOUR_17_DIGIT_STEAM_ID with your SteamID64
docker compose pull
docker compose up -d
docker compose logs -f quasar
```

Then open `http://127.0.0.1:8080` and sign in with Steam as the administrator from `QUASAR_ADMIN_STEAM_ID`. Continue with [First setup](FirstSetup.md).

### What docker-compose.yml sets

| Setting | Value | Why |
| --- | --- | --- |
| `image` | `ghcr.io/cometworks/quasar:${QUASAR_VERSION:-latest}` | Version from `.env` |
| `container_name` | `quasar` | |
| `restart` | `unless-stopped` | |
| `init` | `true` | Reaps child processes of game servers |
| `network_mode` | `host` | Game server ports are opened directly on the host |
| `cap_add` | `SYS_NICE` | Lets Quasar raise managed server priority |
| `env_file` | `.env` (optional) | Every variable in `.env` reaches the container |
| `volumes` | `./quasar-data:/data` | All persistent state |
| `stop_grace_period` | `30m` | Time for managed servers to stop cleanly |

Compose refuses to start with `Set QUASAR_ADMIN_STEAM_ID in .env` when that variable is missing.

## .env settings

| Variable | Default in `.env.example` | Meaning |
| --- | --- | --- |
| `QUASAR_VERSION` | `latest` | Image tag: `latest`, `v1.1.0.31` or `1.1.0.31` |
| `QUASAR_ADMIN_STEAM_ID` | `YOUR_17_DIGIT_STEAM_ID` | First administrator's SteamID64. Required. |
| `QUASAR_HOST_ID` | `quasar-docker` | Stable identity of this Quasar host. Without it Quasar uses the lowercased machine name. |
| `QUASAR_PUBLIC_BASE_URL` | `http://127.0.0.1:8080` | Browser-facing URL. Quasar also passes it to managed servers as `QUASAR_BASE_URL`. |
| `QUASAR_LOG_FORMAT` | `text` | `text` or `json` |
| `QUASAR_LOG_MIN_LEVEL` | `Warn` | Minimum log level |
| `QUASAR_DIAGNOSTICS_HOST_DIRECTORY` | empty | Host folder for the optional diagnostics override (see below) |
| `QUASAR_DIAGNOSTICS_DIRECTORY`, `QUASAR_LOG_DIR` | empty | For native installs. In Docker use the diagnostics override instead. |
| `Quasar__Diagnostics__BackofficeUrl` | `https://log.se-servers.net/` | Diagnostic upload target. Uploads also need consent and a token. See [Diagnostics](Diagnostics.md). |
| `Quasar__Diagnostics__UploadToken` | empty | Installation upload token |
| `QUASAR_UPDATES_AUTO_APPLY`, `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS` | commented out | Opt-in automatic updates. See [Updates](Updates.md). |

Any other Quasar setting works too: either a documented `QUASAR_*` variable or the .NET form with double underscores, for example `QUASAR__AUTH__REQUIREHTTPSFORPUBLICACCESS=true` for `Quasar:Auth:RequireHttpsForPublicAccess`. Environment variables override `appsettings.json`. To use JSON instead, create `quasar-data/appsettings.json`. See [Configuration](Configuration.md). Keep `.env` out of version control when it holds secrets.

### Variables fixed by the image

| Variable | Value | Effect |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://+:8080` | Listen on port 8080, all interfaces. It overrides `Quasar:Host` and `Quasar:Port`. |
| `QUASAR_INSTALL_DIR` | `/data` | State root |
| `QUASAR_MODE` | `service` | No browser, service behavior |
| `QUASAR_CONSOLE_LOGGING` | `true` | Logs go to `docker compose logs` as well as to files |
| `QUASAR_OPEN_BROWSER_ON_START` | `false` | |
| `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` | `false` | Managed servers stop with the container |
| `QUASAR_UPDATES_ENABLED` | `false` | In-app Quasar updater off; the image is the release unit |

## First administrator

When `/data/rbac.json` does not exist, Quasar creates it with one Steam `admin` mapping for `QUASAR_ADMIN_STEAM_ID`. The value must be exactly 17 digits; anything else, including the unedited placeholder, stops startup with `QUASAR_ADMIN_STEAM_ID must be a 17-digit SteamID64.` An existing `rbac.json` always wins, so later role changes are never overwritten. If you delete `rbac.json` while the variable is still set, Quasar recreates the initial mapping. See [Access control](AccessControl.md).

## Persistent state

`./quasar-data` on the host is `/data` in the container and holds everything durable: `appsettings.json`, `rbac.json`, server definitions and saves (`Magnetars/`), world templates, config profiles, the managed SteamCMD, Dedicated Server and Magnetar downloads (`ManagedRuntime/`), data protection keys, logs (`Logs/`), analytics, backups and plugins. The folder layout is the same as a native install; see [Install on Linux](InstallLinux.md#where-quasar-keeps-its-files). Removing or replacing the container does not touch it. Files in it are owned by root, because the container runs as root.

### Diagnostics on a separate disk

1. Create the host folder, for example `/mnt/diagnostics/quasar`, and give the container write access.
2. Set `QUASAR_DIAGNOSTICS_HOST_DIRECTORY=/mnt/diagnostics/quasar` in `.env`.
3. Add the override file to every Compose command:

```bash
docker compose -f docker-compose.yml -f docker-compose.diagnostics.yml config --quiet
docker compose -f docker-compose.yml -f docker-compose.diagnostics.yml up -d
```

The override mounts the folder at `/diagnostics`, sets `QUASAR_DIAGNOSTICS_DIRECTORY=/diagnostics` and moves `quasar.log` and `discord.log` to `/diagnostics/application`. Compose does not create a missing host folder; it fails with `Set an existing absolute diagnostic storage path` when the variable is empty. Installation identity, consent and other state stay on `/data`. See [Diagnostics](Diagnostics.md).

## Networking and game ports

The Compose file uses host networking, so the container shares the host's network stack. Each managed server opens its own UDP game port (new servers get the first free port from `27016`), plus a Steam port (game port + 1000) and a Remote API port (game port + 2000), directly on the host without a Docker port list. The web UI is on port `8080` on all interfaces.

The Dockerfile declares `8080` and `27016/udp` only. If you run the image without host networking, you have to publish every server port yourself.

Keep the web port behind a firewall or an HTTPS reverse proxy when the host is reachable from untrusted networks. See [Access control](AccessControl.md).

### Changing the web port

`ASPNETCORE_URLS=http://+:8080` in the image takes precedence over `Quasar:Port`, and the image's health check probes `127.0.0.1:8080`. Changing the port therefore needs both a different `ASPNETCORE_URLS` and a matching `QUASAR_PUBLIC_BASE_URL`, and the built-in health check then reports the container as unhealthy.

## CPU priority and limits

`SYS_NICE` lets Quasar set managed servers to Normal, Above normal or High priority even if Quasar itself runs at a lower nice level. Magnetar's `-daemon` mode detaches each server, so it keeps the priority Quasar gives it.

Do not set container-wide CPU controls (`cpu_shares`, `cpus`, `cpuset`, CPU quotas or the matching `docker run` flags). They apply to the whole container cgroup, including every game server, and `SYS_NICE` cannot override them. Do not remove `SYS_NICE` on a runtime that cannot grant it either; managed servers would then inherit Quasar's priority.

## Upgrade, pin and roll back

- Track releases: keep `QUASAR_VERSION=latest` and run `docker compose pull && docker compose up -d`.
- Pin: set `QUASAR_VERSION` to a version tag such as `1.1.0.31`, then run the same commands.
- Roll back: set the previous tag and run the same commands. The same `quasar-data` stays mounted. Check the release notes before moving state to an older version.

The Updates page cannot update Quasar itself in a container: `QUASAR_UPDATES_ENABLED=false` turns off Quasar's release checks and shows `Update checks disabled.` The managed Magnetar and Dedicated Server downloads are stored in `quasar-data/ManagedRuntime`, so they survive image upgrades. See [Updates](Updates.md).

## Backup

Back up `quasar-data` as one unit (and the diagnostics folder if you use the override). Quasar's own configuration backups are described in [Backups](Backups.md).

## Build an image locally

The Dockerfile downloads the given release's `quasar-web-linux-x64.tar.gz` and `SHA256SUMS` from GitHub and verifies the checksum:

```bash
docker build --build-arg QUASAR_VERSION=1.1.0.31 -t quasar:1.1.0.31 .
```

`QUASAR_VERSION` may contain only letters, digits, dots and hyphens; otherwise the build fails with `Invalid QUASAR_VERSION`. The build context is only the `Dockerfile`; it does not build Quasar from source.

## Differences from a native install

- No Bootstrap launcher. The **Quasar power** dialog disables **Restart Quasar** with `Restart needs the Quasar launcher. This standalone process can only be shut down.`
- No in-app Quasar updates; change the image tag instead.
- Managed servers stop when the container stops (`QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN=false`). A native service leaves them running.
- Console logging is on, so `docker compose logs quasar` shows Quasar's log output.
- Linux AMD64 only.

## Common problems

- `Set QUASAR_ADMIN_STEAM_ID in .env`: copy `.env.example` to `.env` and set the variable.
- `QUASAR_ADMIN_STEAM_ID must be a 17-digit SteamID64.`: the value is not a SteamID64 (for example the placeholder was left in). Fix `.env` and run `docker compose up -d`.
- `env_file` errors on older Compose: the `path`/`required` form needs Docker Compose 2.24 or newer.
- Players cannot connect: check the host firewall for each server's UDP game port. Host networking means no Docker port mapping is involved.
- A server's priority does not go above Normal: `SYS_NICE` is missing, or a container-wide CPU limit is set.

## Sources
- `Docs/Docker.md`
- `Docs/QuickStart.md`
- `Dockerfile`
- `docker-compose.yml`
- `docker-compose.diagnostics.yml`
- `.env.example`
- `.dockerignore`
- `.github/workflows/release.yml`
- `Quasar/Program.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/Updates/QuasarUpdateService.cs`
- `Quasar/Services/Auth/RbacConfigCatalog.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Components/Layout/QuasarControlDialog.razor`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
