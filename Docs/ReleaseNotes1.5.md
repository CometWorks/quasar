# Quasar 1.5 release preparation

Quasar 1.5 uses the unified application and separate native updater introduced by
the 1.4.1 migration release. Its release assets are exactly:

- `quasar-linux-x64.tar.gz`
- `quasar-win-x64.zip`
- `SHA256SUMS`

Each package includes Quasar's UI and server supervisor, the updater, Host, guided
installer and manual install/recovery commands. Symbols stay in CI artifacts.
Legacy worker and Bootstrap archives are not built or published for 1.5. The latest
full 1.4.1 bridge remains available and is protected from normal release cleanup.

## Required upgrade order

1. Install the complete, corrected 1.4.1 migration package using the existing
   installation paths, account, service names and Linux user/system scope.
2. Start 1.4.1 successfully. Managed installs require verified native-service
   ownership and authenticated health. Keep `Updates/migration-1.4.1.json` with the
   data directory. A legacy 1.4.1 web-worker update alone is insufficient.
3. Select the full 1.5 release on Updates or run `Quasar update --version <release>`
   (`Quasar.exe` on Windows), using the complete version shown on the release page.
4. Check the durable outcome with `Quasar update --status` and use `update --retry`
   only after correcting a reported failure.

The main UI displays the three-part version, such as `1.5.0`. Updates, package
metadata, CLI and health checks retain the full build version, such as `1.5.0.0`.
The installer and application reject 1.5 without evidence of a healthy 1.4.1
migration, including manual installation and prerelease paths. Start with 1.4.1
for a new installation that will move to 1.5.

Application updates preserve detached game servers and local cluster Hosts, merge
configuration, and retain the complete previous application until replacement
passes health checks. Updater upgrades use the same complete package through the
installer with both services stopped. Back up settings, credentials and Data
Protection keys together before migration or planned installer maintenance.

See [installation](Installation.md) and [updates and migration](QuasarUpdates.md)
for platform commands and recovery.

## Promotion gates

The 1.4.1 release identity fix must be merged and published before promoting 1.5.
It keeps `release.json`, runtime health and migration receipts on the complete
public build identity instead of NuGet's internal version. CI compares package
and runtime identity on both platforms.

Before a full 1.5 release, verify:

- A full corrected 1.4.1 release is published with both complete packages, both
  legacy worker bridges and `SHA256SUMS`. The release workflow enforces this.
- Migration-gate, package/health identity, complete rollback and release-retention
  checks pass.
- Native Linux system/user service and Windows service/task-migration integration
  pass, including failed launches, file locks/permissions and restart recovery.
- Both 1.5 packages contain the main application, updater, Host and installers;
  neither packager generates a legacy worker archive.

PR builds are draft prereleases for review and do not bypass installation checks.
This preparation does not publish a full release or deploy to an installation.
