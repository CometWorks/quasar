# Plan: Admin Entity List Management System

> **Research note:** Implementation details for SE types should be verified
> using the `se-dev-game-book` and `se-dev-server-book` skills before coding.
> Deep investigation was intentionally deferred — this is a high-level design
> document. Use `se-dev-game-code` / `se-dev-server-code` skills to read live
> decompiled source for any load-bearing SE API calls.

---

## Context

Space Engineers Dedicated Server tracks all active world entities through
`MyEntities` (static manager class). Admins currently have no visibility into
the live entity list from the Quasar web UI. This feature adds a real-time
entity browser: list, filter, inspect, and delete entities per instance, with
full admin controls.

**Out of scope (later stage):** 3D render preview / world-space visualization
of entity positions. The grid viewer / minimap render is a follow-on feature.
Note this in the roadmap — the entity data model should include `WorldAABB` /
`WorldMatrix` from the start so the renderer can consume it without a schema
change later.

---

## SE Entity Model (high level)

All live entities descend from `MyEntity`. The types relevant to admin
management:

| Type | Description | Key fields |
|---|---|---|
| `MyCubeGrid` | Ship / station grid | `EntityId`, `DisplayName`, `BlocksCount`, PCU, `IsStatic`, `GridSizeEnum` (Large/Small), owner/built-by steam IDs, `WorldAABB` |
| `MyCharacter` | Player character / corpse | `EntityId`, `DisplayName`, `ControllerInfo.Controller.Player`, position |
| `MyFloatingObject` | Loose items in space | `EntityId`, item definition, position |
| `MyVoxelBase` | Asteroids / planets | `EntityId`, `StorageName`, size |

Querying is done via `MyEntities.GetEntities()` which returns a
`HashSet<MyEntity>`. **Must be called on the game thread** — use
`MySandboxGame.Static.Invoke()` or `MyAPIGateway.Utilities` marshal helpers.
Deletions also require the game thread.

> **Verify:** Exact API surface for thread-safe entity enumeration and removal.
> Use `se-dev-game-book` → MyCubeGrid, MyEntities, MyCharacter entries.
> Cross-check Torch plugin patterns via `se-dev-torch` skill.

---

## Architecture

```
[SE Server + Magnetar Agent]
  ├── EntityQueryHandler   ← new handler in Quasar.Agent
  └── EntityDeleteHandler  ← new handler in Quasar.Agent

      ↕  WebSocket (existing Magnetar protocol)

[Quasar Web]
  ├── Protocol messages    ← new request/response types in Magnetar.Protocol
  ├── EntityService        ← singleton, per-instance entity state cache
  └── Entities.razor       ← new page at /instances/{id}/entities
```

The existing WebSocket agent pipeline already handles request/response
message dispatch — new handlers follow the same pattern as health/player
management handlers already in `Quasar.Agent`.

---

## Components to Build

### 1. Protocol Messages — `Magnetar.Protocol`

New message types (follow existing pattern):

- `EntityListRequest` — filter params: type filter (All / Grids / Characters /
  Floats / Voxels), search string, page/limit
- `EntityListResponse` — list of `EntitySummary` DTOs
- `EntityDeleteRequest` — `EntityId` (long), optional confirmation token
- `EntityDeleteResponse` — success/error

**`EntitySummary` DTO fields:**
```
EntityId       long
DisplayName    string
TypeTag        string   // "Grid" | "Character" | "Float" | "Voxel"
SubType        string   // e.g. "LargeStatic" | "LargeShip" | "SmallShip"
BlockCount     int?     // grids only
Pcu            int?     // grids only
OwnerSteamId   ulong?   // grids only
Position       Vector3D // world position (centre of AABB)
WorldAabb      BoundingBoxD  // for future renderer use
LastSeen       DateTime
```

### 2. Agent Handlers — `Quasar.Agent`

**`EntityQueryHandler`**
- Marshals onto game thread via `MySandboxGame.Static.Invoke`
- Calls `MyEntities.GetEntities()`, filters by type, maps to `EntitySummary`
- Returns paginated `EntityListResponse`

**`EntityDeleteHandler`**
- Validates entity ID exists
- Calls `MyEntities.RemoveEntity()` on game thread
- Returns success/error response
- Logs admin action with steamId of requester

> **Verify thread marshalling pattern** against existing agent handlers
> (health/player handlers) and against `se-dev-server-code` for safe
> `MyEntities.RemoveEntity` usage.

### 3. Quasar Service — `EntityService.cs`

Singleton service in `Quasar` project.

- `GetEntitiesAsync(instanceId, filter)` — sends `EntityListRequest` over
  WebSocket, awaits response
- `DeleteEntityAsync(instanceId, entityId)` — sends `EntityDeleteRequest`
- Optional: short TTL cache (5–10 s) to avoid hammering the agent on rapid
  UI refreshes
- Exposes `event Action? Changed` if push-based updates are added later
  (not required for v1 — poll on page load + manual refresh)

### 4. UI Page — `Entities.razor`

Route: `@page "/instances/{InstanceId}/entities"`

Layout (consistent with existing Quasar pages):

```
PageTitle: "Entities — {instanceName}"
MudText h4: "Entities"
MudText body2: "Live entity list for this instance."

Toolbar row:
  MudSelect: Type filter (All / Grids / Characters / Floating / Voxels)
  MudTextField: Search (name / ID)
  MudButton: Refresh
  MudText: "Last updated: {timestamp}"

MudDataGrid<EntitySummary>:
  Columns: Type | Name | Sub-type | Blocks | PCU | Owner | Position | Actions
  Actions column: MudIconButton Delete (with confirmation dialog)
  Sortable: Name, BlockCount, PCU
  Virtual scrolling or pagination (server-side page if > 500 entities)

Empty state: "No entities found" / "Instance offline"
```

No render/minimap preview in this page. Position is shown as text coordinates
only. The `WorldAabb` field is stored in the DTO for future use by the
renderer.

**Delete flow:**
1. Click delete icon → `MudMessageBox` confirmation ("Delete {name}?")
2. Call `EntityService.DeleteEntityAsync`
3. Snackbar success/error
4. Refresh entity list

### 5. NavMenu Entry

Add "Entities" link under each instance context, or as a top-level nav item
under the existing "Instances" section. Exact placement TBD based on nav
information architecture.

---

## Future: 3D Render Preview

> **Deferred — do not implement in this phase.**

A future stage will add a 3D or 2D minimap render of entity positions using
`WorldAabb` / `WorldMatrix` data already captured in `EntitySummary`. Options:

- Three.js / Babylon.js canvas panel embedded in the Entities page
- Top-down 2D SVG grid showing grid positions by size and type
- Click-to-focus entity from render view

The `EntitySummary` DTO is designed to support this without schema changes.

---

## Implementation Order

1. Protocol messages (`EntitySummary`, request/response types)
2. Agent handlers (query + delete, game-thread marshalling)
3. `EntityService` in Quasar (WebSocket send/receive wiring)
4. `Entities.razor` UI page
5. NavMenu integration
6. Manual test: connect to live instance, list entities, delete a test grid

---

## Key References for Implementation

| Resource | Purpose |
|---|---|
| `se-dev-game-book` skill | `MyEntities`, `MyCubeGrid`, `MyCharacter` type docs |
| `se-dev-server-book` skill | Server-side entity lifecycle, game thread rules |
| `se-dev-torch` skill | Thread marshalling patterns from Torch plugin examples |
| `se-dev-server-code` skill | Read live decompiled source for exact API verification |
| Existing `PlayerManagement` handlers | Pattern reference for agent handler structure |
| `Magnetar.Protocol` existing messages | Pattern for new request/response types |
