# Guided cluster setup

The branch implements machine enrollment and guided creation on top of the
seven-stage deployment work. This is a separate acceptance gate from the historical
live cluster tests. The original package pair was Magnetar 2.4.2.3 and cluster 1.1.7;
the published cluster release is now v0.1.0 (beta). Quasar verifies the cluster
release's exact PluginSdk binary pin. If either installed release lacks
the required capability or pin, setup stops and can be resumed. It never starts an older
Magnetar with an unrecognized preparation flag.

## Operator flow

UI labels follow the [agreed terminology](ClusterTerminology.md): **Host machine**
for the computer, **Host executor** for its Quasar.Host service, **Cluster Gateway**
for player traffic, and **Cluster Registry** for administration and coordination.
New host enrollment, cluster creation/registration and standalone server creation
prefill editable names and IDs, such as `host-machine-a1b2c3d4e5f6` or
`cluster-a1b2c3d4e5f6`. Clearing a name generates another suggestion when the field
loses focus or the form is submitted; the visible suggestion is saved. An explicit
ID remains independent of the name. Resumed setup keeps its recorded identity and
inputs; removed cluster IDs remain reserved.


1. Choose **Create Cluster** beside **Create Server** on the Dashboard (cards or list),
   then open **Create and deploy** at `/clusters/new`. **Register existing cluster** is a
   separate advanced tab for independently installed clusters.
2. The wizard opens on **Host enrollment**. Quasar automatically enrolls and prepares
   its Linux x64 localhost using the bundled Host. It prefers a private LAN/VPN
   address for multi-host support, falling back to loopback for a single-host
   cluster. Existing enrollments on this machine are reused.
   Add other Linux x64 host machines inline with **Add host machine**, or use
   **Hosts → Add host machine** outside the wizard. Both use the same enrollment
   form: choose local installation, a one-time command, SSH, or a manual package.
   At least one enrolled host must connect before **Continue to cluster configuration**
   is enabled. Existing connected hosts can be reused without another installation.
   The same matching Quasar.Host executable is used in all four cases. Enrollment
   requires security-administration permission; SSH uses keys/agent and strict
   `known_hosts` verification. Password login and host-key bypass are not supported.
   Installation also enables a per-machine update timer. It checks the active Quasar
   web release every 15 minutes and updates the Host executor automatically, including when
   Quasar runs a prerelease. The cluster package pin does not select the Host executor binary.
3. In **Configure and deploy**, select a world template, configuration profile,
   Cluster Gateway and World Authority host machine, public server port and node counts. **Back to host enrollment** lets you add or repair
   hosts without leaving the wizard; entered cluster settings are preserved.
   Every selected host machine requires 1–32 regular nodes, with at least two in total.
   The Cluster Gateway host machine also runs the Cluster Registry and World Authority. Every selected host machine must be connected before
   creation or resume is enabled. Quasar checks authenticated Host executor identity and
   required capabilities before creating a cluster registration; an offline or
   incompatible Host executor leaves no new cluster registration.
4. Quasar provisions DS/Magnetar, stages the verified latest cluster release whose
   PluginSdk pin matches the installed Magnetar, prepares common plugins and Direct
   Transport, and freezes the runtime snapshot.
   Magnetar exports default SDK configuration without starting a game server.
5. Quasar converts a copy of the world, generates credentials, transfers verified
   inputs to every Host executor, prepares one revision and activates it stopped. Setup checks
   each host's configured deployment before reporting success, including when a
   previously completed setup is submitted again. Successful creation or import
   returns to Dashboard. Choose **Start** on the cluster controls when ready.

On cluster details, the main controls are Start, Stop, Save world (while running)
and the guided cluster update. A managed cluster can be told to stop even when its
Cluster Registry admin endpoint is temporarily unreachable. Live node and player information
appears while a cluster is running
or its managed cluster services is still running during shutdown. When a managed cluster is
stopped, Cluster Registry connection failures are expected and are not shown as warnings.
Recovery requirements, credential errors and other actionable failures remain visible.
Backups and recovery have their own sections; manual JSON deployment requests and
Gateway maintenance controls, including Gateway restart, are under advanced sections.

Automatic installation needs a working systemd user session. It installs missing
Python 3 and util-linux through the native package manager when root or passwordless
sudo is available; otherwise it reports the prerequisites without enabling deployment.
Local installation uses the .NET 10 toolchain already installed for Quasar.
It does not provision a separate SDK. Remote host machines need .NET 10 installed.
Quasar itself also needs .NET 10 for the shipped world converter. Remote enrollment requires a
Quasar HTTPS origin reachable from the target; loopback HTTP is allowed locally.
Machines use private IPv4 LAN/VPN addresses or IPv6 ULA addresses for cluster traffic.
Loopback placement is allowed only for a single-machine cluster. Installation enables
user lingering (`loginctl enable-linger`) when the account or passwordless sudo allows
it, so the Host keeps running after logout. The Host reports lingering that is still
off as a warning on the enrollment page.

**Manual installation** downloads a ZIP containing the matching Host binary,
`host.json`, a private enrollment credential file and run instructions. Extract it
into a private directory on the registered machine and run
`./Quasar.Host run --config host.json` under your preferred service manager. Have the
service manager run `./Quasar.Host stop-clusters --config host.json` before shutdown,
while the Host still runs, and allow it 110 seconds; otherwise a shutdown kills the
clusters without a save. This
path does not require systemd; the administrator supplies Python 3, util-linux and
.NET 10 and maintains the Host executor binary. The download requires security-administration
permission and is not cached. Enrollment alone never makes a host deployable: its
authenticated connection and setup capability checks must succeed.

Single-Host setup automatically gives `PluginStorage.GetSharedDirectory` a path
under that Host's runtime root. The path is included in stopped Host snapshots.
For several Hosts, Quasar leaves shared storage unset unless an advanced imported
deployment specification supplies `sharedStorageRoot`. That path must be one
filesystem mounted identically on every Host; matching local path strings are
insufficient. Quasar does not provision that mount or include external storage
in its per-Host snapshots.

The chosen player port is UDP. Setup reserves that port through port +264 for
Cluster Registry administration and node communication: admin control is port +16; regular node
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

The setup form keeps a bound request read-only while it can be resumed. After a
failed attempt, inputs remain bound and visible so an administrator can reconnect
the hosts and resume. **Start a new setup** explicitly clears the bound request and
ID so the operator can revise inputs under a new ID. Failures do not clear the form.
A resumed setup opens **Configure and deploy**, including when a selected host is
offline. The enrollment step remains available to repair connections; placement
stays bound to the original request.
Removed IDs remain reserved while their Host state and operation history exist.

Before each attempt, guided setup updates managed Magnetar and checks its actual
PluginSdk bytes against the selected cluster release. When the pins differ, it
downloads the latest cluster release and selects it if its SDK pin matches.
Selection clears the previous dependency snapshot; a plugin export made with an older
SDK is prepared again. If no compatible release is published, setup stops with both
hashes in the error and can be resumed after publication. Activated deployments keep
their frozen runtime and use the separate cluster update workflow.

Preparation reuses Magnetar's existing loader profile: Quasar writes
`Profiles/Current.xml` in its isolated preparation configuration directory and passes
its absolute path explicitly through `-profile <file>`. That
profile selects plugins and source versions; the separate preparation command
exports their compiled bundles and SDK configuration schemas/defaults. Selecting
a profile alone does not produce the canonical values required by managed readiness.
World configuration edits during preparation use a disposable copy, preserving the
pinned source world and its receipt across retries.

Magnetar's exporter always includes `direct-transport`. Guided setup resolves its
commit-pinned MagnetarHub entry, even when the selected standalone profile or the
global developer-folder catalog also contains a `direct-transport` checkout. Quasar
leaves that developer source out of the disposable preparation configuration so it
cannot shadow the hub entry. The resulting compiled Direct Transport bundle is then
frozen with the other cluster dependencies. Standalone developer-folder settings are
left intact. A failed setup can be resumed with a new idempotency key.

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
package, then test the Quasar prerelease. A newer Magnetar release can change SDK
bytes even without an API change, so package publication must follow it. No release
binaries are patched to bypass this check.

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
invalidates the previous ticket. Installation stages files before publication.
Reinstalling the same registration verifies its identity and credentials, keeps its
configuration and state, and atomically replaces a different Host binary. It restarts
only the Host systemd unit; a failed restart restores the previous binary. New
enrollments install a separate systemd update timer. It uses the enrollment credential
over the configured HTTPS Quasar origin (loopback HTTP is also allowed), verifies the
downloaded binary's size and SHA-256, and rolls back if the Host cannot stay active.
Quasar installs this timer automatically for older local enrollments on startup.
Older remote enrollments need one final reinstall because their existing Host has no
updater and Quasar does not retain SSH credentials. A page reload can select an
existing registration and generate a new ticket or retry SSH for that migration.

Machine and cluster credentials are generated automatically, encrypted with Quasar
Data Protection centrally, and stored in private Host files. Preserve Quasar's Data
Protection keys with its encrypted credentials when backing up or moving the control
plane. Host credential installation is idempotent and rejects changed secrets or a
changed executor roster for this setup. Child processes receive only their explicitly
referenced secrets, not inherited Host enrollment or unrelated cluster credentials.

The systemd unit uses `KillMode=process`: restarting the Host preserves its managed
Gateway/node processes for adoption. Quasar outages do not stop them. Stop clusters
through their lifecycle controls before intentionally removing a Host installation.

At logout or machine shutdown systemd tears down the user manager and kills whatever
the Host unit left running. To save first, installation adds
`quasar-host-<id>-stop.service`. It is ordered after the Host unit, so systemd stops it
first, and its `ExecStop` runs `Quasar.Host stop-clusters`. That asks every local
Gateway to shut its cluster down (30 s drain grace) and waits up to 105 s. A Host
restart or update does not touch this unit, and installation only ever starts it,
because stopping it shuts the clusters down.

systemd gives `user@.service` 120 s to stop by default, enough for this. Ubuntu ships
`/usr/lib/systemd/system/user@.service.d/timeout.conf` with `TimeoutStopSec=5`, which
kills the clusters before the save. Drop-ins apply in file name order, so override it
with a file of the same name, `/etc/systemd/system/user@.service.d/timeout.conf`,
holding `[Service]` and `TimeoutStopSec=150`, then run `systemctl daemon-reload`. The
Host checks lingering, the stop unit and this timeout, and the enrollment page shows
what is wrong.

After the machine is back, the Gateway restarts in the Down phase, because a process
restart never undoes a shutdown. When the cluster's goal is On, the reconciler starts
the next generation, as Stop and Start would, but only after a clean shutdown and once
the Host has restarted. A Down cluster that does not meet both conditions waits for an
operator.

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
`GET /api/v1/hosts/{host}/update` and `/update/binary` use that same Host credential
to advertise and stream the active Quasar release's Host executable.

## Existing and stale registrations

Release-fetch failures identify the GitHub repository (`CometWorks/cluster`), the
resource being fetched (release metadata, checksums or package archive), and the HTTP
status. A 404 can mean a missing release or insufficient access to a private repository;
check **Updates → GitHub token** and its repository access before retrying setup.
The no-scope token recommended for public update checks cannot read private releases.
Authentication, rate-limit, connectivity and timeout errors provide their own recovery
guidance. These errors occur during package retrieval, before world preparation or
cluster activation. Existing saved failure messages change on the next setup attempt
after installing a build containing this diagnostic improvement.

Registration explains the Cluster Registry admin HTTP URL and environment-variable credential
reference; it does not provision cluster services. Unreachable errors identify the endpoint.
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
