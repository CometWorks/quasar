# Quasar.Agent/StopCommand.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`StopCommand` is a Quasar-owned PluginSdk command module for the root `!stop` in-game admin command. It overrides Magnetar's earlier `stop` root by being registered later from `AdminPlugin`, acknowledges the caller, then calls `ServerControl.SaveAndQuit()` on a worker task so the world is saved and the dedicated server process exits.

## Structure
**Namespace:** `Quasar.Agent`  **Base:** `CommandModule` (PluginSdk.Commands)  **Modifiers:** public, sealed

| Member | Description |
|---|---|
| `Stop()` | Handles the default root command (`!stop`), responds that save/shutdown has started, then queues `ServerControl.SaveAndQuit()` via `Task.Run`. |

## Dependencies
- `PluginSdk` — `ServerControl.SaveAndQuit`
- `PluginSdk.Commands` — `CommandRootAttribute`, `CommandAttribute`, `CommandModule`
- `System.Threading.Tasks` — `Task.Run`

## Notes
`[Permission]` is intentionally absent, so PluginSdk applies its fail-safe default of admin-only access. The method uses `[Command("")]` so a bare `!stop` runs the command instead of showing the generated command overview. Because this path does not set `GameBridge.QuasarRequestedStop`, `AdminPlugin.OnServerTerminating` reports `AdminStop` to Quasar before exit, flipping the supervisor goal state to `Off`.
