# Licensing

Copyright (c) 2026 CometWorks (https://github.com/CometWorks).

Quasar's current distribution uses the GNU Affero General Public License version
3 only, identified as `AGPL-3.0-only`. You can run it, including commercially,
without a license fee or registration. Your own plugins and services can stay
closed when they use the documented APIs and do not copy AGPL core code. Read
`LICENSING.md` for the guide and `EXCEPTION.md` for the permission and its limits.

Calling something a plugin does not exempt copied core code, internal patches or
private hooks routed through a wrapper. Documented UI replacement points and
ordinary API adapters are allowed. Similar functionality alone does not make a
plugin a copy, and a separate host patch does not make an otherwise independent
plugin AGPL. Falling outside the exception does not itself settle copyright law.

The listed SDK and contract code stays MIT-licensed. Quasar and Agent changes
keep their applicable AGPL source obligations. Upstream PRs are encouraged.

## Alternative licensing

Contact CometWorks if you need different terms, including keeping core changes
private. Availability, scope, fees and source obligations are agreed case by case
in writing. Asking does not change your current obligations. Independent plugins
that meet the exception do not need a paid alternative license.

## Previous MIT distribution

The transition is dated 1 November 2026. The frozen MIT snapshot is preserved by
the tag https://github.com/CometWorks/quasar/tree/refs/tags/archive/mit at commit
`80eddddb51d034517ffe699c09ff7e84f00b0021`. Code already received under MIT keeps
those permissions. The implementation uses AGPL with the extension exception;
the retained original MIT notice does not make the whole distribution dual-licensed.

## Modified hosted versions and redistribution

If you modify Quasar and users interact with it over a network, AGPL section 13
requires a prominent offer to download your version's Corresponding Source at
no charge. The sidebar has a **Source (AGPL-3.0)** link to the upstream
repository. Keep it prominent and point it at your fork's matching source,
including your changes.

Binary and container redistribution must also meet AGPL section 6's
Corresponding Source requirements. Keep the license, copyright and warranty
notices. The build copies `LICENSE`, `LICENSE.MIT`, `NOTICE`, `LICENSING.md` and `EXCEPTION.md`
next to the binaries, so the complete platform packages include them.
The symbols archives, which are CI build artifacts and not release assets,
include them too. The Docker image carries them in `/opt/quasar`. Official source is available from
the matching release tag and source archives at
https://github.com/CometWorks/quasar/releases. Symbols archives record the
exact commit in `manifest.json`. Keep any source you must supply yourself,
since release cleanup removes older releases.

Read the full license and transition guidance at
https://github.com/CometWorks/quasar/blob/main/LICENSING.md. Separately
licensed components, Space Engineers, Magnetar and downloaded plugins keep
their own terms.

## Sources
- `LICENSE`
- `LICENSE.MIT`
- `NOTICE`
- `LICENSING.md`
- `EXCEPTION.md`
- `Directory.Build.props`
- `scripts/package-linux-release.sh`
- `scripts/package-windows-release.ps1`
- `scripts/package-symbols.py`
- `Dockerfile`
- `Quasar/Components/Layout/MainLayout.razor`
- `.github/workflows/release.yml`
