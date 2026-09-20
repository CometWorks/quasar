# Guided cluster setup

The branch now implements machine enrollment and guided creation on top of the
seven-stage deployment work. This is a separate acceptance gate from the historical
live cluster tests. It requires the preparation command in
[Magnetar PR #58](https://github.com/CometWorks/magnetar/pull/58) (version 2.4.2.2) and a verified cluster
release built against the exact shipped SDK. Until those releases exist, setup stops
with a capability or SDK mismatch error and can be resumed. It never starts an older
Magnetar with an unrecognized preparation flag.

## Operator flow

1. Open **Create and deploy** at `/clusters/new`. **Connect existing Gateway** is a
   separate advanced tab for independently installed Gateways.
2. Enroll Linux x64 machines at **Hosts → Add cluster machine**. Choose local
   installation, a one-time command to run on the target, or installation over SSH.
   The same matching Quasar.Host executable is used in all three cases. Enrollment
   requires security-administration permission; SSH uses keys/agent and strict
   `known_hosts` verification. Password login and host-key bypass are not supported.
3. Select a world template, configuration profile, Gateway machine and node counts.
   At least two regular nodes are required. The Gateway machine also runs the WA.
4. Quasar stages the entire verified stable cluster release, provisions DS/Magnetar,
   prepares common plugins and Direct Transport, and freezes the runtime snapshot.
   Magnetar exports default SDK configuration without starting a game server.
5. Quasar converts a copy of the world, generates credentials, transfers verified
   inputs to every Host, prepares one revision and activates it stopped. Choose
   **Start** on the cluster controls when ready.

Machines need Python 3, .NET 10 and a working systemd user session. Quasar itself
also needs .NET 10 for the shipped world converter. Remote enrollment requires a
Quasar HTTPS origin reachable from the target; loopback HTTP is allowed locally.
Machines use private IPv4 LAN/VPN addresses or IPv6 ULA addresses for cluster traffic.
Loopback placement is allowed only for a single-machine cluster. Enabling user
lingering (`loginctl enable-linger`) keeps a user service running after logout.

The chosen player port is UDP. Setup reserves that port through port +264 for
Gateway control and node communication: admin control is port +16; regular node
backends start at +100, their controls at +200, and WA uses +164/+264. Keep this
range free on the selected machines and allow internal traffic over the LAN/VPN.
Known cluster assignments are checked; external services and firewall rules still
need verification on the target machines. Only the player port should be public.

## Durable preparation and plugin contract

`ClusterSetup/<id>` records the immutable request, copied profile/world, verified
plugin export, conversion receipts, activation receipt and progress. Retrying uses
the same inputs; changing a bound request requires a new cluster ID. An interrupted
setup exposes **Resume guided setup** on its cluster page. API retries use a new
idempotency key after a failed attempt; reusing a key returns that attempt's result.
Source templates and profiles are preserved. The cluster receives its own profile
copy, and remains stopped after activation.

Preparation reuses Magnetar's existing loader profile: Quasar writes
`Profiles/Current.xml` in its isolated preparation configuration directory and passes
its absolute path explicitly through `-profile <file>`. That
profile selects plugins and source versions; the separate preparation command
exports their compiled bundles and SDK configuration schemas/defaults. Selecting
a profile alone does not produce the canonical values required by managed readiness.
World configuration edits during preparation use a disposable copy, preserving the
pinned source world and its receipt across retries.

The exporter owns plugin compilation, source provenance, dependency/native-asset
capture and canonical SDK defaults. Quasar does not inspect Magnetar's private
compiler caches. The exporter supports concrete public SDK configuration properties
with public parameterless constructors. Private-only, ambiguous, or game-dependent
configuration declarations fail explicitly; existing-server conversion can instead
use its reviewed live Agent snapshot. Fresh setup uses SDK defaults, not settings
from an unrelated running server. All regular/WA/replacement nodes receive the same
common bundles and canonical values; shipped cluster role plugins remain untouched.

Quasar verifies both the exporter SDK hash and the cluster package's SDK pin. Release
order: merge/release Magnetar's exporter, produce the matching verified cluster
package, then test the Quasar prerelease. No release binaries are patched to bypass
this check. The preparation command does not change the SDK API, but build provenance
can still change the SDK bytes and therefore its pin.

## Ownership, credentials and transport

Hosts listen on loopback. An authenticated outbound WebSocket connects each Host to
Quasar, and each HTTP command connection gets a separate streaming tunnel. Host
identity and pending tunnel IDs are checked independently; disconnects close pending
commands, and reconnects restore control. Long file transfers retain streaming and
existing content verification. Gateway control remains a direct LAN/VPN connection
from Quasar; the tunnel is for Host control only.

The Linux worker archive includes its matching Host executable. Host publishing
embeds native runtime libraries so the installer can deploy that executable alone.
Enrollment downloads expire after 15 minutes and are single-use; issuing a replacement
invalidates the previous ticket. Installation stages files before publication and
can replay the same completed enrollment without overwriting different data. A page
reload can select an existing registration and generate a new ticket or retry SSH.

Machine and cluster credentials are generated automatically, encrypted with Quasar
Data Protection centrally, and stored in private Host files. Preserve Quasar's Data
Protection keys with its encrypted credentials when backing up or moving the control
plane. Host credential installation is idempotent and rejects changed secrets or a
changed executor roster for this setup. Child processes receive only their explicitly
referenced secrets, not inherited Host enrollment or unrelated cluster credentials.

The systemd unit uses `KillMode=process`: restarting the Host preserves its managed
Gateway/node processes for adoption. Quasar outages do not stop them. Stop clusters
through their lifecycle controls before intentionally removing a Host installation.

## API

Cluster setup retains cluster scope checks and requires cluster-manage plus
configuration-edit permission:

- `POST /api/v1/clusters/setup` with `Idempotency-Key` and a `ClusterSetupRequest`.
- `GET /api/v1/clusters/{id}/setup` for persisted progress and the original request.

Machine enrollment requires security-administration permission:

- `GET /api/v1/host-enrollment` lists registrations and connection state.
- `POST /api/v1/host-enrollment` registers ID, display name, private address and
  optional loopback command port (default 18400).
- `POST /api/v1/host-enrollment/{host}/ticket` with `quasarUrl` returns an enrollment command.
- `POST /api/v1/host-enrollment/{host}/local` installs on the Quasar machine.
- `POST /api/v1/host-enrollment/{host}/ssh` accepts `quasarUrl` and
  `ssh: { address, user, port, identityFile }`. The optional key path is on Quasar.

The ticket download and Host WebSocket endpoints use their own bearer credentials;
normal browser/API authentication is insufficient for these machine channels.

## Existing and stale registrations

Import explains the Gateway admin HTTP URL and environment-variable credential
reference; it does not provision a Gateway. Unreachable errors identify the endpoint.
The Delete dialog also offers explicitly confirmed **Forget registration**, requiring
the exact cluster ID. This archives the registration and stops Quasar managing it;
remote processes and data remain untouched. Removed IDs are reserved, including
case variants, to prevent accidental adoption of retained Host/operation state.
Normal deletion still requires verified stopped managed processes.

The local `dev` registration was inspected on 2026-09-20: legacy Gateway bundle,
goal On, no active managed deployment, and a local Host endpoint with no running Host.
It has not been edited or removed. The previous verification deployment remains removed.

## Acceptance still required

Focused tests cover enrollment auth, expiry/replay, private-address placement,
streamed HTTP tunnels, disconnection, deletion/forget, deployment identity and route
permissions. Host self-tests cover immutable credentials and child-secret isolation.
Magnetar's exporter was exercised with fresh hub downloads and the actual Quasar Agent;
Quasar's bundle validator accepted those outputs. This does not establish full
provisioning acceptance. After matching releases exist, verify a fresh world and
selected configurable plugins through managed Serving, both remote installation
methods on real machines, reconnect/adoption, setup resume, and firewall diagnostics.
No Quasar service or live cluster was launched for this implementation.
