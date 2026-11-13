# Licensing

Quasar uses the [AGPL v3 only](LICENSE) (`AGPL-3.0-only`). AGPL keeps changes to our core available to
its users. Independent plugins using our documented APIs can stay closed under
the [plugin exception](EXCEPTION.md), including when loaded in-process. Copying
core code or patching its internals is outside that permission, even if packaged
as a plugin.
## MIT SDKs and contracts

The code and libraries built from these paths are also [MIT-licensed](LICENSE.MIT):

- `Quasar.Plugin.Abstractions/`
- `Magnetar.Protocol/`
- `Quasar.Host.Contract/`
- `Contracts/ClusterGateway.AdminContract/`
- `Quasar/wwwroot/_quasar/sdk/v1/client.js`
- `Quasar/wwwroot/_quasar/sdk/v1/rtc.js`
- `Quasar/wwwroot/_quasar/sdk/v1/download-worker.js`

## Running and sharing

Commercial use needs no license fee or registration. Running an unmodified
version does not itself trigger AGPL section 13.

If users connect to your modified version, offer them its matching source for
free, including required build/install material. This includes small patches.
Our game-server guidance includes players; a join notice can link to the source.
An upstream link without your changes is insufficient. You do not have to submit
your changes upstream.

Distributing binaries or containers also requires AGPL section 6 compliance.
Keep the notices and required source available.

## Alternative licensing

[Contact us](https://github.com/CometWorks) if you need different terms, including
keeping core changes private. We consider these requests case by case, where we
have the rights to offer them. Scope, fees and source obligations need a written
agreement; asking does not change your current license obligations.

<details>
<summary>MIT history and release source</summary>

The implementation changed to AGPL-3.0-only on 1 November 2026. The
[`archive/mit` snapshot](https://github.com/CometWorks/quasar/tree/refs/tags/archive/mit)
is commit `80eddddb51d034517ffe699c09ff7e84f00b0021`; its MIT notice remains in
`NOTICE`. Use the matching [release source](https://github.com/CometWorks/quasar/releases)
and build instructions. Modified deployments must link the UI to their matching
source. Preserve required source independently of release cleanup.

</details>

Earlier grants and third-party licenses still apply.
