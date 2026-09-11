# Analytics CPU usage and Shift+F11

Quasar's Analytics **Process CPU %** chart measures total dedicated-server process CPU
usage, with **100% equal to one fully occupied logical CPU**. Values above 100%
are valid. A reading of 150–160% represents about 1.5–1.6 logical CPUs of work
across the process's threads; it does not mean the entire machine is overloaded.

The in-game Shift+F11 **Server simulation CPU Load** measures a different quantity:
the elapsed time spent updating a server frame, as a percentage of the nominal
16.67 ms budget at 60 updates per second. A reading of 20–30% corresponds to about
3.3–5 ms per update. These readings can coexist with simulation speed near 1.0
and smooth gameplay. Process CPU usage alone does not establish simulation lag.

Shift+F11 also has a **Simulation CPU Load** row without the word **Server**.
That row measures the local game client. Use the server row when comparing server
performance.

## Charts, history, and policies

Analytics includes **Process CPU %** (`cpu`) and **Server Simulation CPU %**
(`simcpu`) as separate panels, visible by default. Existing saved layouts pick up
the new simulation panel automatically. Each chart explains its units; both
allow values above 100%. Simulation CPU above 100% means an update exceeded the
nominal frame budget.

Both values are retained in raw samples and minute/hour averages and survive
Quasar restarts. Historical entries without simulation CPU appear as gaps, not
zeroes. Averages ignore missing simulation samples while preserving real zeroes.
The existing process CPU series keeps its key and meaning.

Summary chips show the latest values averaged across selected servers. Discord
analytics reports show separate averages for both CPU metrics. The synthetic
analytics generator also emits both fields, including process usage above 100%.

Neither CPU percentage controls Quasar alerts, restarts, throttling, or other
automated policies. Discord performance alerts use **SimSpeed**; supervisor
simulation-stall detection uses **simulation-frame progress**.

## Source evidence

Investigated on 2026-09-11 at Quasar commit `a282f27`, in branch
`investigate/analytics-cpu-load`. The reported server was not profiled and no Quasar service was started.
The implementation below follows that investigation.

### Quasar collection and charting

- [`GameBridge.GetProcessCpuLoadPercent`](../Quasar.Agent/GameBridge.cs) calculates
  `100 * delta(Process.TotalProcessorTime) / delta(wall-clock time)`. There is no
  division by processor count and no upper clamp at 100%.
  [`Process.TotalProcessorTime`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.totalprocessortime)
  includes the process's user and privileged CPU time.
- `GameBridge.BuildMetrics` assigns that result to `ServerCpuLoadPercent` and
  separately assigns `Round(Sync.ServerCPULoad, 1)` to `SimCpuLoadPercent`.
- [`MetricSampleFactory.FromSnapshot`](../Quasar/Services/Analytics/MetricSampleFactory.cs)
  copies `ServerCpuLoadPercent` into historical `CpuPercent` and
  `SimCpuLoadPercent` into nullable `SimCpuPercent`. Invalid simulation samples
  (negative or non-finite) are treated as missing.
- [`AnalyticsMetrics`](../Quasar/Services/Analytics/AnalyticsMetrics.cs) selects
  `CpuPercent` for the `cpu` panel and `SimCpuPercent` for `simcpu`, with
  explanatory subtitles and no fixed maximum. JSONL persistence uses `Cpu` and
  optional `Scpu`; absent `Scpu` stays missing when older history is loaded.
- [`RrdRollupBuffer`](../Quasar/Services/Analytics/RrdRollupBuffer.cs) and
  [`AnalyticsSeriesService`](../Quasar/Services/Analytics/AnalyticsSeriesService.cs)
  average CPU samples within time buckets. They do not multiply CPU percentages
  or add different servers together into a chart series.

### Space Engineers calculation

Verified directly against local decompiled Dedicated Server **1.210.014 b0** in
the `se-dev-server-code` skill's `Data/Decompiled` tree. The server handbook reports
**1.209.024 b0**, so its summaries were not used as authoritative evidence.
Paths below are relative to that decompiled tree:

| Source | Relevant behavior |
| --- | --- |
| `Sandbox.Game/Sandbox/Engine/Platform/Game.cs`, `RunSingleFrame` | Times `UpdateInternal`; calculates `CPULoad = elapsedSeconds / (1 / 60) * 100`. Frame limiting occurs afterward. This measures elapsed update time, not OS process CPU time. |
| `Sandbox.Game/Sandbox/Game/Multiplayer/Sync.cs`, `ServerCPULoad` | On the server, reads `MySandboxGame.Static.CPULoad`. |
| `Sandbox.Game/Sandbox/Engine/Multiplayer/MyMultiplayerServerBase.cs`, `WriteCustomState` | Sends server `CPULoad` to clients. |
| `Sandbox.Game/Sandbox/Engine/Multiplayer/MyMultiplayerClientBase.cs`, `ReadCustomState` | Receives that value and updates `ServerCPULoad` and `ServerCPULoadSmooth`. |
| `Sandbox.Game/Sandbox/Game/Gui/MyGuiScreenDebugTiming.cs`, `Update` | Supplies integer `Sync.ServerCPULoadSmooth` for the server CPU row and local `CPULoadSmooth` for the client row. |
| `VRage.Library/VRage/Stats/MyStatKeys.cs` | Defines the distinct client and server labels. |

The agent's simulation CPU value is an unsmoothed sample; the in-game server row
is smoothed and truncated to an integer. The simulation CPU chart therefore does
not necessarily match the display exactly at each instant.

Process time covers work on all server threads, including work outside the
measured update interval. The two metrics therefore cannot be converted into
each other by dividing by the machine's processor count. Identifying which
threads account for this particular server's 150–160% would require live profiling.

### History and issue status

Commit [`518868e`, Deep metric telemetry](https://github.com/CometWorks/quasar/commit/518868e6ebf21ad409eac0d59af8019965606d91),
dated 2026-06-07, changed `ServerCpuLoadPercent` from `Sync.ServerCPULoad` to the
process-time calculation while retaining the separate simulation CPU field.
Thus the graph's meaning changed at that commit; the discrepancy is explained
by the current implementation.

A GitHub issue search for `cpu`, including closed issues, returned no results
on the investigation date. This does not establish that the question has never
been discussed elsewhere.

