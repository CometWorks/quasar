# Quasar handbook contents

Quasar runs and manages Space Engineers 1 dedicated servers. Bootstrap launches the Quasar web worker, which supervises one Magnetar process per server, and Quasar.Agent inside each server reports back to it. On cluster machines Quasar.Host runs the cluster processes. The pages below go from installing Quasar to running servers, extending them, keeping them current, clusters, and finding out what went wrong.

For a term, setting, environment variable or file name, see [Index.md](Index.md). For things the code leaves unclear, see [OpenQuestions.md](OpenQuestions.md).

## Getting started

- [Overview](Overview.md): what Quasar is, its parts (Quasar, Bootstrap, Quasar.Agent, Magnetar, the DS, Quasar.Host), and the glossary.
- [Install on Linux](InstallLinux.md): release assets, .NET 10, `./Quasar serve`, the `install.sh` systemd service, file locations, ports, uninstall and upgrade.
- [Install on Windows](InstallWindows.md): the zip, `Quasar.exe serve`, the `install.ps1` Scheduled Task, file locations, uninstall and upgrade.
- [Docker](Docker.md): the GHCR image, Docker Compose, `.env` settings, the `/data` volume, game port networking, upgrades and backups.
- [First setup](FirstSetup.md): the first administrator, Steam sign-in, the first-server wizard, importing a vanilla DS or Torch server.

## Running Quasar

- [Access control](AccessControl.md): Steam sign-in, the trusted-network bypass, the viewer, editor and admin roles, `rbac.json`, the Security page, API tokens, lockout recovery.
- [Configuration](Configuration.md): `appsettings.json`, where files live, the web UI port, headless mode, logging settings, environment variables, the `Quasar` command line.

## Servers

- [Servers](Servers.md): creating and editing servers, start, stop, restart and kill, crash and health recovery, the console, ports, .NET 10 or .NET Framework 4.8, SteamCMD, DS and Magnetar downloads, Workshop mods, process priority.
- [Config profiles and worlds](ConfigProfiles.md): config profiles, world templates, world saves, mods, access lists, and what Quasar writes into the server files on every start.
- [Plugins](Plugins.md): server plugins from MagnetarHub, choosing them per config profile, plugin settings and logs, dev folders, Quasar.Agent, registering a plugin.
- [UI plugins](UiPlugins.md): QuasarHub, the UI Plugins page, the .NET 10 SDK, safe boot, companion plugins.

## Keeping things current and safe

- [Updates](Updates.md): Quasar self-update through Bootstrap, automatic updates, the GitHub token, Magnetar and Dedicated Server updates, failed updates.
- [Backups](Backups.md): Quasar config, server and world backups, schedules and retention, restoring.

## Watching servers

- [Monitoring](Monitoring.md): the dashboard, Analytics, Players, Chat, Entities, notifications, the Discord bot, appearance.
- [Diagnostics](Diagnostics.md): error report consent, what is collected and uploaded, the public server listing.

## Clusters

Clusters are in beta. They split one world across several server processes behind a Cluster Gateway.

- [Clusters](Clusters.md): cluster terms and pages, starting and stopping, profiles and plugins on clusters, mods and content updates, release updates, backups, converting a standalone server to a cluster and back, the `Quasar cluster` command line.
- [Cluster setup](ClusterSetup.md): the setup wizard, enrolling host machines (local, command, SSH or manual), prerequisites, port layout, setup errors.
- [Cluster hosts](ClusterHosts.md): Quasar.Host packages and files, `host.json`, credentials, the connection to Quasar, systemd units, updates, shutdown, logs and connection problems.

## When something goes wrong

- [Troubleshooting](Troubleshooting.md): where every log is, how to read the server console, and common failures with their messages and fixes. Most pages also end with a Common problems section.
