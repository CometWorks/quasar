# Quasar handbook contents

Quasar runs and manages Space Engineers 1 dedicated servers. Quasar is one application that serves the web UI and supervises one Magnetar process per server, with a small updater service next to it. Quasar.Agent inside each server reports back to it. On cluster machines Quasar.Host runs the cluster processes. The pages below go from installing Quasar to running servers, extending them, keeping them current, clusters, and finding out what went wrong.

For a term, setting, environment variable or file name, see [Index.md](Index.md). For things the code leaves unclear, see [OpenQuestions.md](OpenQuestions.md).

## Getting started

- [Overview](Overview.md): what Quasar is, its parts (Quasar, the updater, Quasar.Agent, Magnetar, the DS, Quasar.Host, the legacy Bootstrap launcher), and the glossary.
- [Licensing](Licensing.md): AGPL-3.0-only, the MIT archive, source availability, modified hosted versions and redistribution.
- [Install on Linux](InstallLinux.md): the release package, the guided `Install.desktop`, prerequisites, `./Quasar serve --foreground`, the `install.sh` systemd services (main, updater, clusters), file locations, ports, uninstall, upgrade and the 1.4.1 migration.
- [Install on Windows](InstallWindows.md): the zip, the guided `Install.cmd`, `Quasar.exe serve --foreground`, the `install.ps1` Windows services, moving off the old Scheduled Task, file locations and permissions, uninstall and upgrade.
- [Docker](Docker.md): the GHCR image, Docker Compose, `.env` settings, the `/data` volume, game port networking, upgrades and backups.
- [First setup](FirstSetup.md): the first administrator, Steam sign-in, the first-server wizard, importing a vanilla DS or Torch server.

## Running Quasar

- [Hosted access](HostedPanel.md): Nebula enrollment, the frpc connector Quasar runs, public path URLs and their security, shared administration and billing, moving a panel, private connector files and current availability.
- [Access control](AccessControl.md): Steam sign-in, the trusted-network bypass, the viewer, editor and admin roles, `rbac.json`, the Security page, the assistant and player agent, the Audit log, API tokens, the machine endpoint policy, lockout recovery.
- [Configuration](Configuration.md): `appsettings.json`, where files live, the web UI port, headless mode, logging settings, environment variables, the `Quasar` command line (`serve`, `update`, `rollback`, `cluster`).

## Servers

- [Servers](Servers.md): creating and editing servers, start, stop, restart and kill, crash and health recovery, stops and restarts requested by plugins, the console, ports, .NET 10 or .NET Framework 4.8, SteamCMD, DS and Magnetar downloads, Workshop mods, process priority.
- [Config profiles and worlds](ConfigProfiles.md): config profiles, direct folder/ZIP import to servers, world templates, world saves, mods, access lists, and what Quasar writes into the server files on every start.
- [Plugins](Plugins.md): server plugins from MagnetarHub and other plugin hubs, choosing them per config profile, plugin settings and logs, dev folders, Quasar.Agent, registering a plugin.
- [UI plugins](UiPlugins.md): QuasarHub, the UI Plugins page, the .NET 10 SDK, safe boot, companion plugins.

## Keeping things current and safe

- [Updates](Updates.md): direct upgrades and native-service migration, Quasar updates through the native updater service, migrating from Bootstrap, automatic updates, the GitHub token, Magnetar and Dedicated Server updates, failed updates.
- [Backups](Backups.md): Quasar config, server, world and cluster backups, schedules and retention, restoring.

## Watching servers

- [Monitoring](Monitoring.md): the dashboard, Analytics, Players, Chat, Entities, notifications, the Discord bot, appearance.
- [Diagnostics](Diagnostics.md): error report consent, what is collected and uploaded, hung cluster node evidence, the public server listing and CometWorks announcements.

## Clusters

Clusters are in beta. They split one world across several server processes behind a Cluster Gateway.

- [Clusters](Clusters.md): cluster terms, live health map, service addresses and grouped controls, offline partition profiles and predictions, shared power and zone costs, rate-cache administration, starting and stopping, profiles, in-game names and plugins on clusters, mods and content updates, release updates, backups, converting a standalone server to a cluster and back, the `Quasar cluster` command line.
- [Cluster setup](ClusterSetup.md): the setup wizard, enrolling host machines (local, command, SSH or manual), prerequisites, port layout, setup errors.
- [Cluster hosts](ClusterHosts.md): Quasar.Host packages and files, `host.json`, credentials, network policy and the connection to Quasar, systemd units, updates, shutdown, logs and connection problems.

## When something goes wrong

- [Troubleshooting](Troubleshooting.md): where every log is (Quasar, updater, journal, event log), startup limit warnings, how to read the server console, and common failures with their messages and fixes. Most pages also end with a Common problems section.
