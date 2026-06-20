# Quasar.Agent/StopCommand.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`StopCommand` is a Quasar-owned PluginSdk command module for the root `!stop` in-game admin command. It overrides Magnetar's earlier `stop` root by being registered later from `AdminPlugin`, acknowledges the caller, reports an admin stop to Quasar through a static hook wired by `AdminPlugin`, then calls `ServerControl.SaveAndQuit()` on a worker task so the world is saved and the dedicated server process exits.

## Structure
**Namespace:** `Quasar.Agent`  **Base:** `CommandModule` (PluginSdk.Commands)  **Modifiers:** public, sealed

| Member | Description |
|---|---|
| `AdminStopRequested` | Static hook assigned by `AdminPlugin`; lets the command send Quasar's `AdminStop` signal before shutdown starts. |
| `Stop()` | Handles the default root command (`!stop`), responds that save/shutdown has started, then queues admin-stop notification plus `ServerControl.SaveAndQuit()` via `Task.Run`. |
| `TryNotifyAdminStopRequested()` | Best-effort wrapper around `AdminStopRequested` so a notification failure cannot block the server shutdown. |

## Dependencies
- `PluginSdk` — `ServerControl.SaveAndQuit`
- `PluginSdk.Commands` — `CommandRootAttribute`, `CommandAttribute`, `CommandModule`
- `System` — `Action`
- `System.Threading.Tasks` — `Task.Run`

## Notes
`[Permission]` is intentionally absent, so PluginSdk applies its fail-safe default of admin-only access. The method uses `[Command("")]` so a bare `!stop` runs the command instead of showing the generated command overview. The command reports `AdminStop` before calling `SaveAndQuit`, flipping the supervisor goal state to `Off` while the agent socket is still available. `AdminPlugin.OnServerTerminating` remains a fallback for other admin-initiated shutdowns and dedupes duplicate reports.
