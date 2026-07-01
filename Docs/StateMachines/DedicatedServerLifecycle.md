# Dedicated Server Lifecycle

Quasar supervises each managed Dedicated Server (DS) like an infrastructure
reconciler: an operator sets the **desired goal**, and the supervisor drives the
**observed process** toward it while a **health** assessment feeds automated
recovery. Three separate-but-related state values describe one server:

| State value | Enum | Meaning |
| --- | --- | --- |
| Goal state | [`DedicatedServerGoalState`](../../Quasar/Models/DedicatedServerGoalState.cs) | Desired reconciled outcome (`Off` / `On`). |
| Process state | [`DedicatedServerProcessState`](../../Quasar/Models/DedicatedServerProcessState.cs) | Observed supervisor lifecycle stage. |
| Health state | [`DedicatedServerHealthState`](../../Quasar/Models/DedicatedServerHealthState.cs) | Liveness/quality assessment of a running server. |

The reconciliation loop ([`DedicatedServerSupervisor.ReconcileAsync`](../../Quasar/Services/DedicatedServerSupervisor.cs))
runs every ~2 seconds: it evaluates health, detects process transitions, and
queues `Start` / `Stop` / `Restart` actions to close the gap between goal and
observed state.

---

## Goal state

The desired state. Operator actions usually mutate goal state first, then let
reconciliation perform the transition. Quasar Agent owns the in-game `!stop`,
`!quit`, and `!restart` roots for managed servers. `!stop` saves and reports
`AdminStop`; `!quit` reports `AdminStop` and exits immediately without saving.
Both flip the goal to `Off` (and therefore do **not** treat the shutdown as a
crash to restart). `!restart [seconds]` broadcasts a chat countdown (10 seconds
by default, up to 3600 seconds; longer delays use periodic checkpoint
announcements plus the final 10 seconds), then reports `AdminRestart`, keeps the
goal `On`, moves the observed process to `Restarting`, and lets Quasar relaunch
after the process exits.

```mermaid
stateDiagram-v2
    [*] --> Off
    Off --> On: operator Start / SetGoalStateAsync(On)
    On --> Off: operator Stop / SetGoalStateAsync(Off)
    On --> Off: Quasar Agent !stop / !quit (AdminStop signal)
    On --> On: Quasar Agent !restart [seconds] countdown (AdminRestart signal)
    On --> Off: Discord !stop command
```

![Dedicated server goal state](diagrams/ds-goal-state.png)

| Transition | Trigger | Source |
| --- | --- | --- |
| `Off → On` | Operator/API `SetGoalStateAsync(On)` | `DedicatedServerSupervisor.SetGoalStateAsync` |
| `On → Off` | Operator/API `SetGoalStateAsync(Off)` | `DedicatedServerSupervisor.SetGoalStateAsync` |
| `On → Off` | Quasar Agent `!stop` / `!quit` → agent `AdminStop` | `AgentSocketHandler.ProcessMessageAsync` (`AdminStop` case) |
| `On → On` | Quasar Agent `!restart [seconds]` countdown → agent `AdminRestart` | `AgentSocketHandler.ProcessMessageAsync` (`AdminRestart` case), `DedicatedServerSupervisor.BeginAdminRestartAsync` |
| `On → Off` | Discord `!stop` command | `DiscordCommandDispatcher.DispatchAsync` |

---

## Process state

The observed supervisor lifecycle. The UI treats `Starting`, `Stopping`, and
`Restarting` as transitionary states: `Start`, `Stop`, `Restart`, and `Save`
actions are disabled while a server is already transitioning. `Starting` and
`Restarting` keep an immediate `Kill` action so an accidental or wedged launch
can still be cancelled before the agent attaches. `Running` shows
`Stop`/`Restart`; `Stopped`, `Crashed`, and `Faulted` show `Start`. The
Dashboard problem banner can clear `Crashed`/`Faulted` error status back to
`Stopped` after setting the goal `Off`.

Restart is a supervisor-owned stop/start sequence. Reconciliation does not
auto-schedule an agent-refresh restart. If a connected running server's deployed
Magnetar local `Quasar.Agent.dll` hash differs from the bundled deployable
agent, Quasar warns that a manual restart is required; the subsequent launch prep
copies the bundled plugin into Magnetar's local plugin folder before starting
the DS process.

```mermaid
stateDiagram-v2
    direction LR
    [*] --> Stopped
    Stopped --> Starting: goal On (no restart pending)
    Stopped --> Restarting: goal On (restart pending)
    Starting --> Running: agent attached, snapshot ready
    Starting --> Restarting: attach grace expired, retry budget remains
    Starting --> Stopping: operator Kill (cancel launch)
    Starting --> Faulted: launch preflight/prep failure / attach retries exhausted
    Running --> Stopping: goal Off
    Running --> Restarting: unhealthy, uptime or scheduled
    Running --> Crashed: unexpected exit (code != 0)
    Running --> Stopped: clean exit, not requested
    Restarting --> Starting: relaunch begins
    Restarting --> Stopping: operator Kill pending launch
    Restarting --> Faulted: attempts exhausted
    Stopping --> Stopped: process exited
    Stopping --> Faulted: stop failure
    Crashed --> Restarting: RestartOnCrash, within budget
    Crashed --> Starting: operator Start retry
    Crashed --> Stopped: goal Off / clear error status
    Faulted --> Starting: operator Start after cause fixed
    Faulted --> Stopped: clear error status
```

![Dedicated server process state](diagrams/ds-process-state.png)

| State | Meaning | Normal next states |
| --- | --- | --- |
| `Stopped` | No managed process is running. | `Starting`, `Restarting` |
| `Starting` | Launch in progress; agent/game snapshot not ready yet. | `Running`, `Stopping`, `Faulted` |
| `Running` | Process is alive; agent attached or reconnecting. | `Stopping`, `Restarting`, `Crashed` |
| `Stopping` | Graceful stop in progress; waiting for exit. | `Stopped`, `Faulted` |
| `Restarting` | Intentional restart sequence in progress. | `Starting`, `Running`, `Faulted` |
| `Crashed` | Process exited unexpectedly. | `Starting`, `Restarting`, `Stopped` |
| `Faulted` | Launch/restart failed or attempts exhausted. | `Starting` after the cause is fixed, `Stopped` after clear error status |

**Restart policy & faults** (all in [`DedicatedServerSupervisor`](../../Quasar/Services/DedicatedServerSupervisor.cs)):

- Crash detection: a non-zero exit with no stop requested becomes `Crashed`;
  `HandleProcessExitedAsync` re-launches via `Restarting` when `RestartOnCrash`
  is set and the consecutive attempt budget (`RestartAttempts` vs
  `MaxRestartAttempts`, default 3) is not exhausted, after
  `RestartDelaySeconds`. When the budget is exhausted the server becomes
  `Faulted` and reconciliation does not keep trying. The attempt counter resets
  after a server runs past the reset window.
- `Faulted` is reached from `SetFaulted` on launch-prep failures (missing world
  save selection, missing save files, runtime not ready, executable/working-dir
  missing, world preflight failure, runtime prep failure, process start failure)
  or from crash-restart budget exhaustion. An
  explicit operator `Start` resets the streak and retries after the cause is
  fixed; the reconcile loop does not auto-retry `Crashed`/`Faulted` states.
- World preflight blocks a known Space Engineers init crash: if
  `Sandbox_config.sbc` has `EnableOxygen=false` and the save contains
  oxygen-capable blocks or definitions such as O2/H2 generators, Quasar faults
  before starting the DS process. Enable oxygen or remove the oxygen blocks to
  start the world.
- Clear error status is an explicit operator acknowledgement for a non-running
  `Crashed`/`Faulted` server. It sets the goal `Off`, resets health/restart
  counters and mod-download failure details, and returns the process state to
  `Stopped`.
- Agent attach retries: while a process is still `Starting`, health monitoring
  waits `AgentStartupGraceSeconds` for Quasar.Agent. A socket `hello` is not
  considered fully attached until the first telemetry snapshot arrives, so
  rollover/reconnect startup stays under the startup grace instead of the shorter
  heartbeat timeout. If the agent does not attach and
  `AutoRestartOnUnhealthy` is enabled, the supervisor kills the starting process,
  waits `AgentAttachRetryDelaySeconds`, and relaunches. After
  `AgentAttachRetryAttempts` consecutive attach retries, the server becomes
  `Faulted`.
- Planned restarts come from the health policy (`Unhealthy` +
  `AutoRestartOnUnhealthy`), `MaximumUptime`, `DailyRestartTimeLocal`
  (optionally staggered by `AvoidSimultaneousScheduledRestarts`), and the
  Quasar Agent `!restart [seconds]` command. Agent-requested restart is tracked
  as `Restarting` after the chat countdown and before the process exits; the
  subsequent clean exit is relaunched without consuming crash-restart budget.

---

## Health state

Computed every reconcile pass by `EvaluateHealth`. Health drives automated
recovery: an `Unhealthy` server with `AutoRestartOnUnhealthy` is restarted;
`Warning` only surfaces in the UI / Discord presence.

```mermaid
stateDiagram-v2
    [*] --> Unknown
    Unknown --> Healthy: agent snapshot received, heartbeat fresh, sim progress OK
    Unknown --> Warning: within agent startup grace / waiting for first snapshot / goal Off but still running
    Unknown --> Unhealthy: attach/snapshot grace expired / not running while goal On
    Healthy --> Warning: uptime > warn threshold
    Healthy --> Unhealthy: heartbeat stale / sim progress stalled / uptime > recycle
    Warning --> Healthy: condition clears
    Warning --> Unhealthy: heartbeat stale / sim stalled / uptime > recycle
    Unhealthy --> Healthy: recovers (typically after restart)
    Unhealthy --> Warning: partial recovery
    note right of Unhealthy
        Unhealthy + AutoRestartOnUnhealthy
        triggers a reconcile restart
    end note
```

![Dedicated server health state](diagrams/ds-health-state.png)

| State | When | Effect |
| --- | --- | --- |
| `Unknown` | Monitoring disabled, or transitional (starting/restarting), or `goal Off` and stopped. | None. |
| `Healthy` | Agent telemetry snapshot received, heartbeat fresh, simulation progress above threshold, uptime under warn threshold. | None. |
| `Warning` | Within agent startup grace, waiting for the first telemetry snapshot, uptime past the warn threshold, or `goal Off` but process still running. | Surfaced in UI/Discord only. |
| `Unhealthy` | Agent attach/snapshot grace expired, heartbeat stale beyond `AgentHeartbeatTimeoutSeconds`, simulation-frame progress stalled, uptime past recycle threshold, or process not running while `goal On`. | Auto-restart if `AutoRestartOnUnhealthy`. |

The simulation-frame check mirrors the dedicated server's own watcher:
`frameProgressScore = deltaFrames / (elapsedSeconds * 60)` is compared against a
configurable minimum, with save-in-progress windows resetting the baseline
instead of counting as a stall (`EvaluateSimulationProgress`).
Collecting the first simulation-progress baseline is `Unknown`, not `Warning`;
the card can show the state, but it does not raise a dashboard warning
notification.

---

## Related

- [Agent Connection](AgentConnection.md) — the agent attach/heartbeat that feeds health.
- [Architecture › Process Supervision](../QuasarArchitecture.md#process-supervision)
- Back to the [State Machine Index](Index.md).
