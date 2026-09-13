# Docker Deployment

Quasar publishes a Linux AMD64 image to `ghcr.io/cometworks/quasar` for every
full release. Each image contains the exact checksummed Linux worker artifact
from that release. Available tags are:

- `latest`
- `1.1.0.31` (replace with the required release)
- `v1.1.0.31`

Pull-request and manual draft releases do not publish container images.

The final image is based on the .NET 10 SDK image, not the runtime-only image.
QuasarHub UI plugin builds therefore use the SDK already on `PATH` and do not
prompt to download Quasar's private managed SDK inside the container.

## Start with Docker Compose

Docker Engine on Linux and Docker Compose 2.24 or newer are required. From the
repository root:

```bash
cp .env.example .env
```

Edit `.env`, replace `YOUR_17_DIGIT_STEAM_ID`, then start Quasar:

```bash
docker compose pull
docker compose up -d
docker compose logs -f quasar
```

Open `http://127.0.0.1:8080`. The Compose manifest uses host networking because
Quasar supervises multiple game servers whose UDP ports are selected per server.
Host networking exposes those ports directly without maintaining a static Docker
port list. Keep the Quasar web port behind a firewall or TLS reverse proxy when
the host is reachable from an untrusted network.

`SYS_NICE` lets Quasar apply configured process priorities. Magnetar's
`-daemon` mode detaches its session in place, so it keeps the priority Quasar
applies. This means a managed Magnetar can run at Normal, Above normal, or High
priority even if the Quasar process itself has a lower nice level.

> [!WARNING]
> Do not set container-wide CPU priority or limits (`cpu_shares`, `cpus`,
> `cpuset`, CPU quotas, or equivalent runtime flags). Those controls apply to
> the container's whole cgroup, including every daemonized Magnetar and game
> server, and `SYS_NICE` cannot override them. Do not remove `SYS_NICE` or lower
> Quasar's nice level on a rootless/container runtime that cannot grant that
> capability; managed servers could otherwise inherit Quasar's lower priority.

Compose also enables an init process for child-process reaping and gives Quasar
up to 30 minutes to stop managed servers cleanly.

## Persistent state

The manifest bind-mounts `./quasar-data` at `/data`. `QUASAR_INSTALL_DIR=/data`
makes this the root for all durable Quasar state, including:

- `appsettings.json` and `rbac.json`
- server definitions, saves, Magnetar profiles, and rendered configuration
- managed SteamCMD, Space Engineers Dedicated Server, and Magnetar downloads
- data-protection keys, logs, analytics, backups, plugins, and update metadata

Back up `quasar-data` as one unit. Removing or replacing the container does not
remove this directory.

## Initial administrator

`QUASAR_ADMIN_STEAM_ID` creates `/data/rbac.json` with one Steam administrator
when that file does not exist. The value must be a 17-digit SteamID64; an invalid
value stops startup with a clear error. Existing `rbac.json` always wins, so the
environment value never overwrites later role changes. Leaving the variable set
also recreates the initial mapping if `rbac.json` is deliberately deleted.

## Environment configuration

Compose passes every variable in `.env` into the container. Quasar supports its
documented `QUASAR_*` variables and standard .NET hierarchical configuration
with double underscores. For example:

```dotenv
QUASAR_BACKUP_DIR=/data/Backups
QUASAR_ANALYTICS_RETENTION_DAYS=60
QUASAR__AUTH__REQUIREHTTPSFORPUBLICACCESS=true
QUASAR__AUTH__TRUSTEDNETWORKBYPASS__ALLOWLOOPBACK=true
QUASAR__AUTH__TRUSTEDNETWORKBYPASS__ALLOWSAMESUBNET=false
```

Service-principal, Gateway, OIDC, and other secrets may also be supplied through
`.env`; do not commit that file. To use JSON instead, create
`quasar-data/appsettings.json`. Environment variables take precedence over JSON.
See [Configuration](Configuration.md) for available settings.

Container defaults differ from packaged services in three intentional ways:

- console logging is enabled for `docker compose logs`;
- managed servers stop with the container instead of being left detached;
- Quasar's in-app updater is disabled because the image is the release unit.

## Upgrade or pin

For automatic full-release tracking, keep `QUASAR_VERSION=latest`:

```bash
docker compose pull
docker compose up -d
```

For a controlled rollout, set `QUASAR_VERSION` to an immutable version tag in
`.env`, then run the same commands. Roll back by restoring the previous tag; the
same `quasar-data` directory remains mounted. Review release notes before moving
state backward across versions.

## Build a released image locally

The Dockerfile builds from a published release artifact and verifies it against
that release's `SHA256SUMS`:

```bash
docker build --build-arg QUASAR_VERSION=1.1.0.31 -t quasar:1.1.0.31 .
```

Source builds continue to use the normal .NET release packaging workflow; the
container build does not duplicate that dependency and packaging logic.
