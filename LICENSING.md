# Licensing

Independent plugins using Supported Interfaces may remain proprietary under the
[binding exception](EXCEPTION.md). Copied or adapted core code, internal patches
and covered modifications repackaged as plugins are outside that permission.
The guide below summarizes the terms; the exception governs its additional grant.

## Licence scope

Quasar implementation code uses [AGPL v3 only](LICENSE) (`AGPL-3.0-only`), with the
[CometWorks Independent Extension Exception](EXCEPTION.md). Existing AGPL version
choices remain unchanged.

The following source paths and their compiled libraries are additionally
available under the MIT licence in [LICENSE.MIT](LICENSE.MIT):

- `Quasar.Plugin.Abstractions/`
- `Magnetar.Protocol/`
- `Quasar.Host.Contract/`
- `Contracts/ClusterGateway.AdminContract/`
- `Quasar/wwwroot/_quasar/sdk/v1/client.js`
- `Quasar/wwwroot/_quasar/sdk/v1/rtc.js`
- `Quasar/wwwroot/_quasar/sdk/v1/download-worker.js`

## Running and sharing

Permitted commercial use and qualifying extensions require no license fee or
registration. Running an unmodified version does not itself trigger section 13.
For a modified version used remotely, prominently offer users its matching
Corresponding Source, including required build/install material, free of charge.
Our game-server guidance includes players who interact with that version; a
conspicuous join notice can link to the source. Small patches count. An upstream
link omitting your changes is insufficient. Binary/container distribution must
also satisfy section 6. Preserve applicable notices and source availability.
Upstream PRs are encouraged, not required.

## Alternative licensing

[Contact CometWorks](https://github.com/CometWorks) to request a separate written
license if the AGPL and exception do not accommodate your use, including requests
for proprietary core modifications. Availability depends on the relevant
copyright holders' rights. Fees, entities, products, versions, deployments,
duration and source obligations must be agreed in writing. No alternative
license, fixed price or entitlement to an offer is granted here; a request does
not suspend existing obligations.

<details>
<summary>Practical examples</summary>

- Original gameplay, administration or performance code using only Supported
  Interfaces may qualify, even if useful only with the host.
- A documented UI replacement point may qualify; extracting covered UI code or
  patching private host methods does not receive the exception.
- HTTP, WebSocket, mod messages and ordinary API adapters may be used. A wrapper
  exposing otherwise unsupported core internals does not establish independence.
- A small host patch still carries applicable source obligations. An otherwise
  qualifying plugin remains eligible when used with that patched host.

These examples explain the exception; they do not enlarge it. Exclusion from the
exception does not itself establish that copyright law makes a work AGPL-covered.

</details>

<details>
<summary>MIT history and release source</summary>

The implementation changed to `AGPL-3.0-only` on 1 November 2026. The frozen
[`archive/mit` tag](https://github.com/CometWorks/quasar/tree/refs/tags/archive/mit)
is commit `80eddddb51d034517ffe699c09ff7e84f00b0021`. The original MIT notice is
retained in `NOTICE`; it does not make the entire current distribution MIT.

Use the matching [release tag and source archive](https://github.com/CometWorks/quasar/releases),
and its build instructions. [Current build instructions](https://github.com/CometWorks/quasar/blob/main/Docs/BuildingAndDevelopment.md)
are an entry point; use the copy at the deployed tag for an older version.
For modified deployments, keep the UI source link prominent and point it to your
matching source. Preserve source independently of release-asset cleanup. Symbols
archives identify the commit in `manifest.json`.

</details>

Earlier valid grants and third-party terms remain in effect. The AGPL warranty
disclaimers and liability limitations continue to apply.
