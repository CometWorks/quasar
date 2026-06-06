# Quasar.Agent/GameBridge.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`GameBridge` is the central game-thread façade for `AgentConnection`. It collects session telemetry (metrics, players, kicked players, chat, deaths, plugins), builds `AgentHello` / `AgentSnapshot` wire messages, and executes server commands (chat, save, stop, kick, ban, promote, clear-kick-cooldown, entity list/delete) by marshalling work onto the game thread via `MySandboxGame.Invoke`. It also enumerates all loaded plugins and exposes their configuration through `IQuasarConfigProvider` or Magnetar PluginSdk `PluginConfig` reflection.

## Structure
**Namespace:** `Quasar.Agent`  
**Modifiers:** public, concrete

**Key public members:**

| Member | Description |
|---|---|
| `QuasarRequestedStop` (property) | True once a `StopServer` command was received from Quasar |
| `GameBridge(object gameServer)` | Reads `MAGNETAR_HOST_ID` and `QUASAR_UNIQUE_NAME` env vars; captures plugin version |
| `Update()` | Called each game tick; throttles snapshot refresh to ≤1 Hz via `_lastSnapshotUtc` |
| `GetHello()` | Returns a cached `AgentHello`; thread-safe via `_sync` lock |
| `GetSnapshot()` | Returns a cached `AgentSnapshot`; thread-safe via `_sync` lock |
| `ExecuteCommandAsync(ServerCommandEnvelope, CancellationToken)` | Marshals `ExecuteCommandOnGameThread` via `game.Invoke`; handles `StopServer` without a live session |
| `RecordDeath(DeathEventSnapshot)` | Enqueues a death event; capped at 50 entries |
| `GetPluginConfigs()` | Enumerates all plugins, wraps config providers, serializes via `SaveJson`; safe to call off game thread |
| `ApplyPluginConfigAsync(string pluginId, string valuesJson)` | Finds matching provider, marshals `ApplyConfigJson` onto game thread |

**Private nested types:**

- `LoadedPlugin` — carries `IPlugin` reference plus resolved `PluginId` / `DisplayName`
- `ConfigProviderAdapter` — unifies `IQuasarConfigProvider` (explicit) and PluginSdk `PluginConfig` (reflection-based) behind `GetConfigJson()` / `ApplyConfigJson()`

**Commands handled by `ExecuteCommandOnGameThread`:**

`Refresh`, `SendChat`, `SaveWorld`, `StopServer`, `KickPlayer`, `BanPlayer`, `UnbanPlayer`, `PromotePlayer`, `DemotePlayer`, `SetPlayerPromoteLevel`, `ClearKickCooldown` (calls `MyMultiplayer.Static.KickClient(steamId, kicked: false)`), `ListEntities`, `DeleteEntity`

**Pulsar interop:** `EnumerateChildPlugins` reflects into `Pulsar.Legacy.Loader.PluginLoader` to discover child plugins by reading its `Plugins` property and each entry's `plugin` field, `Id`, and `FriendlyName`.

## Dependencies
- [`Quasar.Agent/EntityInspector.cs`](EntityInspector.cs.md)
- `Magnetar.Protocol.Bridge` — `IQuasarConfigProvider`
- `Magnetar.Protocol.Model` — `AgentHello`, `AgentSnapshot`, `ServerMetrics`, `PlayerSnapshot`, `ChatMessageSnapshot`, `DeathEventSnapshot`, `PluginConfigSnapshot`, `PluginConfigData`, `PluginRuntimeInfo`, `ServerCommandEnvelope`, `ServerCommandResult`, `ServerCommandType`
- `Magnetar.Protocol.Transport` — wire transport types
- `PluginSdk.Config` — `PluginConfig`, `ConfigStorage`, `ConfigOptionAttribute`
- `VRage.Plugins` — `IPlugin`, `MyPlugins`
- `Sandbox` — `MySandboxGame`
- `Sandbox.Engine.Multiplayer` — `MyMultiplayer`, `MyDedicatedServer`
- `Sandbox.Game.Entities` — `MyCubeGrid`, `MyEntities`
- `Sandbox.Game.Gui` — chat channel types
- `Sandbox.Game.Multiplayer` — `Sync`
- `Sandbox.Game.World` — `MySession`, `MyAsyncSaving`
- `VRage.Game.ModAPI` — `MyPromoteLevel`
- `Newtonsoft.Json` — payload serialization

## Notes
- Constructor reads `MAGNETAR_HOST_ID` (not `MAGNETAR_NODE_ID`) to populate `_hostId`; this reflects the Node→Host rename.
- Snapshot state is guarded by `_sync` (object lock); `_quasarRequestedStop` is `volatile` for lock-free reads from the termination handler.
- Plugin config reads (`GetPluginConfigs`) are intentionally off-thread for responsiveness; applies are marshalled to the game thread.
- Private `GetKickedPlayers(MySession)` populates `AgentSnapshot.KickedPlayers` by reading `MyMultiplayer.Static.KickedClients` and `MyMultiplayerBase.KICK_TIMEOUT_MS` to compute the remaining cooldown per SteamId.
- `ConfigProviderAdapter` uses `MethodInfo` reflection to invoke generic `ConfigStorage.SaveJson<T>` / `LoadJson<T>` because `T` is only known at runtime.
- `ApplyConfigJson` for SDK configs copies only properties decorated with `[ConfigOption]` to preserve non-option fields.
- Private `GetServerName(MySession)` reports `MySandboxGame.ConfigDedicated?.ServerName` (the configured server name shown in the server browser), falling back only to `Space Engineers {processId}`. It deliberately does **not** fall back to `session?.Name`, which is the loaded world/save name (matching the world template) rather than the server — this keeps the per-server name used by the UI's server filters distinct from the world name.
