# Quasar.Agent/GameBridge.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`GameBridge` is the central game-thread façade for `AgentConnection`. It collects session telemetry (metrics, current profiler mode/snapshot, human players, hidden NPC/bot player ids, kicked players, chat, deaths, plugins), builds `AgentHello` / `AgentSnapshot` wire messages, and executes server commands (chat, blocking save, save-and-stop, profiler mode change, kick, ban, promote, clear-kick-cooldown, entity list/delete) by marshalling work onto the game thread via `MySandboxGame.Invoke`. Save/stop commands route through Magnetar PluginSdk `ServerControl` so Quasar observes completed disk saves before treating the command as successful. Metrics include process CPU derived from `Process.TotalProcessorTime`, simspeed/sim CPU from `Sync`, memory, human player count, PCU, active entities/grids, total blocks, floating objects, latest world-save time, and unsaved game-time progress since the last checkpoint. It enumerates loaded plugins from `MyPlugins.Plugins` (including Pulsar child plugins) for runtime inventory, dedupes configured fallback plugin paths against loaded plugins by path stem, parent dev-folder name, manifest `<Id>`, and manifest `<FriendlyName>`, and exposes plugin configuration through `IQuasarConfigProvider` or Magnetar PluginSdk `PluginConfig` reflection. Chat history normalizes dedicated-server/Good.bot messages to author `Server` and marks `ChatMessageSnapshot.IsServerMessage`.

## Structure
**Namespace:** `Quasar.Agent`  
**Modifiers:** public, concrete

**Key public members:**

| Member | Description |
|---|---|
| `QuasarRequestedStop` (property) | True once a `StopServer` command was received from Quasar |
| `GameBridge(object gameServer)` | Reads `MAGNETAR_HOST_ID` and `QUASAR_UNIQUE_NAME` env vars; captures plugin version only when explicit `AssemblyInformationalVersion` metadata is present |
| `Dispose()` | Unsubscribes the `MySession.OnSaved` handler used to update world-save telemetry. |
| `Update()` | Called each game tick; marks the game thread for profiler attribution, advances continuous profiler publishing, and throttles snapshot refresh to ≤1 Hz via `_lastSnapshotUtc` |
| `GetHello()` | Returns a cached `AgentHello`; thread-safe via `_sync` lock |
| `GetSnapshot()` | Returns a cached `AgentSnapshot`; thread-safe via `_sync` lock |
| `ExecuteCommandAsync(ServerCommandEnvelope, CancellationToken)` | Marshals `ExecuteCommandOnGameThread` via `game.Invoke`; handles `StopServer` and `SetProfilerMode` without a live session |
| `RecordDeath(DeathEventSnapshot)` | Enqueues a death event; capped at 50 entries |
| `GetPluginConfigs()` | Enumerates all plugins, wraps config providers, serializes via `SaveJson`; safe to call off game thread |
| `ApplyPluginConfigAsync(string pluginId, string valuesJson)` | Finds matching provider, marshals `ApplyConfigJson` onto game thread |

**Private nested types:**

- `LoadedPlugin` — carries `IPlugin` reference plus resolved `PluginId` / `DisplayName`
- `DeclaredPlugin` — carries fallback configured-plugin identity plus all dedupe aliases derived from a path or XML manifest
- `ConfigProviderAdapter` — unifies `IQuasarConfigProvider` (explicit) and PluginSdk `PluginConfig` (reflection-based) behind `GetConfigJson()` / `ApplyConfigJson()`

**Commands handled by `ExecuteCommandOnGameThread`:**

`Refresh`, `SendChat`, `SaveWorld`, `StopServer`, `SetProfilerMode`, `KickPlayer`, `BanPlayer`, `UnbanPlayer`, `PromotePlayer`, `DemotePlayer`, `SetPlayerPromoteLevel`, `ClearKickCooldown` (calls `MyMultiplayer.Static.KickClient(steamId, kicked: false)`), `ListEntities`, `DeleteEntity`

**Pulsar interop:** `EnumerateChildPlugins` reflects into `Pulsar.Legacy.Loader.PluginLoader` to discover child plugins by reading its `Plugins` property and each entry's `plugin` field, `Id`, and `FriendlyName`.

## Dependencies
- [`Quasar.Agent/EntityInspector.cs`](EntityInspector.cs.md)
- `Magnetar.Protocol.Bridge` — `IQuasarConfigProvider`
- `Magnetar.Protocol.Model` — `AgentHello`, `AgentSnapshot`, `ServerMetrics`, `PlayerSnapshot`, `ChatMessageSnapshot`, `DeathEventSnapshot`, `PluginConfigSnapshot`, `PluginConfigData`, `PluginRuntimeInfo`, `ServerCommandEnvelope`, `ServerCommandResult`, `ServerCommandType`
- [`Quasar.Agent/AgentProfiler.cs`](AgentProfiler.cs.md)
- `Magnetar.Protocol.Transport` — wire transport types
- `PluginSdk` — `ServerControl` blocking save / save-and-quit facade bound by Magnetar
- `PluginSdk.Config` — `PluginConfig`, `ConfigStorage`, `ConfigOptionAttribute`
- `VRage.Plugins` — `IPlugin`, `MyPlugins`
- `Sandbox` — `MySandboxGame`
- `Sandbox.Engine.Multiplayer` — `MyMultiplayer`, `MyDedicatedServer`
- `Sandbox.Game.Entities` — `MyCubeGrid`, `MyFloatingObject`, `MyEntities`
- `Sandbox.Game.Gui` — chat channel types
- `Sandbox.Game.Multiplayer` — `Sync`
- `Sandbox.Game.World` — `MySession`, `MyAsyncSaving`
- `Sandbox.Engine.Networking` — `MyLocalCache` checkpoint loading for initial save timestamp/progress baseline
- `VRage.Game.ModAPI` — `MyPromoteLevel`
- `Newtonsoft.Json` — payload serialization

## Notes
- Constructor reads `MAGNETAR_HOST_ID` (not `MAGNETAR_NODE_ID`) to populate `_hostId`; this reflects the Node→Host rename.
- Snapshot state is guarded by `_sync` (object lock); `_quasarRequestedStop` is `volatile` for lock-free reads from the termination handler.
- Plugin config reads (`GetPluginConfigs`) are intentionally off-thread for responsiveness; applies are marshalled to the game thread.
- Private `GetKickedPlayers(MySession)` populates `AgentSnapshot.KickedPlayers` by reading `MyMultiplayer.Static.KickedClients` and `MyMultiplayerBase.KICK_TIMEOUT_MS` to compute the remaining cooldown per SteamId.
- Private `GetRecentChat()` reads `MyDedicatedServer.GlobalChatHistory`; messages with SteamId 0, author `Good.bot`, or author `Server` are treated as server-authored, exposed as `Server`, and flagged with `IsServerMessage`.
- `ConfigProviderAdapter` uses `MethodInfo` reflection to invoke generic `ConfigStorage.SaveJson<T>` / `LoadJson<T>` because `T` is only known at runtime.
- `ApplyConfigJson` for SDK configs copies only properties decorated with `[ConfigOption]` to preserve non-option fields.
- Runtime plugin inventory uses `EnumeratePlugins()` so Magnetar/Pulsar-loaded plugins appear even when `MySandboxGame.ConfigDedicated.Plugins` is empty; configured plugin paths are still added as `declared` fallback rows only when not already represented by a loaded plugin. XML manifest fallback rows are matched against loaded plugins by full path, path stem, parent dev-folder/source name, `<Id>`, and `<FriendlyName>`, preventing duplicate local dev-folder rows.
- `SaveWorld` returns success only after `ServerControl.SaveWorld()` reports completion; `StopServer` marks `_quasarRequestedStop` and calls `ServerControl.SaveAndQuit()` so Magnetar owns the save, plugin disposal, and process exit path.
- World-save telemetry is initialized once per `MySession.CurrentPath` by loading `Sandbox.sbc` through `MyLocalCache.LoadCheckpoint`, updated optimistically from `MySession.OnSaved`, then refreshed from the checkpoint once async save progress ends; `UnsavedGameTimeSeconds` is based on `MySession.ElapsedGameTime`, so offline wall-clock time is not counted as unsaved progress.
- Private `GetServerName(MySession)` reports `MySandboxGame.ConfigDedicated?.ServerName` (the configured server name shown in the server browser), falling back only to `Space Engineers {processId}`. It deliberately does **not** fall back to `session?.Name`, which is the loaded world/save name (matching the world template) rather than the server — this keeps the per-server name used by the UI's server filters distinct from the world name.
- Private `GetPlayers(MySession)` and `GetOnlinePlayerCount(MySession)` filter online `MyPlayer` entries to human players only before filling `AgentSnapshot.Players` or `ServerMetrics.PlayersOnline`: SteamId must be non-zero, `IsBot` must be false, and `MySession.Players.IdentityIsNpc(identityId)` must be false. Filtered ids are reported through `AgentSnapshot.HiddenPlayerSteamIds` / `HiddenPlayerIdentityIds` so Quasar can remove stale known-player rows. Wolves, spiders, and NPC identities remain available through entity inspection, but do not appear in Quasar player rows or player counts.
