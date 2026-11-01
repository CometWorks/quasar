# Config profiles, world templates and saves

This page covers config profiles (the reusable world settings, mod list and plugin selection that Quasar applies to servers), world templates, world saves, and what Quasar writes into a server's files when it starts. Server lifecycle and Workshop downloads are on [Servers](Servers.md).

## How the pieces fit

- A **config profile** holds world settings, access lists, the Steam Workshop mod list and the Magnetar plugin selection. Several servers can share one profile.
- A **world template** is a stored copy of a Space Engineers world folder. Quasar never runs a template directly.
- A **world save** is the world folder one server actually runs. You usually create it from a template.

Each server definition points to one profile (**Configuration profile**) and one save (**World save**). On every start Quasar renders the profile into the server's DS config and into the save's `Sandbox_config.sbc`, so the profile wins over whatever the world had before.

## Config profiles

Open **Configs** in the navigation menu (page title "Configuration profiles"). Clicking a server's profile name on the Dashboard opens the same editor as a full-screen **Config Profiles** dialog.

The left column lists profiles. Type a name in **New profile name** and click **Create profile** to add an empty profile. Each profile has **Clone <name>** and **Delete <name>** buttons.

- **Clone** creates "<name> (Copy)" with the same content.
- A new profile starts with the Bugfixes and Performance server plugins selected. See [Plugins](Plugins.md#default-plugins).
- **Delete** is refused while servers use the profile: "Profile still assigned to servers. Reassign or clear them first." Otherwise it asks for confirmation.

The editor header shows **Profile name**, **Description**, counters for plugins, mods and **Assigned Servers**, and "Applied to:" with the Server IDs that use it. Buttons:

- **Save Profile** writes the profile. It is disabled while an Access list has invalid SteamIDs.
- **Reset** discards unsaved edits and reloads the profile from disk.
- **Clear Mods** empties the mod list (save to keep it).
- **Clone for Crossplay** creates a new profile with Network Type EOS, Cross Platform on, Experimental Mode off and all mods removed. The current profile does not change.

Editing profiles needs the config edit permission (see [Access control](AccessControl.md)).

### What a profile holds

The profile editor has four tabs plus a **Developer** panel:

- **World**: all DS world options, grouped into Server, Automation, Moderation, General, Survival, Multipliers, Player & Progression, Blocks & Combat, NPCs & World, Trash & Cleanup, Economy & Contracts and Advanced, plus the **Access** section. **Search world options** filters them.
- **Plugins**: Magnetar plugins to load. See [Plugins](Plugins.md).
- **Mods**: the Steam Workshop mod list in load order. Disabled while **Cross Platform** is on ("Mods are disabled while Cross Platform is enabled. Turn off Cross Platform to add or edit mods.").
- **Command aliases**: shortcuts for in-game commands, with the target command's permissions. See [Command aliases](#command-aliases).
- **Developer**: local plugin dev folders and extra plugin hubs. See [Plugins](Plugins.md).

The profile does not hold the game port, listen IP, server and world names, restart or health policy. Those belong to the server definition (see [Servers](Servers.md#creating-a-server)).

Expanded sections keep space below the final field and its help text, including
**Solar Radiation Intensity** and its oxygen pressurization and radiation requirements.

### Command aliases

Open **Configs**, select a profile, then open **Command aliases**. Add a source
such as `!grids list` and target `!ess grids list`, then **Save Profile**. Each
profile keeps its own aliases, including when cloned. Remove rows and save to
clear them. Restart assigned standalone servers to apply the profile. For a
cluster, stage and apply the selected profile so the pinned Agent configuration
reaches every node; saving alone does not change a running server.

An alias matches a whole command-word prefix, ignoring case. The longest source
wins and intentionally overrides a registered command with the same path.
Remaining arguments keep their quoted or escaped grouping. For example,
`!grids list "Big Ship"` becomes `!ess grids list "Big Ship"`. Expansion happens
once; aliases do not chain. An unavailable target produces a private error and
the command text is suppressed from normal chat. Target plugins must be enabled.

The original player's identity and target-command permissions still apply.
Aliases do not grant console or admin access. Quasar's Chat console can also use
aliases with its existing console permissions. Generated `!help` continues to list
the registered targets rather than aliases.

Sources and targets start with `!` and contain ASCII letters, digits, underscores
or hyphens separated by spaces or tabs. A profile supports 64 aliases, with up to
512 characters per path. Duplicate sources, exact self aliases and sources
starting with `!help` are rejected. Fixed quoted arguments in configured paths
are not supported; players can still quote arguments when invoking them.

Aliases need the matching Quasar.Agent and Magnetar with PluginSdk contract 1.1
(or a later 1.x contract). Quasar refuses to start or stage a profile with aliases
on an older runtime. Profiles without aliases continue to work on older Magnetar.
Standalone preparation replaces `CommandAliases.json` under the server's Magnetar
data directory, including an empty snapshot when aliases are cleared. Clusters
carry the same values in their existing canonical plugin configuration. Profile
staging overrides any previous Agent alias values. Edit this setting in the profile
for persistence; temporary plugin-config edits are replaced by the next profile
application.

### Where profiles are stored

| Path | Contents |
| --- | --- |
| `<quasar-root>/ConfigProfiles/<profile id>/profile.json` | The profile. |
| `<quasar-root>/ConfigProfiles/<profile id>/History/<timestamp>.json` | A copy on every save. Delete writes `<timestamp>-deleted.json` here and removes `profile.json`. |

The profile id is a slug made from the name when the profile is created. Quasar writes profiles and their history atomically and owner-readable/writable only on Unix, then checks every `ConfigProfiles/<profile id>/profile.json` for changes every 2 seconds: editing `profile.json` by hand works, and the UI reloads the change. Quasar does not create or restore built-in default profiles.

### Unsaved changes

When you switch to another profile in the list, navigate to another Quasar page
(including browser Back and Forward), or close the **Config Profiles** dialog with
**Done** or the back arrow while the editor has unsaved changes, Quasar shows
**Unsaved config changes** with:

- **Cancel**: stay and keep the edits.
- **Discard**: drop the edits and continue.
- **Save**: save the profile, then continue.

If saving fails or the profile has invalid values, you stay in the editor with
your changes. Refreshing, closing the browser tab or leaving Quasar shows the
browser's standard unsaved-changes warning instead. Saved profiles do not prompt.

### When profile changes take effect

Quasar applies a profile only when a server starts. Saving a profile does not change running servers. Restart each assigned server (the **Applied to** list) to apply changes. The profile editor does not mark which changes need a restart: all of them do.

Plugin configuration edited through plugin config panels is a separate mechanism; see [Plugins](Plugins.md).

### Access lists: admins, reserved slots, banned users

In the **World** tab, **Access** section:

- **Whitelist Group ID**: one Steam group ID. 0 disables it.
- **Admin User IDs**, **Reserved Slot User IDs**, **Banned / Blacklisted User IDs**: SteamID64 values separated by commas, semicolons, spaces or new lines. Each must be exactly 17 digits. Invalid entries stay in the field with an error, and the profile cannot be saved until you fix them ("Fix the SteamID64 values in Access before saving this profile.").

On every start Quasar replaces the `Administrators`, `Reserved` and `Banned` lists and `GroupID` in the server's `SpaceEngineers-Dedicated.cfg` with the profile's values. To keep a user banned or an admin across restarts, put them in the profile.

### Server password

**Server Password** is in the World tab, Server group. Quasar stores it as plain text in the owner-only `profile.json` and history files; treat configuration backups as secrets too. When rendering `SpaceEngineers-Dedicated.cfg` it writes only `ServerPasswordHash` and `ServerPasswordSalt` (a new random salt on every start). An empty password clears both, so the server has no password.

### Online mode and Offline safety

**Online Mode** (World tab, General group) has Offline, Public, Friends and Private. New profiles default to **Public**, including profiles created from a world template; Quasar does not copy a template world's online mode.

Offline is unsafe on a dedicated server because Space Engineers gives every connected user Owner-level permissions. The editor warns: "World is set to Offline, everyone will be granted Admin permissions and players will not be able to join from outside localhost." When a server starts with an Offline profile, Quasar:

- writes `127.0.0.1` as the DS listen IP instead of the server's **Listen IP**, and
- Quasar.Agent rejects every client whose direct address is not loopback. Relayed connections are rejected too.

Public, Friends and Private use the server's own **Listen IP**.

### Mods

The Mods tab has these panels:

- **Mod list**: the profile's mods in load order. Drag to reorder. **Filter selected mods**, **Merge from World Template**, **Auto Sort Dependencies** and **Remove Dead Mods** are above the list.
- **Steam Workshop**: **Search Workshop** and **Popular Mods** (both need the Steam Web API key), **Add API Key** / **Update API Key**, and **Add SteamCMD account**. See [Servers](Servers.md#steam-web-api-key-and-steamcmd-account).
- **Advanced/manual mod setup**: paste Workshop URLs, IDs or a collection URL/ID into **Workshop URLs, IDs, or collection** (collections expand into their items), or add one mod with **Mod name** and **Workshop ID**.

Dependency handling:

- When a profile is opened, saved, cloned or receives imported mods, Quasar reads the declared Workshop dependencies (needs the API key), adds missing dependency mods and marks them as dependencies. It does not reorder the list.
- **Auto Sort Dependencies** sorts the list so dependencies load before the mods that need them. Quasar warns when a dependency is listed after its dependent, when Steam reports a circular dependency, or when a dependency cannot be satisfied.
- After a check, a **Dependency outline** panel shows the dependency tree. It is not saved.
- Quasar manages the DS **Autodetect Dependencies** setting (hidden in the World tab). It turns it off after a clean check or a successful sort, so the DS loads exactly the list Quasar wrote. It turns it on when the list was changed by hand, the API key is missing, the check failed or warnings remain.
- Without an API key or when Steam is unreachable, Quasar keeps the list as it is and shows a warning; saving still works.
- Opening the Mods tab refreshes mod display names from Steam (no API key needed). The new names are unsaved edits until you save.
- **Remove Dead Mods** asks Steam about every selected mod and removes the ones Steam reports as unavailable. Save the profile to keep the change.

## World templates

Open **Worlds** in the navigation menu (page title "World templates"). Templates are stored here:

| Path | Contents |
| --- | --- |
| `<quasar-root>/WorldTemplates/<template id>/template.json` | Name, description, timestamp. |
| `<quasar-root>/WorldTemplates/<template id>/World/` | The copied world files. |
| `<quasar-root>/WorldTemplates/<template id>/History/` | `<timestamp>-deleted.json` after a delete. |

When Quasar copies a world into a template, it skips the world's `Backup` subfolder. The source must contain `Sandbox.sbc`, otherwise: "Source directory '<path>' does not contain Sandbox.sbc." or "Source world directory not found: <path>".

Ways to create a template:

- **Predefined worlds** tab: lists the worlds shipped with the installed Dedicated Server (`Content/CustomWorlds`, `Content/QuickStarts` and `Content/Scenarios`, shown as DS Custom Worlds, DS Quick Starts and DS Scenarios). **Use this world** selects one and then offers to create a config profile from it.
- **Custom import** tab: enter **Template name**, **Description** and **Source world path** (an absolute path to a Space Engineers save folder; **Browse** opens a folder picker), then click **Import**.
- **Create world template** in a server's menu on the Dashboard: copies the server's current save. The server must be stopped with goal Off ("Stop the server before creating a world template from its current state."). The default name is "<server name> Snapshot <date time>".
- **New Template** in the **Create World Save** dialog opens a quick import dialog with the same two tabs (see [Quick import](#quick-import)). The Dashboard setup guide uses the same dialog, titled **Choose Dedicated Server World**.

Discovery runs in the background and uses the runtime-selected Dedicated Server installation as well as configured and managed locations. The chooser checks again automatically when server files become ready. **Where Quasar looked** lists searched folders; unreadable folders show warnings. **Refresh** retries discovery, and **Clear search** removes a filter with no matches. If no worlds are found after preparation, use **Updates > Check Dedicated Server**, then refresh, or choose **Custom import**.

Import progress shows the number of copied files. The source stays unchanged; a cancelled or failed copy removes the incomplete template and publishes no template entry.

The **World Templates** table lists templates with size, update time, name and description. Row actions:

- **Create config profile from world settings**: reads the template's `Sandbox_config.sbc` and shows how many session settings and mods it found. Creates a profile with those settings and mods, except Online Mode, which stays Public.
- **Clone template**: copies it as "<name> (Copy)".
- **Delete template**: permanently deletes the template and its world files. Servers whose saves were created from it are not affected, because each save is a separate copy.

### Quick import

The quick import dialog adds a **Template config profile** step after you pick the source world. It reads the source's `Sandbox_config.sbc` and offers:

- **Create new config profile from world settings** (enter **Profile name** and **Description**),
- **Import mods into existing config profile** (choose **Config profile**; only mods not already in it are added),
- **Import the world template only**.

### Merging template mods into a profile

In a profile's Mods tab, **Merge from World Template** opens **Merge mods from world template**. Pick a template; the dialog shows how many new mods it would add and how many are already in the profile. Only missing mods are added, at the end of the list. Save the profile afterwards.

## World saves

A server's saves live in its saves path, by default `<quasar-root>/Magnetars/<Server ID>/DedicatedServer/Saves/`. The **World save** field in the server editor lists the folders there. A save is valid when its folder contains `Sandbox.sbc`.

You can also copy any Space Engineers save folder into the saves path by hand and then select it.

### Creating a world save from a template

In the server editor, click **Create From Template** under **World save**. The **Create World Save** dialog asks for a **World template** ("Template world files will be copied into a new save folder.") and has **New Template** to import one first. **Create Save**:

- needs a Server ID in the editor first ("Server ID required before creating a world save."),
- copies the template's world files (without `Backup`) into a new folder in the saves path, named after the template (with " 2", " 3" and so on when the name is taken),
- selects the new save in the editor.

The folder is created right away, even if you then cancel the server editor.

## What Quasar writes on every start

Before each launch Quasar prepares the server's files in this order:

1. Repairs the Steam Workshop cache if needed (see [Servers](Servers.md#workshop-cache-repair)).
2. Renders `SpaceEngineers-Dedicated.cfg` in the DS app-data folder. It starts from the existing file (or an empty one) and sets: `IgnoreLastSession` false, `ServerPort`, `SteamPort` (game port + 1000), `RemoteApiPort` (game port + 2000), `IP` (`127.0.0.1` when Offline), every World-tab option, block type limits, `GroupID`, `Administrators`, `Reserved`, `Banned`, the password hash and salt, `ServerName` and `WorldName`.
3. Writes the profile's `CommandAliases.json` snapshot, then Magnetar's `Sources/sources.xml` and `Profiles/Current.xml` from the profile's plugin selection and deploys Quasar.Agent into `Local/`.
4. Rewrites the save's `Sandbox_config.sbc`: `SessionName` (the in-game world name), every session setting from the profile, and the `Mods` list in the profile's order. If the save has no `Sandbox_config.sbc`, Quasar skips this step and logs a warning.
5. Writes `Saves/LastSession.sbl` pointing at the selected save.

Because of step 4, world settings or mods changed outside the profile (in-game admin changes, a tool that edits the save) are overwritten at the next start. Make such changes in the profile.

Step 4 runs before the oxygen check, so the profile's **Enable Oxygen** decides whether a world with oxygen blocks may start (see [Servers](Servers.md#launch-failures-that-fault-a-server)).

## Common problems

- **"Profile still assigned to servers. Reassign or clear them first."**: change the **Configuration profile** of every server listed under "Applied to", then delete.
- **Save Profile disabled**: an Access list has an entry that is not a 17-digit SteamID64. Fix or remove it.
- **Settings changed but the server still uses the old ones**: restart the server. Profiles apply only at start.
- **Mods disappeared from a world**: the server's profile does not list them. Add them to the profile or use **Merge Into <profile>** in the server editor.
- **Mods tab greyed out**: **Cross Platform** is on. Turn it off to edit mods.
- **"Workshop API key needed"**: add a Steam Web API key in the Mods tab. Dependency checks, search and **Auto Sort Dependencies** need it.
- **A ban or admin added in-game is gone after restart**: Quasar replaces these lists from the profile on every start. Add the SteamID64 to the profile's Access lists.
- **Players cannot join, profile is Offline**: Offline restricts the server to connections from the same machine. Use Public, Friends or Private.
- **"Selected world save is missing Sandbox.sbc."**: the folder is not a valid save. Create a save from a template or copy a complete save folder.

## Sources
- `Quasar/Components/Shared/DedicatedServerSetupStatus.razor`
- `Quasar/Services/QuasarDefaultPlugins.cs`
- `Docs/StateMachines/ConfigProfileChanges.md`
- `Docs/Configuration.md`
- `Docs/QuasarArchitecture.md`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/Models/QuasarConfigProfile.cs`
- `Quasar/Models/QuasarWorldTemplate.cs`
- `Quasar/Models/DedicatedServerPathResolver.cs`
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/ChildFileWatcher.cs`
- `Quasar/Services/QuasarConfigMetadata.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- `Quasar/Services/WorldTemplateImportLocationService.cs`
- `Quasar/Services/WorldSandboxConfigEditor.cs`
- `Quasar/Services/WorldStartupCompatibilityGuard.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Components/Pages/Configs.razor`
- `Quasar/Components/Pages/Configs.razor.css`
- `Quasar/Components/Pages/ConfigsPageDialog.razor`
- `Quasar/Components/Pages/ConfigProfilePendingChangesDialog.razor`
- `Quasar/Components/Pages/ConfigProfileQuickCreateDialog.razor`
- `Quasar/Components/Pages/WorldTemplates.razor`
- `Quasar/Components/Pages/WorldTemplateConfigProfileDialog.razor`
- `Quasar/Components/Pages/WorldTemplateFromServerDialog.razor`
- `Quasar/Components/Pages/WorldTemplateQuickImportDialog.razor`
- `Quasar/Components/Pages/MergeWorldTemplateModsDialog.razor`
- `Quasar/Components/Pages/CreateWorldSaveFromTemplateDialog.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`
- `Quasar.Agent/OfflineModeNetworkGuard.cs`
- `Quasar/Services/QuasarCommandAliases.cs`
- `Quasar/Components/CommandAliasesEditor.razor`
- `Quasar/Components/ConfigProfileReadOnly.razor`
- `Quasar.Agent/CommandAliasConfiguration.cs`
- `Quasar.Agent/AdminPlugin.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/ClusterConversionService.cs`
- `Quasar/Services/ClusterUpdatePreparationService.cs`
