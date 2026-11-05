# Quasar browser SDK

The version 1 SDK provides typed operations, a shared authorized dispatcher,
endpoint authorization settings, binary reads, staged uploads and subscriptions.
All 15 Entity Viewer operations use it. Built-in analytics and log downloads use
the same boundary. HTTP is the implemented adapter; WebRTC is separate work.
See the [transport contract](QuasarSdkTransport.md) for verified session binding,
expiry/logout, resource limits and commit behavior.

## Register an operation

Use `Quasar.Plugin.Abstractions.Operations` in the plugin's existing
`ConfigureServices` method. The plugin owns request/response types and feature
validation; Quasar owns transport and authorization.

```csharp
services.AddQuasarOperation<StatusRequest, StatusResponse>(
    context.PluginId, "status.read", [QuasarPolicyNames.CanView],
    async (caller, request, token) =>
        await caller.Services.GetRequiredService<PluginStatusService>()
            .ReadAsync(request, token));
```

Plugin and operation IDs are lowercase identifiers of at most 128 characters.
Duplicate IDs fail startup. The default policy list must contain registered
policies; missing, empty or unknown policies deny invocation. Every operation
requires an authenticated principal accepted by Quasar, independently of policy
overrides. A plugin does not need an endpoint-specific auth attribute for this.
Existing machine credentials remain restricted to their existing APIs.

For a read-only legacy URL that must share the operation's settings, use:

```csharp
endpoints.MapQuasarOperationRead(
    "/_quasar/plugins/example.plugin/api/status", context.PluginId, "status.read");
```

This maps to the same dispatcher rather than adding an independent handler or
static policy gate. Preserve any additional feature checks in the registered
handler. Other existing plugin endpoints remain supported.

## Operation metadata, audit and undo

Every registration helper takes an optional `QuasarOperationMetadata`:

```csharp
services.AddQuasarOperation<SetMotdRequest, SetMotdResponse>(
    context.PluginId, "servers.motd.set", [QuasarPolicyNames.CanEditConfigs],
    async (caller, request, token) =>
    {
        var service = caller.Services.GetRequiredService<MotdService>();
        caller.Undo!.SetData(new { request.Server, before = service.Get(request.Server), after = request.Text });
        caller.Undo.Server = request.Server;
        return await service.SetAsync(request.Server, request.Text, token);
    },
    new QuasarOperationMetadata { Reversal = QuasarReversal.Undo, Undo = new MotdUndo() });
```

- `Effect` is `Read` or `Change`. Read and subscription operations default to
  `Read`, JSON and upload operations to `Change`. Declare `Effect = Read` on a
  JSON operation that changes nothing. Quasar writes an audit entry for every
  change operation call, and an assistant can only propose a change.
- `Area` is a lowercase identifier such as `servers` or `config`. It defaults to
  the first `.`-separated segment of the operation ID.
- `Reversal` is `NotReversible` (the default), `NothingToReverse` (a save, a
  kick, a chat message) or `Undo`, which needs an `IQuasarUndoHandler` in `Undo`.
- `Deadline` replaces the 30-second deadline of JSON operations; 1 to 300 seconds.
  An invalid value fails registration.
- `AgentAccess = Denied` closes the operation to both agent principals.
- `Description` is one or two plain sentences for a person or a model: what the
  operation does, what it returns and when it applies. The assistant uses it as
  the tool description, so mention defaults and limits of the request there.

`QuasarOperation.RequestType` is the request type a registration helper
deserializes, so a consumer can build the input schema with
`JsonSchemaExporter.GetJsonSchemaAsNode(options, operation.RequestType)`. Keep
request types plain records of strings, numbers, booleans, lists and nested
records; nullable properties and defaults show up in the schema.

During a change operation, `QuasarOperationContext.Undo` records what an undo
needs: `SetData` for small values kept inline in the audit entry,
`AddBlobAsync` for files, such as a replaced file or a grid blueprint, and
`Server` for the audit page's server filter. An `Undo` operation that records
nothing is audited as not reversible.

Undo is for admins only, from the **Audit log** page. Quasar first calls the
handler's `CheckAsync`: null means the current state is still what the change
left; otherwise it returns a short description of the difference, which the
admin sees before choosing **Undo anyway**. Then it calls `ReverseAsync` and
writes a new entry that points at the reversed one. The handler gets the
recorded data and the entry's blob folder in `QuasarUndoContext`. The admin
needs `CanManageSecurity` and the original operation's current policies. Undo
data and blobs expire with the entry, after `Quasar:Audit:RetentionDays`
(default 30).

The audit log is `audit/<yyyy-MM>.jsonl` in the Quasar data folder, one JSON
object per line, with blobs in `audit/blobs/<entry id>/`. Argument values whose
names look like a password, token, secret or key are stored as `<redacted>`.
`IQuasarAuditLog` lets a plugin record an agent's proposal and query entries.
Only changes made through the operation registry are audited.

### Agent principals

Quasar core builds two agent principals in process: the admin assistant, which
acts for a person, and the player agent, which acts for one player. Nothing
read from a cookie, token, header or the SDK wire can be one. A plugin creates
the assistant with `IQuasarAgentPrincipals.CreateAssistantAsync`, which needs a
person allowed `CanUseAssistant` (editor and admin by default). Test for an agent
with `principal.IsQuasarAgent(out var agent)`.

For an agent call the dispatcher also:

- refuses operations declared `AgentAccess = Denied`, operations whose
  effective rule requires `CanManageSecurity` or `CanShutdownQuasar`, and
  operation IDs starting with `security.`, `rbac.`, `auth.`,
  `service-principals.`, `update.`, `updates.`, `workshop.credentials`,
  `discord.token`, `assistant.settings` or `llm.`, under any plugin ID;
- checks the agent's own roles from `agentRoles` in `rbac.json` (assistant
  `editor`, player none by default; never `admin`);
- checks the assistant's asker too, so it never does more than the asker could
  do by hand, and the approver of a proposal (`QuasarOperationContext.ApprovedBy`).

Player-scoped operations use the `CanActAsPlayer` policy. No role grants it and
a role override cannot; only the player agent satisfies it.

## Browser requests

In `ConfigureEndpoints`, expose a Quasar-owned discovery module at each plugin
asset prefix, for example:

```csharp
endpoints.MapQuasarSdkClient("/_quasar/plugins/example.plugin/_sdk/client.js");
```

The browser feature imports the bridge relative to its own asset URL. Quasar
initializes it with the correct local or hosted panel base:

```javascript
import { client } from "./_sdk/client.js";

const stop = new AbortController();
try {
    const status = await client.request("example.plugin", "status.read", {}, { signal: stop.signal });
    renderStatus(status);
} catch (error) {
    if (error.name !== "AbortError") showError(error.message);
}
// On feature teardown: stop.abort(). Shared clients remain available to other features.
// A client owned solely by this view can instead be disposed with client.dispose().
```

Use `createClient({ baseUrl })` from `/_quasar/sdk/v1/client.js` only when the host
already supplies an explicit same-origin panel URL ending in `/`. Plugin code
must not guess a panel ID or discover a private network address. A standalone
preview must explicitly supply an adapter; importing the module grants no session.

The client obtains a request-verification token from the authenticated
`/_quasar/sdk/v1/session` endpoint and posts JSON to
`/_quasar/sdk/v1/operations/<plugin-id>/<operation-id>`. It preserves the panel
prefix, sends same-origin credentials, propagates cancellation and refuses
redirects. It does not retry operations. `QuasarSdkError.status` distinguishes
HTTP errors; aborted requests retain the browser's cancellation error.

Control JSON is limited to 64 KiB and depth 32, with a 30-second handler deadline.
The handler must honor cancellation before side effects. A timed-out write can
have an unknown result, so callers must not blindly replay it. Responses are
private no-store. Use the streaming API for large binary data rather than
base64-encoding bytes into control JSON.

## Reads, uploads and subscriptions

Register feature-owned resource definitions, never browser-supplied filesystem
paths or machine credentials. The dispatcher checks the declared/effective policy
before the handler runs. Features retain consent, ownership and target validation.
All code below belongs in the plugin's existing service registration method.

```csharp
services.AddQuasarRead<ReportRequest>(context.PluginId, "report.read", [QuasarPolicyNames.CanView],
    async (caller, request, token) =>
    {
        var reports = caller.Services.GetRequiredService<ReportService>();
        var report = await reports.FindOwnedAsync(caller.User, request.Id, token);
        return new QuasarRead(open => reports.OpenAsync(report, open), "application/octet-stream",
            FileName: report.Name, Length: report.Length, EnableRanges: true,
            ValidateAsync: check => reports.VerifyOwnershipAsync(caller.User, report, check));
    });

services.AddQuasarUpload<ImportRequest, ImportResult>(context.PluginId, "report.import",
    [QuasarPolicyNames.CanEditConfigs], maximumBytes: 8 * 1024 * 1024,
    async (caller, request, staged, token) =>
    {
        var reports = caller.Services.GetRequiredService<ReportService>();
        // staged is complete, private, bounded and rewound. No feature write yet.
        var validated = await reports.ParseAndValidateAsync(staged, token);
        return new QuasarUpload<ImportResult>(async commit =>
        {
            await reports.VerifyImportTargetAsync(caller.User, request, commit);
            return await reports.ApplyAtomicallyAsync(request, validated, commit);
        });
    });

services.AddQuasarSubscription<WatchRequest, StatusResponse>(context.PluginId, "status.watch",
    [QuasarPolicyNames.CanView], (caller, request, token) =>
        caller.Services.GetRequiredService<PluginStatusService>().ObserveAsync(request, token));
```

These example feature types/services are plugin-defined. The SDK rechecks current
authorization immediately before upload commit. It deletes partial staging on
failure/cancellation and removes successful staging on disposal. A feature must
apply atomically and honor cancellation; once committed, a disconnect does not
imply rollback. `caller.EnsureAuthorizedAsync(token)` is also available for a fresh
check before JSON-handler side effects. Ongoing reads use `ValidateAsync` for
feature checks; subscriptions must honor cancellation during enumeration.

```javascript
const report = await client.openRead("example.plugin", "report.read", { id }, { signal });
try { await report.body.pipeTo(destination, { signal }); }
finally { await report.dispose(); }

const upload = await client.openWrite("example.plugin", "report.import", { targetId }, { signal });
try { const result = await upload.write(file); showResult(result); }
finally { await upload.dispose(); }

for await (const item of client.subscribe("example.plugin", "status.watch", {}, { signal })) {
    renderStatus(item);
}
// Breaking the loop, aborting or disposing cancels the owned subscription.
await client.download("example.plugin", "report.read", { id }, { fileName: "report.bin", signal });
```

Reads use native Web Streams; uploads accept native Blob/File or supported request
streams. Downloads use an SDK-owned native browser URL, avoiding a full JS buffer.
HTTP resources are single-use and bound to the verified browser session. Four
resources per session, 16 per user and 64 per worker limit concurrency; unclaimed
leases expire after one minute and active resources after ten minutes. Queues and
buffers stay bounded. A policy/role change, session expiry, logout, feature
revocation or cancellation ends active work. The owner must dispose its handle.

`QuasarSdkError.status` includes 400 invalid input/CSRF, 401 unauthenticated/expired,
403 denied/disabled, 404 missing or foreign resource, 409 already claimed,
413 input/upload too large, 416 unsupported range, 426 browser protocol mismatch,
429 capacity and 504 cooperative JSON deadline. Features can supply other explicit
statuses through `QuasarOperationException`. After a write timeout/disconnect,
check feature state before retrying; mutations have no automatic replay.

The 64 KiB limit applies to request control JSON and individual subscription
items. JSON responses such as scenes/analytics may be larger; HTTP serializes
them as response bodies. A peer adapter must frame/chunk large responses while
bounding outstanding bytes, rather than rejecting existing feature responses.

## Administrator authorization settings

On **Security**, **Plugin endpoint authorization** lists registered operations,
their declared policies, effective policies and installation overrides.
Administrators with current `CanManageSecurity` permission can disable an
operation or replace its required policies. All selected policies must pass.
**Restore declared policies** removes the operation binding override; shared
policy role overrides remain in effect.

**Shared policy role grants** changes which Quasar roles satisfy a role-based
policy. It affects every endpoint using that policy, including ordinary Quasar
routes. Empty role grants deny that policy. The editor rejects unknown policies
and roles and prevents removing `admin` from `CanManageSecurity`. Scope, ownership
and other non-role policy requirements are retained. Policies consisting only
of scope/resource assertions cannot be turned into role grants through this UI.

Settings use the existing `rbac.json`. For example:

```json
{
  "policyOverrides": {
    "CanView": ["viewer", "editor", "admin"]
  },
  "operationOverrides": {
    "cometworks.entityviewer/assets.roots.read": {
      "enabled": true,
      "requiredPolicies": ["CanManageSecurity"]
    }
  }
}
```

These fields belong alongside existing subject/claim mappings; replacing the
whole file with this example would remove those mappings. Normal application
edits use the catalog's atomic update gate and retain its last-administrator
safeguard. Successful edits are logged with the actor and changed rule.

Runtime policy evaluation now reads `policyOverrides`; earlier builds stored
that field without applying it. Review any existing overrides before upgrading,
especially `CanManageSecurity`. Authorization policies are not cached across
changes. Current caller roles are refreshed before dispatch and settings writes,
so stale UI state cannot grant a forbidden change.

Invalid saved operation bindings deny access with a diagnostic. A malformed RBAC
file disables SDK invocation and SDK rule editing until an operator restores a
valid file. The SDK never repairs corruption by silently resetting its rules to
more permissive defaults. Existing local trust is provided only by Quasar's core
auth pipeline, and hosted requests do not receive a local-trust bypass.

## Compatibility and validation

Protocol version 1 is checked by the browser session exchange. New plugins using
these APIs require a matching Quasar contract assembly and runtime; the updated
viewer 0.1.13 requires Quasar 1.6.0 or later. Existing plugins retain their current registration and HTTP routes.
The viewer uses SDK v1 for all 15 feature operations.

`Quasar.Tests/QuasarSdkTests.cs` exercises the real HTTP dispatcher, authorization,
antiforgery, limits, settings and legacy aliases in an isolated TestServer.
`node --test Quasar.Tests/browser-sdk.test.mjs` checks browser URL, credential,
version, cancellation and error behavior. The viewer's isolated streaming/replay
fixtures use the real core SDK runtime and verify plugin handlers; real Quasar/Steam
and hosted browser acceptance still require matching packages in an authorized
isolated environment. Do not launch a user's Quasar service for these checks.

## Plugin capability and discovery

Declare `"browserSdkVersion": 1` in `quasar-plugin.json`. Omitted/zero preserves
legacy plugins; a future unsupported version is rejected before assembly load.
Also set `quasarVersion` to the first compatible released Quasar build before
publication. An old host cannot enforce a field it does not know.

`endpoints.MapQuasarSdkClient(pluginPrefix + "/_sdk/client.js")` supplies a
protected module exporting `client`, initialized with the actual panel base.
A plugin module can import `./_sdk/client.js` without owning URL-prefix discovery,
authentication or transport selection. Validate `client.version === 1` and show
an explicit update error if discovery/capabilities fail; never silently fall back
to legacy fetch/auth code. Apply this mapping to each supported plugin prefix.

Quasar coordinates four active transfers and at most 128 queued reservations per
panel across same-origin frames. Control operations do not wait behind transfers.
Only an explicit HTTP 429 during resource reservation is retried, at most 40 times
250 ms apart; no feature handler has run at that point. Upload/JSON mutations are
never automatically replayed. Browser-native downloads have no completion event,
so server limits remain authoritative. Session cookie initialization is coalesced
across concurrent clients; cancellation releases queued and active work.

The viewer is the complete plugin example: all 15 operations share registrations,
legacy aliases, authorization settings and the same browser SDK. Its standalone
PreviewHost explicitly supplies a UI-only adapter; it grants no live credentials.

## Built-in adoption and remaining consumers

`QuasarCoreOperations` registers `quasar.core/analytics.series.read` and
`logs.server.read`/`logs.magnetar.read` with `CanView`, plus `logs.discord.read`
with `CanManageDiscord`. Analytics/Fleet charts and server-console downloads use
the same browser client. Existing analytics and log URLs are dispatcher aliases;
log selection keeps its existing server lookup, safe-filename and log-pattern
rules. Security lists these core operations alongside plugin operations.

Normal local authentication/trusted-network behavior continues through Quasar's
existing principal pipeline, without Nebula services or a hosting entitlement.
SDK endpoints always require an accepted authenticated principal, including when
other legacy routes have authentication explicitly disabled. Disabling hosting
has no effect on the local SDK. No operation adds a Nebula endpoint allowlist.

Other core HTTP APIs, config-backup downloads/uploads, appearance resources,
server lifecycle APIs and existing SignalR circuits retain their current paths.
The Discord log button uses its legacy alias to the SDK handler. SignalR and
remaining core transport migration belong to later RTC steps. Existing plugins
can keep their old interface and endpoint registration; SDK adoption is opt-in.

Validation uses isolated core HTTP fixtures, real viewer SDK integration and
browser-module checks. Production Steam login, installed matching packages and
hosted browser acceptance remain release gates. No live service is started by
these checks, and no release version is invented here.


Quasar 1.6.0 is the first compatible release for the SDK v1 viewer candidate.
The current viewer integrates the server-bound asset-session security fix:
`scene.read`/`replay.query` return `{ data, assetSession }`, issued only from
verified feature data; browser-selected session creation is removed. See
[rollout and rollback](BrowserSdkRollout.md) before publishing the catalog pin.
