# Guided cluster setup

How to create a managed cluster with the **Set up cluster** wizard: the local Host on the Quasar machine, enrolling other host machines and installing the Host executor there (enrollment command, SSH or manual package), the prerequisites, what the wizard checks and what its error messages mean. For cluster operation after setup see [Clusters](Clusters.md); for the Host executor itself see [Cluster hosts](ClusterHosts.md).

## Before you start

Quasar side:

- Quasar runs on Linux x64 from a published release. The release ships the Host executor as `Host/Quasar.Host` next to the web worker; a plain development build has none and enrollment fails with "This Quasar build does not include the Host installer (…)". For a development build, set `QUASAR_HOST_BINARY` to a published single-file `Quasar.Host`.
- A GitHub token with access to `CometWorks/cluster` in **Updates → GitHub token**, so Quasar can download cluster releases.
- The managed Dedicated Server, Magnetar and SteamCMD (Quasar installs and updates them; see [Updates](Updates.md)). The installed Magnetar must support managed plugin preparation (`-prepareManaged`).
- A published cluster release that runs on the installed Magnetar's PluginSdk: same PluginSdk contract major and an equal or higher minor on the Magnetar side. For a release or Magnetar without a contract version, the exact PluginSdk build has to match.
- `dotnet` with the .NET 10 runtime on the Quasar machine, because Quasar runs the release's world converter (`MagnetarWorld.dll`) with `dotnet`.
- A world template and a configuration profile. The profile's administrators must be numeric SteamID64 values and max players must be at least 2.
- If the profile has Workshop mods: a Steam Web API key in the Steam Workshop settings (**Configs → Mods → Add API Key**), because setup resolves the mods' dependencies. Without mods no key is needed.
- Roles: cluster manage and configuration editing (Editor or Admin) to run setup; security administration (Admin) to enroll host machines. Without Admin the wizard says "A security administrator must enroll host machines. You can continue with an existing connected host machine."

Host machine side (each machine that runs cluster processes):

- Linux x64.
- The .NET 10 runtime (`dotnet --list-runtimes` must list `Microsoft.NETCore.App 10.x`). The Gateway and world tools need it.
- Python 3 and util-linux (`flock`). The automatic installer installs them with `apt-get`, `dnf`, `yum` or `pacman` when it runs as root or has passwordless sudo.
- A working systemd user session for automatic installation. The manual package works without systemd.
- A private LAN/VPN IP address that the other host machines and Quasar can reach.
- An HTTPS address of Quasar that the machine can reach. Only an installation on the Quasar machine itself may use plain HTTP on loopback.

## Enroll host machines

Open **Hosts** (nav group "Infrastructure & tools") and choose **Add host machine** (`/hosts/enroll`), or expand **Add host machine** inside the wizard's **Host enrollment** step. Both use the same form:

| Field | Rules |
| --- | --- |
| Registration | **New host machine**, or an existing one to resume or update its Host executor. |
| Host machine ID | Lowercase letter first, then lowercase letters, digits and hyphens, up to 63 characters. Permanent. |
| Host machine name | Display name. A blank name gets a generated suggestion. |
| Cluster network IP address | A private IP: `10.x`, `172.16.x` to `172.31.x`, `192.168.x`, an IPv6 ULA (`fc00::/7`), or loopback. Use `127.0.0.1` only for a cluster entirely on the Quasar machine. |
| Quasar address reachable by this host machine | The Quasar origin, for example `https://quasar.example.com`, with no path or query. HTTP is allowed only on loopback. |
| Installation method | **Run an enrollment command on the host machine** (default), **Install over SSH**, **Download package for manual installation**. |

Registration validation errors:

- "Use a lowercase Host machine ID, a private IP address and a control port from 1024 to 65535."
- "A host machine with this ID is already registered."
- "This host machine address and Host executor control port are already registered. Resume the existing enrollment."
- "Use the Quasar HTTPS origin reachable by the machine, for example https://quasar.example.com. HTTP is permitted only for local loopback setup."

Each enrollment gets a Host executor control port (default 18400, loopback only on the host machine) and a generated credential. The Host executor connects out to Quasar; you do not open any inbound port for it.

### The local Host on this PC

The Quasar machine needs no enrollment. On Linux x64 every Quasar install has a local Host executor with ID `local` and the name "<host name> (this PC)". Quasar registers it at its first start, with a private LAN/VPN address of the machine (or `127.0.0.1` if it has none) and a free loopback control port from 18401 up. The registration doesn't appear in the **Registration** list of the enrollment form.

Quasar runs `Quasar.Host` from its own release and keeps the Host's files in `<install root>/LocalHost/`. Nothing goes into the home folder and no systemd unit is installed for it. Quasar starts the Host when the Hosts page or cluster setup is opened, when Quasar needs it for a cluster, and at Quasar start when a cluster is deployed on this PC. Details are in [Cluster hosts](ClusterHosts.md#the-local-host).

The Hosts page and the wizard show its status, for example "Local Host executor ready. Quasar starts it when a cluster needs it.", "Local Host executor connected.", "Automatic local cluster hosting requires Linux x64. Enroll a Linux host machine to run cluster processes." or, in the Docker image, "Quasar has no local Host executor in a container. Enroll a Linux host machine to run cluster processes."

### Run an enrollment command

**Generate enrollment command** shows a one-line shell command. Run it on the host machine as the account that will own the cluster processes. The command downloads the installer script with a one-time bearer secret and runs it with `bash`. It expires after 15 minutes, works once, and generating a new one cancels the previous one. Treat it as a password.

### Install over SSH

Fields: **SSH address**, **SSH username**, **SSH port** (default 22) and an optional **SSH private key file** path on the Quasar machine. Quasar runs `ssh -T -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=20` as the Quasar service user and pipes the installer into `bash -s`. Requirements:

- Key or SSH agent login. Password login is not supported.
- The remote host key must already be in the Quasar user's `known_hosts`. Quasar never disables host key checking.
- The Quasar address must not be loopback: "An SSH-installed Host needs a Quasar HTTPS address reachable from that machine; localhost points to the remote machine itself."
- A bad username, address or port gives "SSH requires a valid machine address, login name and port."; a missing key file gives "SSH identity file must exist on the Quasar machine."

### Download package for manual installation

**Prepare manual enrollment**, then **Download manual host package** gives `quasar-host-<id>.zip` with `Quasar.Host`, `host.json`, `state/credentials.json` and `README.txt`. The package holds the machine's credential; keep it private. On the host machine:

```bash
chmod 700 Quasar.Host
chmod 600 host.json state/credentials.json
./Quasar.Host run --config host.json
```

Keep it running with your own service manager. Before shutdown, have the service manager run `./Quasar.Host stop-clusters --config host.json` while `Quasar.Host` still runs, and allow it 110 seconds; otherwise the clusters are killed without a save. You install Python 3, util-linux and .NET 10 yourself and you update the binary yourself.

### What the automatic installer does

The command and SSH methods run the same script. It:

1. Checks Linux x64, a working systemd user session, Python 3, `flock` and the .NET 10 runtime.
2. Installs into `~/.local/share/Quasar/Hosts/<id>/` with `Quasar.Host`, `host.json`, `state/credentials.json` and `bin/host-update.py`.
3. Writes systemd user units `quasar-host-<id>.service`, `quasar-host-<id>-update.service`, `quasar-host-<id>-update.timer` and `quasar-host-<id>-stop.service`, and enables them.
4. Enables user lingering (`loginctl enable-linger`) when allowed, so the Host keeps running after logout. Otherwise it prints "Could not enable user lingering; run: sudo loginctl enable-linger <user>".
5. Prints "Quasar Host installed or updated."

Running it again for the same enrollment keeps `host.json`, credentials and state, replaces the binary only if it differs, and restarts only the Host unit (cluster processes keep running). A failed restart restores the previous binary. Details in [Cluster hosts](ClusterHosts.md).

### Check the connection

The form shows "Host executor connected. It is ready for cluster placement." once the Host connects, or "Waiting for this Host executor to connect. Check its service log if installation has finished." Enrollment alone does not make a machine usable; the Host must connect. The form also lists warnings the Host reports, such as lingering being off, a missing stop unit, or a too short systemd stop timeout. See [Cluster hosts](ClusterHosts.md#shutdown-and-logout).

## Create the cluster

Open **New cluster** on the Dashboard or Servers page (`/clusters/new`, tab **Create and deploy**).

**Step 1, Host enrollment.** Lists enrolled host machines with **Connected** or **Offline**. **Continue to cluster configuration** is enabled once at least one host machine is connected ("Waiting for an enrolled Host executor to connect. Enrollment alone does not make a host machine ready for deployment.").

**Step 2, Configure and deploy.**

| Field | Rules |
| --- | --- |
| Cluster ID | 1 to 64 letters, digits, hyphens or underscores. Permanent. A removed ID cannot be reused. |
| Server name | Shown to players and in Quasar. |
| World template | Quasar converts a copy; the template is not changed. |
| Configuration profile | Game settings, mods and plugin selection. Quasar copies it to a new profile "<Server name> (cluster)". |
| Enable the Bugfixes and Performance server plugins (recommended) | On by default. Adds the MagnetarHub plugins Bugfixes (`bugfixes`) and Performance (`454B0F55-C727-4DAB-B1A9-5AE6050A0932`) at their current hub commit and with their default settings to the cluster's copy of the profile, when the profile lacks them. The selected profile is not changed. The caption under it names what will be added, or says the profile already has them or had them turned off before, in which case Quasar leaves its plugin selection alone. Untick it to leave both out. To keep only one, leave it ticked and remove the other from the cluster's profile later. |
| Host machines | Tick each machine and set **Regular nodes**, 1 to 32 per machine, at least 2 and at most 254 in total. |
| Hot spares | How many hot spares the cluster has, 1 by default. A hot spare runs warm and holds nothing; when a node crashes the cluster moves that node's partitions to a spare at once, and the crashed node restarts as the new spare. Quasar only sets the count: it adds that many spare nodes, from the Gateway host machine on, at most 32 nodes per machine. With 0 the page warns that a crashed node's partitions reload on the remaining nodes. Hot spares need a cluster release that supports them; with an older release setup stops with "This cluster release does not support hot spares". |
| Cluster Gateway and World Authority host machine | One of the selected machines. Players connect to it. It also runs the Cluster Registry and the World Authority. |
| Public server port | UDP, 1024 to 65000, default 28000. |

**Create and deploy cluster** stays disabled until every selected host machine is connected. On success the page returns to the Dashboard and the cluster stays stopped; choose **Start** on its card or page.

Several host machines need distinct addresses that can reach each other, and none may be loopback. Quasar must also reach the Cluster Registry admin port on the Gateway machine.

### Ports

Setup reserves the public server port P through P+264 on the selected machines. Allow only P (UDP) through the public firewall; keep the rest on the LAN/VPN.

| Port | Use |
| --- | --- |
| P | Public server port (UDP, players) |
| P+16 | Cluster Registry admin endpoint (`http://<gateway address>:P+16`) |
| P+100 and up | Regular node backends, one per node on each machine |
| P+200 and up | Regular node controls |
| P+164 | World Authority backend |
| P+264 | World Authority control |

Setup refuses a port range that overlaps a local standalone server, another Cluster Gateway or another cluster's nodes on the same machines ("Choose a public server port at least 265 ports away."). It cannot see other software or firewall rules on the machines.

## What setup does

The status line shows each phase:

1. "Checking cluster hosts": each selected Host executor must answer with its enrolled identity and the required capabilities.
2. "Provisioning Dedicated Server and Magnetar": installs or updates the managed runtime.
3. "Downloading a cluster release compatible with Magnetar": downloads and selects the latest cluster release if the selected one cannot run on the installed PluginSdk.
4. "Staging the profile's Workshop mods": resolves the mods' dependencies and downloads the current version of each mod with the managed SteamCMD, the same way **Stage profile changes** does (see [Mods and content updates](Clusters.md#mods-and-content-updates)). Magnetar's companion mod is always pinned with them, also for a profile without mods or with crossplay enabled, because Magnetar adds it to every node.
5. "Preparing identical plugins and canonical configuration": runs Magnetar's managed exporter on a disposable copy, without starting a game server. Plugins start with their SDK default settings.
6. "Freezing the common runtime snapshot": copies DS, Content, Magnetar, Direct Transport and the common plugins into a pinned dependency snapshot. It leaves out what a server running from the installed DS writes itself: `DedicatedServer64/ProfileOptimization` (the .NET runtime's JIT profile) and `Content/Workshop`.
7. "Converting a copy of the world": splits the world with the release's converter.
8. "Distributing verified files and generated credentials": generates the admin, join and per-Host executor credentials, installs them on each Host, sends `steamclient.so` to the Gateway Host, and transfers the installation, the world and the staged mods.
9. "Preparing and activating the stopped cluster": every Host prepares the same revision and Quasar activates it stopped.
10. "Ready to start": the Host has already reported the activated Gateway as stopped, so a start is accepted at once.

Quasar generates all credentials. You do not set any environment variable for a guided cluster.

### steamclient.so

The Gateway's Steam frontend needs Valve's `steamclient.so` in `~/.steam/sdk64` of the Host account. Setup copies it from Quasar's managed SteamCMD (`linux64/steamclient.so`) or from the Quasar user's `~/.steam/sdk64/`. If Quasar has neither, setup continues and logs "steamclient.so was not found in Quasar's managed SteamCMD (linux64/steamclient.so). The Gateway's Steam frontend starts only if the Host account already has ~/.steam/sdk64/steamclient.so." Without it the Gateway exits at start with "Steam GameServer.Init failed".

## Resume and retry

Setup is durable. Its files live in `<install root>/ClusterSetup/<Cluster ID>/` (`request.json`, `status.json`, copied profile and world, plugin export, activation receipt).

- After a failure the status shows "Setup interrupted: <error>" and the inputs stay locked. Fix the cause (for example reconnect a Host) and choose **Resume setup**.
- "A file changed while it was copied into content storage, nothing was stored: <path> ..." means something rewrote that file during the freeze, for example a DS or Magnetar update. Storage is not damaged. **Resume setup** pins the file again.
- "Content object does not match its verified pin: <path> ..." means a file already in content storage changed, which Quasar never does. Check the disk and anything else that writes there before you resume.
- The cluster page of an unfinished setup shows **Resume guided setup**, which opens `/clusters/new?resume=<Cluster ID>`.
- **Start a new setup** clears the bound request so you can change the inputs under a new Cluster ID.
- Submitting a finished setup again only re-checks the Hosts.

Resume also re-checks the PluginSdk: if Magnetar was updated in between and the selected cluster release no longer fits it, setup selects a newer compatible release. If Magnetar changed at all, it prepares the plugins again.

## Setup errors

| Message | Cause and fix |
| --- | --- |
| "Host executor '<name>' lacks a required cluster capability. Update the Host executor (Quasar.Host) on that host machine, then resume setup." | The Host binary is older than this Quasar. Wait for its update timer (every 15 minutes) or reinstall it, then resume. |
| "Gateway Host cannot receive steamclient.so yet. …" | Same cause, on the Gateway Host. Older remote enrollments without the update timer need one reinstall from **Hosts → Add host machine**. |
| "Host executor '<name>' does not match its enrollment or has no executor identity." | Another Host answered on that registration. Check the machine and reinstall. |
| "Host executor '<name>' has not configured this cluster deployment. Resume setup after the Host executor is ready." | The Host has not applied the deployment yet. Resume. |
| "Cannot reach Host executor '<id>'. Start the Host executor (Quasar.Host) on that host machine and wait for it to reconnect before retrying." | The Host is offline. See [Cluster hosts](ClusterHosts.md#troubleshooting-connection-problems). |
| "Cluster release <v> cannot run on the installed Magnetar: it needs PluginSdk contract <x.y>, but Magnetar has <x.y>. …" | The cluster release and Magnetar have incompatible PluginSdk contracts. The message says whether a newer Magnetar or a cluster release built for the newer contract is needed. Update, or wait for that release, then resume. |
| "Cluster release <v> cannot run on the installed Magnetar: it was built against PluginSdk <hash>, but Magnetar has <hash> …" | The release or Magnetar declares no PluginSdk contract, so the exact PluginSdk build has to match, and no published cluster release matches the installed Magnetar yet. Wait for a matching release, then resume. |
| "The installed Magnetar release does not support managed plugin preparation. …" | Update Magnetar, then resume. No game server was started. |
| "Magnetar preparation failed: …" | Magnetar's exporter failed, often a plugin that cannot build or has unsupported configuration. Read the message, fix the profile, resume. |
| "Could not … from GitHub repository CometWorks/cluster (HTTP 404). …" | Missing release or no access. Check the token in **Updates → GitHub token**. 401, 403 and 429 have their own guidance in the message. |
| "This cluster ID already belongs to an existing registration. Choose a new ID." | Pick another Cluster ID. |
| "This cluster ID was removed. …" | Removed IDs are reserved. Pick another. |
| "This setup is already bound to another request. Resume its original settings or choose a new cluster ID." | You changed inputs of an existing setup. Use **Start a new setup**. |
| "Configuration profile cannot be used for a cluster: …" | Administrators must be SteamID64 numbers and max players at least 2. |
| "Choose the Cluster Gateway host machine and distinct LAN/VPN addresses. Remote placements cannot use loopback addresses." | Two machines share an address, or a multi-machine cluster uses loopback. |
| "This port range overlaps …" | Choose another public server port. |
| "Install the .NET 10 runtime before enrolling this machine. Gateway and world tools require it." | Installer check on the host machine. |
| "A working systemd user session is required." | Use the manual package, or log in through a session with a systemd user manager. |
| "Install Python 3 and util-linux manually, or allow passwordless sudo for automatic tool installation." | Installer check on the host machine. |
| "Host installation failed: …" | The installer script failed; the rest of the message is its error output. Installer runs time out after 10 minutes. |

## API

- `POST /api/v1/clusters/setup` with an `Idempotency-Key` header and `{ "uniqueName", "displayName", "worldTemplateId", "configProfileId", "gatewayHost", "playerPort", "machines": [ { "hostId", "regularNodes" } ], "hotSpares", "defaultPlugins", "inGameWorldName" }`. `inGameWorldName` is the world name players see, `displayName` when left out. `hotSpares` defaults to 0 here; `defaultPlugins` defaults to `true` (Bugfixes and Performance, as the checkbox above). Needs cluster manage and configuration editing. After a failure, retry with a new key; reusing a key returns the earlier result.
- `GET` and `PUT /api/v1/config-profiles/{profileId}/plugins` read and replace a profile's plugin selection, `[ { "pluginId", "displayName", "selectedVersion" } ]`, where an empty `selectedVersion` means the hub's current commit. Reading needs view access, changing needs configuration editing. A cluster runs the new selection only after **Stage profile changes** (`config-profile-preparation`) and **Apply pending update**.
- `GET /api/v1/clusters/{id}/setup` returns the stored request and status.
- `GET /api/v1/host-enrollment` lists enrollments with `connected`. `POST /api/v1/host-enrollment` registers `{ "id", "name", "address", "commandPort" }` (default 18400).
- `POST /api/v1/host-enrollment/{host}/ticket` with `{ "quasarUrl" }` returns the enrollment command; `/ssh` takes `{ "quasarUrl", "ssh": { "address", "user", "port", "identityFile" } }`; `GET /api/v1/host-enrollment/{host}/manual?quasarUrl=…` downloads the manual package. All need security administration.

## Sources
- `Docs/ClusterGuidedSetupPlan.md`
- `Docs/ClusterTerminology.md`
- `Docs/Configuration.md`
- `Docs/BuildingAndDevelopment.md`
- `Quasar/Components/Pages/ClusterCreate.razor`
- `Quasar/Components/Pages/ClusterDetail.razor`
- `Quasar/Components/Pages/HostEnroll.razor`
- `Quasar/Components/Pages/Hosts.razor`
- `Quasar/Components/Shared/HostEnrollment.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Services/ClusterApi.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/QuasarDefaultPlugins.cs`
- `Quasar/Services/ClusterConversionService.cs`
- `Quasar/Services/ClusterWorldConverter.cs`
- `Quasar/Services/ClusterHostCatalog.cs`
- `Quasar/Services/ClusterHostClient.cs`
- `Quasar/Services/ClusterHostEnrollmentApi.cs`
- `Quasar/Services/ClusterHostInstaller.cs`
- `Quasar/Services/ClusterLocalHost.cs`
- `Quasar/Services/ClusterPackageService.cs`
- `Quasar/Services/ClusterReleaseMonitor.cs`
- `Quasar/Services/ClusterSteamClientLibrary.cs`
- `Quasar/Services/ClusterWorkshopModStager.cs`
- `Quasar/Program.cs`
