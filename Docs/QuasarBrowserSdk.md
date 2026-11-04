# Quasar browser SDK

The first HTTP SDK slice provides typed plugin operations, a shared authorized
dispatcher, runtime endpoint settings and a browser JSON client. Entity Viewer
uses it for asset-status and Content-root settings reads, including both legacy
HTTP aliases. Binary streaming, subscriptions, the remaining viewer requests and
WebRTC are not implemented by this slice.

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

## Browser requests

Import `/_quasar/sdk/v1/client.js` relative to the panel base. The host supplies
that base explicitly; inside a viewer iframe its own document URL is the plugin
asset directory, not necessarily the panel base.

```javascript
import { createClient } from "/my-server-panel/_quasar/sdk/v1/client.js";

const client = createClient({ baseUrl: "https://dash.se-servers.net/my-server-panel/" });
const status = await client.request("example.plugin", "status.read", {}, { signal });
```

The concrete URL above illustrates an enrolled panel. Derive both module and
base URLs from the host's panel context in a real plugin rather than hardcoding a
panel name. A local installation can supply its local base URL with the same API.
Standalone preview hosts must supply a compatible adapter or fixture; they do
not obtain a Quasar session by importing this module.

The client obtains a request-verification token from the authenticated
`/_quasar/sdk/v1/session` endpoint and posts JSON to
`/_quasar/sdk/v1/operations/<plugin-id>/<operation-id>`. It preserves the panel
prefix, sends same-origin credentials, propagates cancellation and refuses
redirects. It does not retry operations. `QuasarSdkError.status` distinguishes
HTTP errors; aborted requests retain the browser's cancellation error.

Control JSON is limited to 64 KiB and depth 32, with a 30-second handler deadline.
The handler must honor cancellation before side effects. A timed-out write can
have an unknown result, so callers must not blindly replay it. Responses are
private no-store. Use the future streaming API for large binary data rather than
base64-encoding bytes into control JSON.

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
viewer must not be released until its manifest names the first compatible Quasar
release. Existing plugins retain their current registration and HTTP routes.
The browser-wide viewer migration remains a later SDK milestone.

`Quasar.Tests/QuasarSdkTests.cs` exercises the real HTTP dispatcher, authorization,
antiforgery, limits, settings and legacy aliases in an isolated TestServer.
`node --test Quasar.Tests/browser-sdk.test.mjs` checks browser URL, credential,
version, cancellation and error behavior. The viewer's isolated streaming fixture
supplies the dispatcher boundary and verifies plugin handlers; real Quasar/Steam
and hosted browser acceptance still require matching packages in an authorized
isolated environment. Do not launch a user's Quasar service for these checks.
