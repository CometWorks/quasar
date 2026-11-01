# Licensing and source availability

Copyright (c) 2026 CometWorks (https://github.com/CometWorks).

Quasar is free software: you can redistribute it and/or modify it under the
terms of the GNU Affero General Public License version 3 as published by the
Free Software Foundation, version 3 only (`AGPL-3.0-only`).

Quasar is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR
A PARTICULAR PURPOSE. See the full license at
https://github.com/CometWorks/quasar/blob/main/LICENSE or
https://www.gnu.org/licenses/agpl-3.0.html. Binary distributions include it as
`LICENSE`, alongside `NOTICE` and this document as `LICENSING.md`.

## MIT archive and transition

The licensing transition is dated 1 November 2026. The frozen `archive/mit`
tag preserves the last main-branch snapshot before this change:

- Tag: https://github.com/CometWorks/quasar/tree/refs/tags/archive/mit
- Commit: `80eddddb51d034517ffe699c09ff7e84f00b0021`
- License at that commit: MIT, with the original copyright and permission notice.

The current distribution and subsequent Quasar development use `AGPL-3.0-only`.
The transition does not revoke the MIT permissions for code already received
under MIT. The original MIT notice is retained in `NOTICE` for inherited code;
it does not make the current distribution dual-licensed. No permission to use
future AGPL versions is granted by the `AGPL-3.0-only` identifier.

## Using and hosting Quasar

You may run Quasar, including commercially, under the AGPL. There is no license
fee or registration step.

If you modify Quasar and let users interact with that modified version over a
network, AGPL section 13 requires a prominent offer to those users to download
the Corresponding Source of your version, at no charge, by a standard means of
copying software. This applies even when you do not distribute a binary. Keep
the UI's source link prominent and point it to the source of your deployed
version, including your changes. An upstream link alone does not provide the
source of a modified fork.

## Distributing Quasar

When distributing Quasar binaries or container images, meet the Corresponding
Source requirements of AGPL section 6 and keep the applicable copyright, license
and warranty notices. Source must match the distributed version and include the
changes and build/install material required by the license. Mark modified
versions and the dates of changes as required by section 5.

Official release source is available through the source archives and Git tag on
each release page: https://github.com/CometWorks/quasar/releases. Match the
release tag to the binary or image version. Build instructions are at
https://github.com/CometWorks/quasar/blob/main/Docs/BuildingAndDevelopment.md;
use the copy at the matching tag when rebuilding an older version. Symbols
archives also record the exact repository commit in `manifest.json`.

The UI sidebar links to the upstream source repository. Distributors of modified
versions must provide their own matching source and update that link as needed.
Preserve any source you must supply under the license independently of release
asset cleanup.

## Other software

Separately licensed third-party components retain their own licenses and
notices, including notices shipped under `wwwroot/licenses`. This transition
does not relicense Space Engineers, Magnetar, downloaded plugins, or other
external software. Consult those projects' licenses when using or distributing
them together with Quasar.
