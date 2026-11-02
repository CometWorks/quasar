# Licensing

Copyright (c) 2026 CometWorks (https://github.com/CometWorks).

Quasar's current distribution uses the GNU Affero General Public License version
3 only, identified as `AGPL-3.0-only`. You can run it, including commercially,
without a license fee or registration.

## Previous MIT distribution

The transition is dated 1 November 2026. The frozen MIT snapshot is preserved by
the tag https://github.com/CometWorks/quasar/tree/refs/tags/archive/mit at commit
`80eddddb51d034517ffe699c09ff7e84f00b0021`. Code already received under MIT keeps
those permissions. The current distribution uses AGPL; the retained original
MIT notice does not make it dual-licensed.

## Modified hosted versions and redistribution

If you modify Quasar and users interact with it over a network, AGPL section 13
requires a prominent offer to download your version's Corresponding Source at
no charge. The sidebar has a **Source (AGPL-3.0)** link to the upstream
repository. Keep it prominent and point it at your fork's matching source,
including your changes.

Binary and container redistribution must also meet AGPL section 6's
Corresponding Source requirements. Keep the license, copyright and warranty
notices. The build copies `LICENSE`, `NOTICE` and `LICENSING.md` next to the
binaries of every project, so the complete platform packages include them.
The symbols archives, which are CI build artifacts and not release assets,
include them too. The Docker image carries them in `/opt/quasar`. Official source is available from
the matching release tag and source archives at
https://github.com/CometWorks/quasar/releases. Symbols archives record the
exact commit in `manifest.json`. Keep any source you must supply yourself,
since release cleanup removes older releases.

Read the full license and transition guidance at
https://github.com/CometWorks/quasar/blob/main/Docs/Licensing.md. Separately
licensed components, Space Engineers, Magnetar and downloaded plugins keep
their own terms.

## Sources
- `LICENSE`
- `NOTICE`
- `Docs/Licensing.md`
- `Directory.Build.props`
- `scripts/package-linux-release.sh`
- `scripts/package-windows-release.ps1`
- `scripts/package-symbols.py`
- `Dockerfile`
- `Quasar/Components/Layout/MainLayout.razor`
- `.github/workflows/release.yml`
