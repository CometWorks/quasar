# Monitoring

This page covers the pages Quasar has for watching and running live servers: the Dashboard, Analytics, Players, Chat, Entities, Notifications and the Discord integration, plus the Appearance page. Server lifecycle, health checks and restart policy are in [Servers](Servers.md). Error reporting and the public server listing are in [Diagnostics](Diagnostics.md).

## Where the data comes from

Quasar.Agent runs inside every managed server and sends Quasar a telemetry snapshot about every 2 seconds: players, sim speed, CPU, memory, PCU, grid and entity counts, recent chat, registered chat commands and profiler data. Standalone process telemetry needs a connected agent. Cluster-wide player counts and operating status come from the Cluster Registry, independently of node-agent telemetry. A standalone server without an agent shows "Waiting for Quasar.Agent..." texts or "Selected server has no connected agent snapshot yet." See [Plugins](Plugins.md#quasaragent) for how the agent is deployed and [Troubleshooting](Troubleshooting.md#the-agent-never-connects) when it does not connect.

All pages below need at least the `viewer` role. Changing anything needs the permission named in each section. See [Access control](AccessControl.md#roles-and-permissions).

## Dashboard

The **Dashboard** (`/`, page title **Fleet overview**) is the landing page.

### Summary tiles

| Tile | Shows |
| --- | --- |
| **Servers** | Running standalone servers and serving/degraded clusters, out of the total configured fleet. Each cluster counts once; unavailable cluster status is identified. |
| **Players online** | Reported standalone players plus each cluster's Registry player count. A cluster's shared player limit counts once, not once per node. Reporting coverage is shown; an unknown limit displays a dash. |
| **Average sim speed** | Average across running standalone game processes and active regular cluster nodes. World Authority, hot spares and paused telemetry do not affect it. Health warnings and errors include clusters. |
| **Game process memory** | Sum of fresh standalone and current cluster-node working sets in GiB, including World Authority and hot spares. The Gateway is not a game process and is excluded. Reporting process count is shown. |

Cluster totals reuse the background Registry observations collected for Players, with no extra polling. Node resource readings must be less than 15 seconds old and match the Registry's current host, slot, node and epoch. Replaced nodes, duplicate identity claims and stale/disconnected agents are excluded. Missing readings stay unavailable instead of being counted as zero.

### Card view and list view

The **Cards** and **List** buttons switch the server view. List is the default. The choice is stored per browser (`quasar.dashboard.serverView` in local storage), and `?view=cards` or `?view=list` in the URL selects it too. List view embeds the same table as the **Servers** page, with one fixed row spacing and no density toggle. Card view shows one card per server and one per cluster.

A server card has:

- The status badge: **Running** (agent connected and snapshot received), **Connecting** (process running, agent not attached yet, for example right after Quasar restarted and adopted the process), **Starting**, **Restarting**, **Stopping**, **Stopped**, **Crashed** or **Faulted**.
- **World**, **Host**, **Connect** (click the port to copy the direct connect address) and **Profile**.
- Status lines, each shown only when present:
  - the last supervisor message, for example "Launch requested. Waiting for process and agent." or a fault reason
  - the health summary, for example "Quasar.Agent heartbeat stale beyond 20s timeout."
  - the last restart record: "Restart pending", "Last restart failed" or "Last restart recovered", with the cause (Health policy, Agent attach recovery, Scheduled, Maximum uptime, Manual, In-game, Crash recovery) and the UTC time. Hover for the reason.
  - the latest error line, only for a **Crashed** or **Faulted** server. Click it to open the console at the error lines.
- Live metrics: players, sim speed, process CPU, memory, PCU, mods, plugins loaded, uptime.
- **Live details** (expand it): **Refresh**, **Save**, a **Broadcast message** box with **Send**, the players table with **Set role**, **Kick** and **Ban**, **Recent Chat** and **Recent Command Results**. A **Mod download failed during world initialization.** box lists up to 5 failure lines when the DS reported Workshop download failures.
- **Console** opens the log console (see [Servers](Servers.md#the-server-console)).
- **Start**, **Stop**, **Restart** and **Kill** buttons (need `CanControlServers`). Their behavior is in [Servers](Servers.md#dashboard-and-server-controls).

### Latest error line

When a server crashes or faults, Quasar reads the last 256 KiB of the newest `SpaceEngineersDedicated*.log` in the DS app-data folder and of the newest `info*.log` in the Magnetar app-data folder. It takes the last line written since the server started that contains "exception", "fatal", "critical" or "error", from whichever file changed last. Starting the server again clears it.

### Problem banner

Above the cards, a banner shows the first server that needs attention: a **Crashed** or **Faulted** server first, then an **Unhealthy** one, then one with a health **Warning**. For crashed and faulted servers it has **Clear Error Status**, which sets the goal Off and returns the server to **Stopped**.

### Other panels

- **Managed Runtime** shows SteamCMD, Dedicated Server and Magnetar download progress until all are ready. See [Updates](Updates.md#managed-runtime-warmup).
- **Simulation speed** charts the last 30 minutes for standalone servers and cluster node slots (1.00x is real time) and links to Analytics. Each cluster line is labelled with the cluster name and slot. Node replacements and restarts share the same slot history; missing samples stay missing.
- **Recent activity** lists the last 5 events: server starts and stops, restarts with their reason, and agent command results. A server that crashed, faulted or failed to start shows its error message in red instead of "Server stopped". Cluster actions come from the saved operation log, with in-progress, completed or failed status, so they remain visible without connected agents and after a page refresh or Quasar restart. A completed start request means the desired state was saved, not that all cluster processes are ready. The chart and cluster activity respect cluster access restrictions.
- **Data Handling Consent** appears until a decision is stored. See [Diagnostics](Diagnostics.md#consent-choices).

## Analytics

**Analytics** (`/analytics`) charts the metrics history of the servers you select.

### Controls

| Control | Values |
| --- | --- |
| **Time range** | 30s, 1m, 2m, 5m, 15m, 30m, 1h (default), 6h, 24h, 7d, 30d, **Custom** (from and to date and time) |
| **Auto refresh** | Off, 5s, 15s, 30s, 60s |
| **Servers and clusters** | Standalone server check boxes and one check box per cluster, using its configured name. Selecting a cluster includes all its node slots, including newly discovered slots. Colored node labels identify each series. **Select all** and **Select none** act on both servers and clusters. |
| **Grid** | **Columns** 1 to 6 (default 2), **Rows** 1 to 12 (default 4), **Row height (px)** 180 to 960 (default 320), **Reset panels** |
| **Add panel** | Shows a hidden panel again. The tune icon on each chart opens its panel settings. |
| **Reset layout** | Back to 2 columns, 4 rows, 320 px |
| **Export** | Writes the in-memory analytics to disk now ("Analytics persisted to disk."). It does not download a file. |

The layout, selected servers/clusters and range are stored per browser in local storage (`quasar.analytics.view.v2`), not on the server. Saved per-node or per-epoch selections are upgraded to selecting their whole cluster when the agent identity is known. An explicit empty selection stays empty.

Each metric has one chart with the selected cluster nodes overlaid as separate
colored lines. The same node slot keeps its color across charts and refreshes,
even when a metric is missing on another node. World Authority has its own named
series. Replacements and epochs share their slot's history; missing samples are
not replaced with zero. Profiler and plugin statistics use the same selected
nodes. Standalone servers can be compared alongside clusters.

Idle regular cluster nodes pause new analytics samples until they own partitions
again. Their live-map cards and Nodes table show **Idle** instead of presenting
their reduced simulation rate as a performance problem. CPU bars are paused on
those cards; live RAM and background CPU remain part of resource totals. See
[cluster monitoring](Clusters.md#cluster-pages).

### Panels

| Panel | Meaning |
| --- | --- |
| **SimSpeed** | Simulation ratio. The axis tops out at 1.00, which is real time. |
| **Process CPU %** | CPU used by all threads of the server process. 100% is one fully used logical CPU, so values above 100% are normal on a multi-core machine. |
| **Server Simulation CPU %** | Time spent on one server update as a share of the 16.67 ms frame budget at 60 updates per second. Above 100% means updates ran over budget. |
| **Memory GB** | Working set |
| **Player Count** | Players online |
| **Frame Time ms** | Derived from sim speed |
| **PCU Used** | Used PCU |
| **Active Grids** | Grid count |
| **Active Entities** | Entity count |
| **Profiler: Frame / Update / Physics / Scripts / Network / Other ms** | Simple profiler buckets |
| **Profiler: Top Grids ms**, **Profiler: Entity Types ms** | Extensive profiler only |

Summary chips show selected server, cluster and node-series counts, process
averages and total process memory. With clusters selected, process CPU is summed
across the selected processes. Player, PCU, grid and entity totals are omitted
from these chips because nodes can report shared world data; their charts keep
the individual node readings. A **Plugin statistics** section appears when
Magnetar plugins on the selected processes publish metrics. **Show plugin
statistics charts** adds them, one line per instance.

### Process CPU and simulation CPU

The two CPU numbers measure different things and do not convert into each other:

- **Process CPU %** adds up CPU time of every thread in the DS process. 150% means about 1.5 logical CPUs in use. It does not mean the whole machine is overloaded.
- **Server Simulation CPU %** matches the in-game Shift+F11 row **Server simulation CPU Load** (use the row with "Server" in it; the row without it measures the local client). 25% means about 4.2 ms of the 16.67 ms budget. The in-game value is smoothed and rounded, so it will not match the chart exactly at every moment.

High readings on either can coexist with sim speed near 1.0 and smooth gameplay. Neither CPU value triggers Quasar restarts or alerts. Health checks use simulation frame progress, Discord alerts use SimSpeed, and the Notifications page has its own CPU threshold.

### History and retention

Quasar keeps three tiers per server:

| Tier | Size | Used for ranges |
| --- | --- | --- |
| Raw samples (one per agent snapshot, about every 2 seconds) | last 3600 samples | up to 2 hours |
| 1-minute averages | retention days x 1440 | up to 24 hours |
| 1-hour averages | retention days x 24 | longer ranges |

Retention defaults to 30 days. Allowed values are 30, 45, 60 and 90; any other value falls back to 30. Set it with `QUASAR_ANALYTICS_RETENTION_DAYS`, `Quasar:AnalyticsRetentionDays` or `Quasar:Analytics:RetentionDays` (checked in that order) and restart Quasar.

History is stored per server in `<data dir>/Magnetars/<server>/analytics.jsonl`. Quasar writes it about every 7 minutes, on **Export** and on shutdown, and compacts it every 12 hours or when the file passes 32 MiB. It survives Quasar restarts. Old entries without simulation CPU show as gaps. Profiler and plugin statistics are kept in memory only (720 samples per server or metric) and are lost when Quasar restarts.

### Profiler mode

Expand **Analytics Profiles** to see one row per selected standalone server or cluster node with **Mode**, **Live state** and where the data appears. These controls apply to individual processes. The mode (**Startup and live mode**) is:

| Option | Value | Fills |
| --- | --- | --- |
| **Simple, low overhead** (default) | `SafeContinuous` | Profiler: Frame, Update, Physics, Scripts, Network, Other |
| **Extensive, deep detail** | `DeepContinuous` | Also Profiler: Top Grids and Profiler: Entity Types |
| **Off** | `Off` | Nothing |

Changing the mode needs `CanControlServers`. Quasar saves it in the server's `server.json` and applies it to a connected agent right away, without a restart. Servers without their own setting use `Quasar:AgentProfilerMode` or `QUASAR_AGENT_PROFILER_MODE`. Use Simple or Off if a game update breaks the deep profiler. **Show simple profiler charts** and **Show extensive charts** add the matching panels.

## Players

**Players** (`/players`, heading **Known Players**) lists live and historical players Quasar has observed on standalone servers and clusters, with server, Steam ID, available client metadata, status and last seen time. Each cluster appears as one server, with one entry per player even after moving between nodes. Registry clients determine cluster presence; Quasar collects them every five seconds in the background, without an open browser. Saved history remains after players disconnect, the Gateway becomes unavailable or Quasar restarts, subject to retention and cleanup. When a cluster is unavailable, its actions are disabled. Cluster access restrictions still apply.

- Filters: **Server**, **Type** (All, Online, Offline, Banned, Kick cooldown) and **Search players**.
- The standalone row menu (needs `CanControlServers` and a connected agent): **Set role** (None, Scripter, Moderator, SpaceMaster, Admin), **Kick** (online players only), **Clear kick cooldown**, **Ban** or **Unban**. A row of a server without a connected agent shows "agent offline" and has no actions.
- The cluster row menu needs `ClusterManage`, a reachable Gateway with management scope and support for the action: **Kick** (online only), **Ban** or **Unban**, including saved offline players. These commands go through the Gateway and apply to the whole cluster. Cluster rows show admission state and node/partition routing when online; role edits and individual-node kick cooldown controls do not apply. The Registry does not supply every standalone metadata field.
- **Clean Server** and **Clean All** delete saved known players from accessible servers, including cluster history when cluster management is allowed. Players still online are recorded again on the next observation.
- **Auto clear after** removes players not seen for that many days (default 30, range 1 to 3650). Click **Save** next to it (needs `CanControlServers`).

Quasar stores the list in `<data dir>/known-players.json` and the auto-clear setting in `known-player-settings.json`.

## Chat

**Chat** (`/chat`) shows the recent chat of one server or cluster node and lets you send to it.

- Pick the server in **Server**. Only servers with a connected agent show chat. The page shows the agent's last 100 chat messages; Quasar does not store chat history.
- **Refresh** asks the agent for a new snapshot. **Save** and **Restart** act on the server (need `CanControlServers`). Cluster saves and restarts are done from the cluster details page.
- The input box (needs `CanControlServers`) broadcasts to global chat. Messages appear from **Server**. Press Enter to send.
- Text that starts with `!` switches the box to **Command text** and suggests matching chat commands registered on that server (Quasar.Agent and Magnetar plugin commands), up to 30 at a time.
- **Run** (or Enter) runs the command on the server as its console, with Owner rights. The players don't see the line. It and the command's replies show only in Quasar, as **Console** rows, and are not relayed to Discord. Announcements the command makes, like a restart countdown, still reach the players. Running commands needs a Magnetar release newer than 2.4.3.4; older ones answer "This Magnetar version can't run commands from Quasar. Update Magnetar."

Quasar.Agent registers `!stop`, `!restart [seconds]` and `!quit` on standalone servers. They are described in [Servers](Servers.md#in-game-commands-from-quasaragent). Cluster nodes do not get them.

## Entities

**Entities** (`/entities`) lists live world entities of a standalone server or a registered cluster.

- Choose **Server / cluster**, **Type** (Grids, Characters, Dropped items, Terrain, Other entities) and optionally **Search name / id / owner**, then press **Refresh**. Each cluster appears once with its configured name. The list does not refresh by itself.
- Standalone queries return up to 500 entities. A cluster query checks every active node against the Registry and loads all result pages from its connected agents. Shared copies are merged by entity ID into one row. **Node(s)** shows where the entity was observed; viewer buttons use that row's node automatically.
- Chips show **Matching** and **Shown**. Standalone servers also show **Live total**. Clusters show **Nodes checked**; their shared live totals are not added together. A **Partial cluster view** warning identifies unreachable nodes, failures or a changing world during paging. The loader shows progress and elapsed time, with **Cancel** to stop querying.
- This is a live view of loaded entities on responding nodes. Saved entities in offline partitions are not listed. Moving entities can change during discovery; refresh for an updated view.
- Columns start with **Name**, then type, optional cluster node locations, viewer, details, quantity, blocks, PCU, size, owner and position. **Entity ID** is near the end, before actions. Types and details use admin-facing labels such as **Dropped item**, **Large-grid station** and **Antenna relay**, rather than internal game type names. Unknown details show a dash.
- **Quantity** shows a dropped item's stack amount, including fractional amounts for resources. It is separate from the item name and details. Older agents' `x10` values also appear here. Updated agents send the numeric amount and use the item definition's display name when available. Other entity types show a dash for quantity. Search accepts readable labels as well as raw identifiers.
- Deleting (row delete button, or select and delete several) needs `CanControlServers`. It asks "Permanently delete N selected entities? This cannot be undone." There is no undo; take a world backup first if unsure.
- Cluster discovery also needs cluster query permission and access to that cluster. Cluster deletion needs cluster management permission and one verified owning node. It is disabled for shared, frozen, ambiguous or unknown ownership. Updated agents report ownership and check it again on the game thread before deleting. Older agents can still be browsed; update them before cluster deletion. If an entity moves or a node is replaced, refresh rather than picking a node manually.

The Entity Viewer is a separate UI plugin from QuasarHub. It adds a viewer button on the Entities page for grids and asteroid voxels and opens a fullscreen metadata-only viewer. It needs a local Space Engineers `Content` folder in the browser and ships its own Magnetar companion plugin. See [UI plugins](UiPlugins.md#companion-plugins).

Unavailable entities show a disabled, crossed-out viewer eye with a fixed muted color, including when the row is hovered or selected. Its tooltip explains the reason; planets are not supported. Supported grids and asteroids keep the normal eye button. This behavior requires the updated Entity Viewer UI plugin.

## Notifications

**Notifications** (`/notifications`, under **Settings** in the menu) and the bell in the app bar show CometWorks operational announcements, update notices and optional instance alerts. Update notices are described in [Updates](Updates.md#update-notices-on-the-notifications-page).

Announcements require current-policy YES and diagnostic-report consent in
Security. They do not add a separate opt-in. Critical announcements sort first,
show a CRITICAL label with error styling and turn the bell red. Plain text is
escaped; optional details links are HTTPS. Push titles include severity and open
the central Notifications page. Push carries a short text preview; the page shows
the full text. Linked Hosts are represented by this Quasar UI;
there is no separate Host delivery or server chat broadcast. Headless Quasar
also polls, but writes consenting notices to its service logs instead of using UI
or browser services. Log severity is Information, Warning or Critical. Headless
receipts survive restart and regrant, retain up to 4,096 IDs until original expiry,
and pause new logging at capacity or on invalid state. See
[Diagnostics](Diagnostics.md#cometworks-announcements) for retention and failure limits.

Revoking consent clears cached visibility and cancels pending delivery. Withdrawal
removes a notice on the next successful poll; expiry is exclusive and stale
snapshots stop displaying after six minutes. An independent timer raises UI
change events even during stalled or failed fetches. Each subscribed browser gets at most
one push attempt per ID, including across Quasar restarts, withdrawal and regrant.
Failed attempts can be missed; active notices remain on the page. Subscriptions
removed and recreated have new delivery history. Browser history admits at most
4,096 announcement IDs for that subscription's lifetime. At capacity, new
announcement attempts pause without evicting accepted IDs; ordinary notices can
still flow and the page/bell remain visible. Refreshing a subscription keeps its
IDs. Whole push state is bounded to 8,192 receipt keys per browser, 65,536 total,
and 8 MiB each for protected state and decoded UTF-8 JSON.

Legacy LastNoticeKey history is preserved alongside array history. Readable
invalid or over-limit receipt history remains on disk; announcement push pauses
and ordinary push can use session-only receipts, which can replay after restart.
Unreadable/oversized push state is preserved for repair, with no subscription or
key reset. Eligible announcements remain in the bell/page. Repair invalid state
and restart, or explicitly remove the affected subscription; continuing pauses
emit one generic warning per service instance. See
[announcements](https://github.com/CometWorks/quasar/blob/main/Docs/Announcements.md) for the full contract and limits.

### Nebula billing notifications

Nebula transfer requests and billing updates target their payer and incoming Steam
administrator through the existing push subscription. They also appear in the
bell/page while that identity can view Quasar. An existing subscription can still
receive billing-only notices after its payer loses panel roles; this grants no
Quasar access. Trusted-network subscriptions do not receive Steam billing notices.
The fixed link opens Nebula's account page for review and approval. Requests stay
there if push is off or delivery fails. Quasar must run and refresh the hosted
installation; real browser delivery through live Nebula still needs acceptance.
See [Hosted access](HostedPanel.md).

### Notification preferences

Preferences are per account and apply to the page, the bell and browser push on every browser. Click **Save preferences**.

| Preference | Default |
| --- | --- |
| **Quasar and cluster updates** | on |
| **Quasar UI plugin updates** (admins only) | off |
| **Instance crashes and failures (including automatic crash recovery)** | off |
| **Restarts caused by health checks or agent attach failures** | off |
| **Low simulation speed**, **Alert below simulation speed** | off, 0.8 (above 0, at most 1) |
| **High process CPU usage**, **Alert above CPU (%)** | off, 200 (100% is one logical CPU) |
| **High memory usage**, **Alert above working set (MB)** | off, 8192 (1 to 1,048,576) |
| **World has unsaved changes**, **Alert above unsaved game time (minutes)** | off, 15 (1 to 10,080) |
| **Metric alert duration (seconds)** | 30 (5 to 3,600). A metric must stay past its threshold this long. |
| **Metric alert cooldown (seconds)** | 300 (30 to 86,400). Minimum time between alerts for the same instance and metric. |
| **Notify when an alerted metric recovers** | off |

Out-of-range values fail with "Check notification thresholds: ...".

Metric alerts use telemetry no older than 30 seconds from running instances, including cluster nodes. Crash and health-restart alerts cover locally supervised servers only. Instance alerts are kept in memory for up to 24 hours, at most 100 per account, and are cleared when Quasar restarts or preferences are saved.

### Browser push notifications

**Enable push notifications** subscribes the current browser for the signed-in account. **Disable push notifications** and **Refresh status** affect only this browser.

- Push needs HTTPS or `localhost` and a supported browser. Otherwise the page says "Push requires HTTPS or localhost and a supported browser."
- Push needs at least the viewer role ("Sign in with a viewer account to enable push notifications."). Trusted-network access shares one notification identity for all such browsers.
- An account can have at most 16 push subscriptions: "This account already has 16 push subscriptions. Disable one from its browser first."
- Quasar checks for new notices to push every 30 seconds.

Subscriptions, the generated VAPID keys and all notification preferences are in `<data dir>/PushNotifications.json`, encrypted with the Data Protection keys in `DataProtection-Keys/`.

## Discord integration

The **Discord** page (`/discord`) configures one Discord bot for all servers and clusters. Viewers see a read-only summary. Changing settings, **Console Logs** and the Discord log download need `CanManageDiscord` (editor or admin).

### Bot setup

1. In the Discord Developer Portal, create an application and a bot, and copy the bot token.
2. Enable **Message Content Intent** for the bot if you use text commands or chat relay. Quasar connects with the Guilds, Guild Messages and Message Content gateway intents.
3. Invite the bot with the `bot` and `applications.commands` scopes and these permissions: View Channel, Send Messages, Read Message History, Attach Files, Embed Links, and Manage Channels (only for `/faction-channel`).
4. On the Discord page under **Bot Settings**: check **Enabled**, paste the **Bot token**, enter the **Guild ID** (your Discord server's ID), click **Save Bot Settings**.

**Use system/environment proxy** (default on) sends Discord API and gateway traffic through the proxy settings of the Quasar process (`HTTPS_PROXY`, `HTTP_PROXY`, `ALL_PROXY`, `NO_PROXY`). Clear it to connect directly. Saving reconnects the bot.

The status chip shows the bot state: Disabled, NotConfigured ("Bot token and guild ID required."), Starting, Running or Faulted with the error. **Console Logs** shows the last 256 KiB of `discord.log` and a download button.

### Bot presence

Under **Bot presence**: **Include all servers** or a selection (new servers are not added to a selection automatically; each cluster counts as one server), **Status indicator** (Automatic, Online, Idle, Do not disturb, Invisible), **Show activity text**, **Activity type** (Watching, Playing, Listening to, Competing in), and what the activity text includes: **Online server count**, **Player count**, **Issues and warnings**, **Server names**, **Server names and states**. A preview shows the result. Discord limits activity text to 128 characters. Quasar updates presence at most every 5 seconds.

### Per-server bindings

Expand a server or cluster under **Per-Server Bindings**, fill in channel IDs (right-click a channel in Discord with Developer Mode on to copy its ID) and click **Save settings**. A cluster entry covers the whole cluster across all Hosts.

| Field | Default | Purpose |
| --- | --- | --- |
| **Command prefix** | empty | Prefix for text commands, for example `;;` |
| **Command channel ID** | empty | Channel where text commands work. Commands need both the prefix and this channel. |
| **Chat relay channel ID** | empty | Global game chat goes here; messages posted here go into the game |
| **Admin whisper channel ID** | empty | Private game whispers go here. The channel must be admin-only (see below). |
| **Log channel ID**, **Log export interval (minutes)** | empty, 30 | New lines of the newest DS log, posted in code blocks |
| **Analytics channel ID**, **Analytics export interval (minutes)** | empty, 60 | An analytics summary per interval: average sim speed, process CPU, simulation CPU, memory, max players, PCU, grids, entities, uptime, state |
| **Death messages channel ID** | empty | Player death messages |
| **SimSpeed alert channel ID** | empty | SimSpeed alerts. Uses the analytics channel when empty. |
| **Status channel ID** | empty | Player join/leave and server lifecycle messages. Uses the chat relay channel when empty. |
| **Enable player connection notifications** | on | "**Name** connected to the server!" and disconnected messages |
| **Enable server lifecycle notifications** | on | "Server is going to Restart!", "Server Started!", "Server Closed!" |
| **Enable chat relay** | on | |
| **Enable log export**, **Enable analytics export** | on | |
| **Enable death messages**, **Death emotes (comma-separated)** | off | |

A feature only runs when its channel ID is set. **Death Message Templates** at the bottom of the page edits the death message texts, stored in `<data dir>/death-messages.json`.

### Text commands

Type the prefix and a command in the command channel, for example `;;status`. The prefix alone shows help.

| Command | Who can use it | Effect |
| --- | --- | --- |
| `help` | anyone in the channel | Lists the commands |
| `status` | anyone | Status embed: goal, state, agent, health, players, SimSpeed, process CPU, memory, PCU, grids, entities, uptime |
| `chat <text>` | anyone | Sends the text to game chat |
| `save` | Discord Administrator | Saves the world |
| `start`, `stop`, `restart` | Discord Administrator | Same as the UI buttons. `start` and `stop` set the saved goal state, and `start` also retries a Crashed or Faulted server. |
| `kick <steamId>`, `ban <steamId>`, `unban <steamId>`, `promote <steamId>`, `demote <steamId>` | Discord Administrator | Player actions |

"Discord Administrator" means the Administrator permission in the Discord guild. Other users get "Discord Administrator permission required." Commands that need the server answer "Error: Server not connected." when its agent is not connected. Unknown commands get "Unknown command `<verb>`." plus the help.

### Slash commands

Quasar registers two guild slash commands when the bot connects:

- `/whisper server:<unique name> user:<online player name or Steam ID> message:<text>` sends a private message to an online player. Anyone can run it, but only in a command, chat relay, admin whisper or faction channel bound to that server ("Use /whisper in a channel configured for that server's Discord bridge."). Ambiguous names fail with "Player name '<name>' is ambiguous; use the Steam ID."
- `/faction-channel server:<unique name> faction:<tag>` (Discord Administrator only) creates an admin-only text channel for that faction in the current category, or re-secures the existing one, and saves the binding.

Use the server's unique name, not its display name. Clusters are `cluster:<name>`.

### Chat relay rules

- Game global chat goes to the chat relay channel, whispers to the admin whisper channel, faction chat to that faction's channel from `/faction-channel`. Faction chat without a bound channel is dropped.
- Quasar posts whispers and faction chat only into channels that deny View Channel to @everyone and allow it only to administrator roles and the bot. Otherwise it logs "Refused to relay private ... chat to Discord channel ...; channel is not admin-only." and drops the message.
- Server and system messages are not relayed, except broadcasts sent from the Quasar UI, which appear as **Server**.
- Any non-bot user's message in the chat relay channel goes into the game. Only Discord administrators can post into a faction channel; their messages reach every online member of the faction privately.
- Quasar does not echo Discord messages back to Discord (2-minute suppression window) and relays each game message once.

### SimSpeed alerts

Under **SimSpeed Rules** (all on by default):

| Rule | Settings (default) | Fires when |
| --- | --- | --- |
| **Sharp drop rule** | **Sharp previous minimum** 0.98, **Sharp current threshold** 0.80, **Sharp drop delta** 0.15, **Sharp cooldown (seconds)** 120 | Sim speed was at least the previous minimum, falls to the threshold or below in one sample, and drops by at least the delta |
| **Sustained loss rule** | **Sustained threshold** 0.90, **Sustained window (seconds)** 60 (5 to 3600), **Sustained cooldown (seconds)** 300 | The average sim speed over the window is at or below the threshold |

Alerts go to the SimSpeed alert channel, or the analytics channel when that is empty.

### Settings file

Everything on the Discord page is saved in `<data dir>/discord.json`. Quasar protects the bot token with its ASP.NET Core Data Protection keyring and creates the file owner-readable/writable only on Unix; the other settings remain ordinary JSON. Keep `DataProtection-Keys/` when moving or restoring the install, or enter the token again. On first load, Quasar migrates a legacy plaintext `botToken` immediately. Quasar watches the file and applies edits made on disk without a restart. Bot messages are logged to `discord.log` as well as `quasar.log` (see [Troubleshooting](Troubleshooting.md#where-the-logs-are)).

## Appearance

**Appearance** (`/settings/appearance`, needs `CanManageAppearance`) changes branding for all users. Saved changes apply live to every open session.

- **Brand identity**: **App name** and **Subtitle**.
- **Theme palette**: presets Quasar Default, CometWorks (Quasar default), Midnight Blue, Slate and High Contrast, or custom colors for the light and dark palette, with a live preview.
- **Logos and favicon**: light and dark logos (`.png`, `.jpg`, `.jpeg`, `.webp`, `.svg`) and a favicon (`.ico`, `.png`), up to 10 MiB each. Uploads apply immediately; a cached favicon may need a browser refresh.
- **Save changes** and **Reset to Quasar default**.

Settings are stored in `<data dir>/branding.json` and uploads in `<data dir>/Branding/`. The light, dark or system color theme is a per-browser choice in the header, separate from this page.

## Common problems

- **Charts empty: "No analytics samples match current filters yet."** No server is selected, or the selected servers have no samples in the range. Samples only exist while an agent is connected.
- **Process CPU above 100%** is normal: 100% is one logical CPU.
- **Profiler panels stay empty**: the server's profiler mode is Off, or Extensive panels with Simple mode. Check **Analytics Profiles**.
- **Profiler charts empty after a Quasar restart**: profiler history is memory only.
- **Discord bot shows NotConfigured**: set both **Bot token** and **Guild ID**, check **Enabled**, save.
- **Bot online but ignores text commands**: Message Content Intent is off in the Developer Portal, the message is not in the bound **Command channel ID**, or the prefix is missing. Non-admins can only use `help`, `status` and `chat`.
- **Slash commands missing**: the bot was not invited with `applications.commands`, or the **Guild ID** is wrong. Check `discord.log` for "Failed registering Discord slash commands."
- **Whispers or faction chat never reach Discord**: the channel is not admin-only. Look for "Refused to relay private" in `discord.log`.
- **`/faction-channel` fails**: the bot lacks Manage Channels.
- **Push button disabled**: open the UI over HTTPS or on `localhost`, or remove an old subscription when at 16.
- **Player actions greyed out**: the server's agent is not connected, or your role lacks `CanControlServers`.

## Profile command aliases

Admins can add per-profile shortcuts such as `!grids list` to `!ess grids list`
in **Configs > Command aliases**. Aliases retain the player's permissions. Save
and restart the standalone server, or stage and apply the cluster profile.
See [Command aliases](ConfigProfiles.md#command-aliases) for matching rules,
collisions, limits and the required Magnetar version.

## Sources
- `Quasar/Services/FleetSummary.cs`
- `Quasar/Services/ClusterOperationStore.cs`
- `Quasar/wwwroot/app.css`
- `Docs/AnalyticsCpuLoad.md`
- `Docs/StateMachines/ProfilerMode.md`
- `Docs/StateMachines/AgentConnection.md`
- `Docs/StateMachines/DedicatedServerLifecycle.md`
- `Docs/Configuration.md`
- `README.md`
- `Docs/QuasarArchitecture.md`
- `Quasar/appsettings.json`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Dashboard/ServerCard.razor`
- `Quasar/Components/Dashboard/ServerDetailPanel.razor`
- `Quasar/Components/Dashboard/FleetTelemetryChart.razor`
- `Quasar/Components/Pages/Analytics.razor`
- `Quasar/Components/Pages/Analytics.razor.css`
- `Quasar/wwwroot/quasar-charts.js`
- `Quasar/Components/Pages/Players.razor`
- `Quasar/Components/Pages/Chat.razor`
- `Quasar/Components/Pages/Entities.razor`
- `Quasar/Services/EntityPresentation.cs`
- `Quasar/Services/EntityService.cs`
- `Quasar.Agent/EntityInspector.cs`
- `Magnetar.Protocol/Model/EntitySummary.cs`
- `Quasar/Components/Pages/Notifications.razor`
- `Quasar/Components/Pages/Discord.razor`
- `Quasar/Components/Pages/DiscordConsoleDialog.razor`
- `Quasar/Components/Pages/Appearance.razor`
- `Quasar/Services/Analytics/AnalyticsMetrics.cs`
- `Quasar/Services/Analytics/AnalyticsStoreOptions.cs`
- `Quasar/Services/Analytics/AnalyticsSeriesService.cs`
- `Quasar/Services/Analytics/AnalyticsViewConfig.cs`
- `Quasar/Services/Analytics/MetricsStoreService.cs`
- `Quasar/Services/Analytics/ServerMetricsStore.cs`
- `Quasar/Services/Analytics/ProfilerStoreService.cs`
- `Quasar/Services/Analytics/PluginStatsStoreService.cs`
- `Quasar/Services/AgentRegistry.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/KnownPlayerCatalog.cs`
- `Quasar/Services/ClusterPlayerService.cs`
- `Quasar/Services/ClusterCatalog.cs`
- `Quasar/Services/ClusterCommandService.cs`
- `Quasar/Models/KnownPlayerRecord.cs`
- `Quasar/Services/BrandingPresets.cs`
- `Quasar/Services/Updates/NotificationPreferences.cs`
- `Quasar/Services/Updates/PushNotificationService.cs`
- `Quasar/Services/HostedPanelService.cs`
- `Quasar/Services/Updates/InstanceNotificationMonitor.cs`
- `Quasar/Services/Discord/DiscordBotService.cs`
- `Quasar/Services/Discord/DiscordOptions.cs`
- `Quasar/Services/Discord/DiscordOptionsCatalog.cs`
- `Quasar/Services/Discord/DiscordCommandRouter.cs`
- `Quasar/Services/Discord/DiscordCommandDispatcher.cs`
- `Quasar/Services/Discord/DiscordChatRelayService.cs`
- `Quasar/Services/Discord/DiscordSimSpeedAlertService.cs`
- `Quasar/Services/Discord/DiscordStatusRelayService.cs`
- `Quasar/Services/Discord/DiscordLogRelayService.cs`
- `Quasar/Services/Discord/DiscordAnalyticsExportService.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar.Agent/GameBridge.cs`
- `Quasar.Agent/ConsoleCommandRunner.cs`
- `Quasar.Agent/StopCommand.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`

- `Docs/Announcements.md`
- `Quasar/Services/Updates/AnnouncementService.cs`
- `Quasar/Services/Updates/UpdateNotice.cs`
- `Quasar/Components/Shared/NotificationCard.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar.Tests/AnnouncementTests.cs`
- `Quasar/Components/Shared/NotificationBell.razor`
- `Quasar.Tests/AnnouncementBrowserReceiptTests.cs`
- `Quasar/Services/QuasarCommandAliases.cs`
- `Quasar.Agent/CommandAliasConfiguration.cs`
- `Quasar/Components/Dashboard/ClusterHealthOverview.razor`
- `Quasar/Components/Dashboard/ClusterResourceUsage.razor`
- `Quasar/Services/ClusterFleetService.cs`
- `Quasar/Services/ClusterOverview.cs`
