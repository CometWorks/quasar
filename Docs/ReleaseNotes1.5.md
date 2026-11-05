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

## Installation and upgrade path

The mandatory 1.4.1 intermediate-release gate from the initial 1.5 release is
removed for the next release. Install the current complete package directly,
including on fresh machines. No migration receipt is required or written.
Existing Bootstrap or Scheduled Task installations still need the explicit
native-service migration with their original paths, accounts and Linux scope.

Use **Update Quasar** or `Quasar update --version <release>` for an existing native
installation. Check the durable result with `update --status`; retry only after
correcting a reported failure. Package metadata and health checks retain the full
public build identity and continue to validate it.

Application updates preserve detached game servers and local cluster Hosts, merge
configuration, and retain the complete previous application until replacement
passes health checks. Updater upgrades use the same complete package through the
installer with both services stopped. Back up settings, credentials and Data
Protection keys together before migration or planned installer maintenance.

See [installation](Installation.md) and [updates and migration](QuasarUpdates.md)
for platform commands and recovery.

## Promotion gates

Package and runtime identities must match on both platforms. CI verifies the
complete public build identity. Current release publication no longer depends on
a retained 1.4.1 package; the existing bridge remains available for compatibility.

Before a full 1.5 release, verify:

- A full corrected 1.4.1 release is published with both complete packages, both
  legacy worker bridges and `SHA256SUMS`. The release workflow enforces this.
- Direct-install, package/health identity, complete rollback and release-retention
  checks pass.
- Native Linux system/user service and Windows service/task-migration integration
  pass, including failed launches, file locks/permissions and restart recovery.
- Both 1.5 packages contain the main application, updater, Host and installers;
  neither packager generates a legacy worker archive.

PR builds are draft prereleases for review and do not bypass installation checks.
This preparation does not publish a full release or deploy to an installation.
