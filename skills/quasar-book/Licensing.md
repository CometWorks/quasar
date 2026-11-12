# Licensing

Copyright (c) 2026 CometWorks (https://github.com/CometWorks).

Quasar's current distribution uses the GNU Affero General Public License version
3 only, identified as `AGPL-3.0-only`. You can run it, including commercially,
without a license fee or registration. The CometWorks Independent Extension
Exception grants additional permission only to qualifying Independent Extensions.
Read the guide in `LICENSING.md` and the binding definitions and conditions in
`EXCEPTION.md` before relying on the exception.

A plugin label, public access modifier, wrapper, network transport or separate
process does not by itself establish independence. The exception excludes copied
or adapted core code, internal runtime patches, unsupported access routed through
shims, and core modifications divided into separately packaged components.
A documented UI replacement point remains an eligible interface; an independently
written feature does not lose eligibility merely because its functionality
resembles a core feature. Exclusion from the exception is not, by itself, a
finding that copyright law requires a work to be AGPL-licensed.

The specified public SDK/contract paths remain available under MIT. An unchanged
independent plugin does not lose its eligibility merely because the host has a
separate core patch. Quasar and Agent implementation changes retain applicable
AGPL source obligations. Open-source plugins and upstream PRs are encouraged.

## Alternative licensing

CometWorks continues to invite requests for a separate written license where the
AGPL and the exception do not accommodate a proposed use. The relevant copyright
holders must agree the scope, duration, applicable products/deployments and any
fee in writing. Asking does not grant different rights or suspend existing
obligations. Qualifying Independent Extensions need no paid alternate license.

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
