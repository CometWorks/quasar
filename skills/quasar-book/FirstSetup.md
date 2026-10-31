# First setup

This page covers the first steps after installing Quasar: giving yourself administrator access, opening the web UI, signing in with Steam, creating the first server with the setup wizard, and importing an existing vanilla Dedicated Server (DS) or Torch server. Installation itself is on [InstallLinux.md](InstallLinux.md), [InstallWindows.md](InstallWindows.md) and [Docker.md](Docker.md).

## Open the web UI

Quasar prints its URL when it starts. With the shipped `appsettings.json` the UI listens on all interfaces on port `8080`, and the printed URL is `http://127.0.0.1:8080`. From another machine use `http://<quasar-host>:8080`. To change the port or the listen address see [Configuration](Configuration.md#web-ui-address-and-port).

The installed services and `Quasar serve` do not open a browser on their own. `Quasar ensure-running --open-browser` opens one when a desktop session is available (see [Configuration](Configuration.md#command-line)).

## Choose public access

A first interactive administrator session shows **Set up administrator access**.
If no subject or claim mapping grants the admin role, its first step asks for
your 17-digit **Administrator SteamID64**. **Save administrator and continue**
saves it to `rbac.json` before showing **Self-deploy** and **Use hosted panel**.
Existing administrator mappings skip this step. **Later** closes setup without
configuring public access; setup returns on the next page load while no admin is
configured. Headless startup is not blocked. Reopen it at
**Security → Manage access setup**. See [Hosted access](HostedPanel.md)
for automatic enrollment and its current availability limits.

## Set the first administrator

Quasar access is controlled by roles (`viewer`, `editor`, `admin`) mapped to SteamID64 values in `rbac.json`. A fresh install has no mappings, so a Steam sign-in alone gives no access. There are three ways to get the first admin in place. Details on roles are in [Access control](AccessControl.md).

### Option 1: open the UI on the Quasar machine itself

By default Quasar trusts requests from loopback (`127.0.0.1`, `::1`) and gives them the `admin` role without a Steam sign-in. Open `http://127.0.0.1:8080` in a browser on the machine that runs Quasar and enter your SteamID64 in the administrator setup step. You can also go to **Security** and add your SteamID with the `admin` role under **Runtime Subject Role Mappings**. The top bar shows "Trusted network" as the user name in this mode. Setup rechecks security-management permission before saving; a remote Steam sign-in alone cannot claim an unconfigured panel.

This does not work when the request comes through a reverse proxy, from another machine, or when `Quasar:Auth:TrustedNetworkBypass:AllowLoopback` is `false`.

### Option 2: create `rbac.json` by hand

Create `rbac.json` in the Quasar install directory (the data directory, next to `appsettings.json`). Replace the subject with your 17-digit SteamID64:

```json
{
  "subjectRoleMappings": [
    { "provider": "Steam", "subject": "76561198000000000", "roles": ["admin"] }
  ],
  "claimRoleMappings": [],
  "policyOverrides": {}
}
```

Typical locations:

| Install | `rbac.json` path |
| --- | --- |
| Linux user service | `~/.local/share/Quasar/rbac.json` (the folder set as `QUASAR_INSTALL_DIR` in the unit) |
| Windows | the folder Quasar was extracted to and installed from, for example `C:\quasar\Quasar\rbac.json` |
| Docker Compose | `./quasar-data/rbac.json` on the host (`/data/rbac.json` in the container) |

Quasar watches `rbac.json`, so a running instance picks up the change without a restart.

### Option 3: `QUASAR_ADMIN_STEAM_ID`

Set the environment variable `QUASAR_ADMIN_STEAM_ID` to your SteamID64 before Quasar starts. When `rbac.json` does not exist, Quasar creates it with that SteamID as `admin`. When `rbac.json` already exists, Quasar ignores the variable and never overwrites the file. The Docker Compose file requires this variable (set it in `.env`).

If the value is not exactly 17 digits, Quasar fails to start with `QUASAR_ADMIN_STEAM_ID must be a 17-digit SteamID64.` (only when `rbac.json` is missing, because that is the only time Quasar reads it).

The Quasar admin role controls access to Quasar only. It is separate from the `Administrators` list of a Space Engineers server, which lives in the config profile (see [Config profiles](ConfigProfiles.md)).

## Sign in with Steam

Click the sign-in icon in the top bar (or open `/login`). Quasar sends you to the Steam OpenID login page and back. After sign-in the top bar shows your SteamID64 next to the sign-out icon (tooltip "Sign out Steam <SteamID>"). The sign-out button submits an antiforgery-protected logout request.

- The session cookie is `Quasar.Auth`. It lasts 12 hours and renews while you use the UI.
- A Steam account without a role mapping gets the page "You are signed in, but this account has no Quasar role." It has **Sign out** and **Retry Steam sign-in** buttons. Ask an admin to add your SteamID, then sign in again.
- Role changes apply on the next request; you do not have to sign out.

## Dashboard prompts on first start

The **Dashboard** (`/`) shows two things before the first server exists:

- **Data Handling Consent** with **YES** and **NO** buttons. The choice controls Magnetar statistics, diagnostic reports to CometWorks and server listings. Until you choose, sharing stays off. You can change it later on the **Security** page. See [Diagnostics](Diagnostics.md).
- **Managed Runtime** panel with progress rows for **SteamCMD**, **Magnetar** and **Dedicated Server**. Quasar downloads these into its data directory in the background. Server starts are blocked with "Managed SteamCMD, Magnetar, and Dedicated Server runtime are still preparing." until they are ready. A failed component shows a **Retry** button.

## The first-server dialog

When no server or cluster exists, the Dashboard opens the **Set Up First Server** dialog once per browser (Quasar remembers this in browser storage). It asks "How should Quasar set up its first server?" with two buttons:

- **Create New Server**: closes the dialog and shows the inline **First Setup** wizard on the Dashboard.
- **Import Existing Server**: continues in the same dialog with the importer (see below).

The dialog needs both the `CanEditServers` and `CanEditConfigs` permissions (roles `editor` or `admin`). Viewers do not see it. Later you can reopen it from the Dashboard buttons **Setup guide** (both choices) and **Import server** (import only). **New server** opens the plain server editor.

## Create a new server with the wizard

The **First Setup** wizard has five steps and a progress bar ("Step N of 5"). It picks up where you are: a step that is already done is skipped. **Back** returns to the previous step and **Hide Wizard** closes it.

1. **Create world template**. Click **Choose DS World** and pick one of the worlds shipped with the Dedicated Server. Quasar copies it into its managed world template storage. The button stays disabled until the managed Dedicated Server download is finished.
2. **Create matching config profile**. Click **Create From World**. Quasar builds a config profile from the template's `Sandbox_config.sbc`, so session settings and mods match the world. Workshop mod dependencies are resolved and added. Online Mode is not copied: new profiles start as Public. If a profile was already created from this template, the step shows as done and the button reads **Review Config Profile**.
3. **Create server**. Click **Create Server**. The server editor opens with the template and the matching profile preselected. Fill in the identity and ports, then use **Create From Template** next to **World save** to create the server's save from the template. See [Servers](Servers.md).
4. **Start server**. A **Start <server>** button appears for each stopped server. It is disabled until the managed runtime is ready and the server has a world save.
5. **Wait for Quasar.Agent**. The wizard waits until the agent inside the server connects and reports data. Then **Close** finishes the wizard.

More on templates and profiles is in [Config profiles](ConfigProfiles.md).

## Import an existing vanilla DS or Torch server

The importer turns an existing server's world and `SpaceEngineers-Dedicated.cfg` into a Quasar server, a config profile and world templates. Stop the source server first. The source folder must be on the machine that runs Quasar, because Quasar reads it directly (**Browse** shows that machine's file system).

### Steps

1. **Source type**: choose **Vanilla Dedicated Server** or **Torch**.
2. **Location and transfer mode**:
   - Vanilla: select the DS app-data folder that contains `SpaceEngineers-Dedicated.cfg`, or a folder up to two levels above it. If Quasar finds more than one config, it uses the first and warns "Import instances separately to choose another."
   - Torch: select the folder that contains `Torch.cfg` (its parent also works). Quasar looks for `SpaceEngineers-Dedicated.cfg` in that folder, in its `Instance` subfolder and in the `InstancePath` from `Torch.cfg`.
   - Choose **Copy** (source world folders stay unchanged) or **Move** (selected source world folders are deleted after a successful import). Click **Analyze**.
3. **Analyze and select**: review warnings, set the **Quasar display name** and **Quasar identifier**, tick the worlds to import, pick the **Primary managed world** and choose which settings to transfer.
4. **Confirm**: tick **I confirm source server is stopped** and click **Import**.

Quasar finds worlds as folders containing `Sandbox.sbc`: the world named in the config's `LoadWorld` and every folder under `Saves`. The world from `LoadWorld` is marked **active** and preselected as primary. `LoadWorld` paths written on Windows, including Wine `Z:` paths, are resolved on Linux; if the path does not exist, Quasar looks for a folder with the same name under `Saves`.

### What Quasar creates

- One world template per selected world. The primary world also becomes the new server's world save (named after the server identifier). Each world's `Backup` subfolder is not copied.
- One config profile named "<display name> Imported".
- One server definition, stopped (goal state Off, auto start off). The success message reads "Imported server '<name>'. Review it before starting."

If the import fails part way, Quasar removes what it created (server, profile, templates). In Move mode, source folders are deleted only after everything is committed; if a folder cannot be deleted the import still succeeds and reports a warning.

### What transfers

Each category is a checkbox, all on by default:

| Checkbox | What Quasar copies |
| --- | --- |
| Server and world names | DS `ServerName` and `WorldName` into the server's in-game server and world names |
| Network, online mode, IP and port | listen IP, server port, cross-play and network type settings, and the world's online mode |
| Server options and MOTD | other root settings of `SpaceEngineers-Dedicated.cfg` |
| Admins, reserved, bans and GroupID whitelist | `GroupID`, `Administrators`, `Reserved`, `Banned` |
| Gameplay and world settings | session settings from the primary world's `Sandbox_config.sbc` (from the DS config if the world has none), except online mode |
| World Workshop mods | the mod list of the primary world |
| Torch crash-restart policy (Torch only) | Torch `RestartOnCrash` into the server's restart-on-crash setting |

With a category off, Quasar uses its own defaults: listen IP `0.0.0.0` and port `27016`, empty in-game names, restart on crash on. If another Quasar server already uses the port, Quasar picks the next free one and warns "Port N was already used by Quasar; imported server uses M."

With **Network** selected, the world's online mode is copied as is, including Offline. Check it before starting (see [Config profiles](ConfigProfiles.md)).

### What does not transfer

- **Server password.** The DS stores only a one-way hash, so Quasar cannot copy it. Set a new password in the imported config profile.
- **Torch plugins.** Torch plugins are not Magnetar plugins; Quasar reports how many it found and skips them. Install Magnetar plugins instead ([Plugins](Plugins.md)).
- **Torch whitelist.** Torch's own whitelist has no DS or Magnetar equivalent. The DS `GroupID` whitelist, admins, reserved slots and bans are imported separately.
- **Torch Autostart.** The imported server stays stopped until you start it.
- The old DS or Torch install, its config, plugins and logs. Move mode leaves them in place for recovery.

## Common problems

| Message | Cause and fix |
| --- | --- |
| `SpaceEngineers-Dedicated.cfg not found in '<path>' or its first two folder levels.` | Wrong folder for a vanilla import. Select the DS app-data folder (the one with `SpaceEngineers-Dedicated.cfg` and `Saves`). |
| `Torch.cfg not found in '<path>'. Select Torch's root folder.` | Select the Torch install folder. |
| `Torch instance SpaceEngineers-Dedicated.cfg not found below '<path>'.` | The Torch instance folder has no DS config. Check `InstancePath` in `Torch.cfg`. |
| `No world folders containing Sandbox.sbc found under '<path>'.` | No valid world in `LoadWorld` or `Saves`. |
| `Cannot move source world '<path>' because it is already inside Quasar storage.` | The source is inside the Quasar data folder. Use Copy. |
| `Cannot move symlinked source world '<path>'. Use Copy mode.` / `... because it is also a source server root. Use Copy mode.` | Move only deletes plain world folders. Use Copy. |
| `Managed world target already exists: <path>` | A save folder with that name already exists for the server. Choose another identifier. |
| "this account has no Quasar role" after Steam sign-in | No mapping for your SteamID in `rbac.json`. Use one of the options in [Set the first administrator](#set-the-first-administrator). |
| `QUASAR_ADMIN_STEAM_ID must be a 17-digit SteamID64.` at startup | Fix or remove the variable. |
| Choose DS World or Start buttons disabled | The managed Dedicated Server download is not finished, or the server has no world save. Check the Managed Runtime panel. |

## Sources
- `Docs/QuickStart.md`
- `Docs/Docker.md`
- `Docs/Configuration.md`
- `.env.example`
- `docker-compose.yml`
- `install.sh`
- `scripts/install.ps1`
- `Quasar/Program.cs`
- `Quasar/appsettings.json`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Pages/ExistingServerImportDialog.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`
- `Quasar/Components/Pages/WorldTemplateConfigProfileDialog.razor`
- `Quasar/Components/Pages/Security.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Shared/AccessSetupDialog.razor`
- `Quasar/Services/ExistingServerImportService.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- `Quasar/Services/WorldSandboxConfigEditor.cs`
- `Quasar/Services/ManagedRuntimeWarmupService.cs`
- `Quasar/Services/Auth/RbacConfigCatalog.cs`
- `Quasar/Services/Auth/RbacConfig.cs`
- `Quasar/Services/Auth/QuasarRoleMapper.cs`
- `Quasar/Services/Auth/QuasarAuthConstants.cs`
- `Quasar/Services/Auth/TrustedNetworkEvaluator.cs`
- `Quasar/Models/QuasarConfigProfile.cs`
- `Quasar.Bootstrap/Program.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
