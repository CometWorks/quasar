# Plan: Integrate Magnetar PluginSdk into Quasar

> **Implementation note:** This plan is intentionally high-level. When executing, the AI should use the `se-dev-plugin-sdk` skill (`/home/owendb/Documents/GitHub/Magnetar/skills/se-dev-plugin-sdk/`) for authoritative SDK details, read the current state of all files before editing, and apply its own judgment where the plan is ambiguous or where code has changed since this document was written. Do not blindly follow line numbers or code snippets here — verify against the live codebase first.

## Context

Magnetar's `PluginSdk` lets SE plugins declare configuration via C# attributes. The SDK already produces two artifacts Quasar needs: a `ConfigSchemaData` JSON envelope (schema + defaults + values) and `QuasarLogSink` structured logs. The goal is to wire Quasar end-to-end so it can:
1. Discover which loaded plugins expose configs
2. Render a live editor UI from the schema (no plugin ships UI code)
3. Push config changes back to the running plugin
4. Display plugin logs in the Quasar log stream

Current branch: `features/integration/pluginsdk`

---

## Architecture Overview

```
Game Server
  └── Plugin (IPlugin + IQuasarConfigProvider)
        └── Quasar.Agent (AdminPlugin)
              │  WebSocket (Magnetar.Protocol)
              ▼
           Quasar (Blazor Server)
              └── PluginConfigService → Blazor UI
```

---

## Step 1 — Add `IQuasarConfigProvider` to Magnetar.Protocol

**File:** `Magnetar.Protocol/Bridge/IQuasarConfigProvider.cs` (new)

```csharp
namespace Magnetar.Protocol.Bridge;

public interface IQuasarConfigProvider
{
    string PluginId { get; }
    string GetConfigJson();           // Returns ConfigStorage.SaveJson(config)
    void ApplyConfigJson(string json); // Calls ConfigStorage.LoadJson + applies
}
```

Keep `Magnetar.Protocol` free of PluginSdk dependency — plugins call `ConfigStorage.SaveJson/LoadJson` internally. The interface only exchanges JSON strings.

---

## Step 2 — Extend Wire Protocol

### New models in `Magnetar.Protocol/Model/`

**`PluginConfigData.cs`**
```csharp
public class PluginConfigData
{
    public string PluginId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = string.Empty; // full SaveJson envelope
}
```

**`PluginConfigSnapshot.cs`**
```csharp
public class PluginConfigSnapshot
{
    public List<PluginConfigData> Plugins { get; set; } = [];
}
```

**`PluginConfigUpdateRequest.cs`**
```csharp
public class PluginConfigUpdateRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string ValuesJson { get; set; } = string.Empty; // partial values dict
}
```

### `WireMessageKind.cs` — add constants
```csharp
public const string PluginConfigSnapshot = "plugin-config-snapshot";
public const string PluginConfigUpdate = "plugin-config-update";
```

### `AgentWireMessage.cs` — add fields
```csharp
public PluginConfigSnapshot? PluginConfigSnapshot { get; set; }
public PluginConfigUpdateRequest? PluginConfigUpdateRequest { get; set; }
```

---

## Step 3 — Agent Side (Quasar.Agent)

### `GameBridge.cs`
- Add `GetPluginConfigs()` method:
  - Iterates `AppDomain.CurrentDomain.GetAssemblies()` to find loaded `IPlugin` types
  - Casts to `IQuasarConfigProvider` where available (best-effort, skips non-implementing plugins)
  - Returns `PluginConfigSnapshot`
- Add `ApplyPluginConfig(string pluginId, string valuesJson)`:
  - Finds matching provider, calls `ApplyConfigJson()`
- Subscribe to `INotifyPropertyChanged.PropertyChanged` on each provider's config to detect drift and push updates

### `AgentConnection.cs`
- On connect after `Hello`: send `PluginConfigSnapshot`
- In `ReceiveLoopAsync`: handle `WireMessageKind.PluginConfigUpdate` → call `bridge.ApplyPluginConfig()`
- On config change (PropertyChanged callback): send updated `PluginConfigSnapshot`

---

## Step 4 — Quasar Service Layer

### New service: `Quasar/Services/PluginSdk/PluginConfigService.cs`

Pattern mirrors `DiscordOptionsCatalog` + `DiscordBotService`. Key responsibilities:
- `IHostedService` subscribing to `AgentRegistry` changes
- Maintain `Dictionary<string, List<PluginConfigData>>` keyed by `agentId`
- Handle incoming `plugin-config-snapshot` messages from WebSocket
- Expose `UpdatePluginConfigAsync(agentId, pluginId, valuesJson)` — sends `plugin-config-update` back via `AgentRegistry`
- Emit `Changed` event for Blazor component reactivity

### Wire into `Program.cs`
```csharp
builder.Services.AddSingleton<PluginConfigService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginConfigService>());
```

### WebSocket handler (existing agent WS endpoint)
Add case for `WireMessageKind.PluginConfigSnapshot` → forward to `PluginConfigService`.

---

## Step 5 — Quasar UI (Blazor)

### Schema DTOs in `Quasar/Services/PluginSdk/`
Define POCOs matching `ConfigStorage.SaveJson()` output shape — no PluginSdk project reference needed:
- `PluginConfigEnvelope` (`schema`, `defaults`, `values`)
- `ConfigSchemaDto` (`layout`, `properties`, `structs`, `enums`)
- `ConfigPropertyDto` (`name`, `type`, `optionKind`, `container`, `min`, `max`, `label`, etc.)
- `LayoutContainerDto` (`id`, `parentId`, `kind`, `caption`)

### `PluginConfigEditor.razor` (new Blazor component)
- Receives `PluginConfigEnvelope` as parameter
- Renders layout: tabs → `MudTabs`, sections → `MudExpansionPanel`, columns → `MudGrid`
- Per `optionKind` control:
  - `bool` → `MudCheckBox`
  - `int`/`long`/`float`/`double` → `MudNumericField` with min/max
  - `string` → `MudTextField`
  - `enum` → `MudSelect`
  - `list`/`dict` → `MudDataGrid` (expandable, later phase)
- On field change: calls `PluginConfigService.UpdatePluginConfigAsync()`

### Integrate into existing Plugins page
Extend `/Quasar/Pages/Plugins/` (or relevant existing page) with a tab per plugin that exposes `IQuasarConfigProvider`.

---

## Step 6 — Logging (QuasarLogSink) — implemented via agent relay

`QuasarLogSink` already exists in PluginSdk and outputs one structured JSON line
per entry. Quasar now relays those entries over the existing agent WebSocket so
managed servers can keep streaming plugin logs after Quasar restarts and
reconnects to a detached Magnetar daemon:

1. **Activate the sink.** `DedicatedServerSupervisor.StartProcessAsync` sets the
   `QUASAR_AGENT` env var (= the server unique name) on the DS child process.
   Any non-empty value makes `LogEnvironment.IsManagedByQuasar()` return true, so
   every SDK-using plugin selects `QuasarLogSink` and emits JSON on stdout.
   (Note: while managed by Quasar, plugin logs route to stdout instead of the
   game `MyLog` — this is the SDK's intended behavior.)
2. **Buffer and persist it in-process.** `Quasar.Agent.PluginLogOutbox`
   subscribes to `LogEnvironment.LineEmitted`, appends a human-readable copy to
   the active server Magnetar app-data `info_*.log`, keeps the raw JSON line in
   a bounded retry buffer, and
   drops entries whose structured `plugin` field is `Magnetar` before any batch
   is sent to the control plane.
3. **Relay it.** `AgentConnection` drains the outbox into `PluginLogBatch`
   messages on the existing WebSocket. Failed sends are requeued and retried on
   the next connection.
4. **Parse it.** `AgentSocketHandler` resolves the server unique name from the
   agent connection, calls `PluginLogStream.TryParseSinkLine`, and appends valid
   `{timestamp,level,plugin,thread,message,data?,exception?}` entries.
   `PluginLogStream` also rejects/hides `Magnetar` entries defensively.
5. **Keep readable logs in the instance log.** `DedicatedServerSupervisor.PumpStandardOutputAsync`
   drains stdout so the managed process cannot block and still ignores
   PluginSdk JSON lines for ordinary server-output handling. The agent-side
   outbox formats those plugin stdout sink lines before writing to the server's
   active Magnetar app-data `info_*.log`, so instance-local diagnostics stay readable
   and include plugin output even when
   the live Quasar panel receives the same entries through the WebSocket relay.
6. **Display it.** `PluginLogPanel.razor` (new component, on the Plugins page)
   subscribes to `PluginLogStream.Changed` and renders recent entries
   (time / level / server / plugin / message + exception) in a `MudTable`.

**Files:** `Quasar/Services/PluginSdk/PluginLogEntry.cs` (new),
`Quasar/Services/PluginSdk/PluginLogStream.cs` (new),
`Quasar/Components/PluginLogPanel.razor` (new),
`Quasar.Agent/PluginLogOutbox.cs` (capture + `Magnetar` suppression),
`Quasar.Agent/AgentConnection.cs` (batch relay),
`Quasar/Services/AgentSocketHandler.cs` (ingest),
`Quasar/Services/DedicatedServerSupervisor.cs` (env var + stdout capture),
`Quasar/Program.cs` (register `PluginLogStream`),
`Quasar/Components/Pages/Plugins.razor` (embed panel).

---

## Critical Files

| File | Change |
|---|---|
| `Magnetar.Protocol/Bridge/IQuasarConfigProvider.cs` | New |
| `Magnetar.Protocol/Model/PluginConfigData.cs` | New |
| `Magnetar.Protocol/Model/PluginConfigSnapshot.cs` | New |
| `Magnetar.Protocol/Model/PluginConfigUpdateRequest.cs` | New |
| `Magnetar.Protocol/Transport/WireMessageKind.cs` | Add 2 constants |
| `Magnetar.Protocol/Transport/AgentWireMessage.cs` | Add 2 fields |
| `Quasar.Agent/GameBridge.cs` | Add plugin config discovery + apply |
| `Quasar.Agent/AgentConnection.cs` | Send snapshot on connect, handle update |
| `Quasar/Services/PluginSdk/PluginConfigService.cs` | New |
| `Quasar/Services/PluginSdk/PluginConfigDtos.cs` | New (schema DTOs) |
| `Quasar/Components/PluginConfigEditor.razor` | New |
| `Quasar/Program.cs` | Register `PluginConfigService` |

Reference pattern: `Quasar/Services/Discord/` (IHostedService + options catalog + DI wiring)

---

## Reused Utilities

- `AtomicFileWriter` — for any config persistence on Quasar side
- `AgentRegistry` — existing service for routing messages to/from connected agents
- `MudBlazor` components — `MudTabs`, `MudCheckBox`, `MudNumericField`, `MudSelect`
- `ConfigStorage.SaveJson` / `LoadJson` — called by plugin implementations

---

## Open Question

**Plugin server discovery in the agent:** `GetPlugins()` today reads `MySandboxGame.ConfigDedicated.Plugins` (file paths only, no servers). To get running `IQuasarConfigProvider` servers, the agent could:
- Option A: Scan `AppDomain.CurrentDomain.GetAssemblies()` for `IPlugin` types and reflect for `IQuasarConfigProvider`
- Option B: Use `VRage.Plugins.MyPlugins.Plugins` if that collection is accessible

Option A is safer (no VRage API assumptions). The scan runs once on connect and is cheap.

---

## Verification

1. **Build check:** All 4 projects compile after protocol additions
2. **Agent test:** Deploy agent to a server with a test plugin implementing `IQuasarConfigProvider` → verify `plugin-config-snapshot` message arrives in Quasar logs
3. **UI test:** Open Plugins page in Quasar → editor renders tabs/fields from schema
4. **Round-trip test:** Change a field in the editor → plugin's `PropertyChanged` fires on server side
5. **Existing features:** Discord integration, entity tracker, analytics still work (no regressions in DI wiring)
