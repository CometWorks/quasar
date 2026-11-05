# Blazor over the peer transport

The RTC branch supplies a server-side Blazor adapter using public ASP.NET Core
HTTP features and SignalR's public client ITransport interface. Quasar still
owns authentication, circuits, rendering, JS interop and reconnection. Pion owns
ICE/DTLS/SCTP. There is no global WebSocket patch or localhost proxy.

## Server integration

A protected `/_quasar/sdk/v1/circuit/negotiate` endpoint requires the existing
CanView policy, antiforgery and a same-origin request when Origin is present.
It invokes the fixed framework negotiation endpoint in process and retains its
opaque token under the exact stored browser binding. Admission is four pending
circuits/session, 16/user and 64/worker, expiring after 30 seconds. Tokens are
single-use; another session for the same user cannot substitute its binding.
The complete HTTP application and its endpoint-specific policies remain intact.

The peer's `circuit` command claims that token. A framework HttpContext uses the
stored authenticated principal, current roles, original Origin/authority and
panel PathBase. The fixed Blazor endpoint's authorization metadata is evaluated
before dispatch. `IHttpWebSocketFeature` supplies a managed WebSocket whose input
and output are the existing bounded peer streams. No ComponentHub internals,
reflection or feature URL routing is used in production.

The framework uses zero-byte receives as readiness probes, so these await data
without treating an empty destination buffer as EOF. Complete WebSocket message
boundaries are preserved separately from binary chunks. The client assembles at
most **8 MiB per framework message** and 64 pending boundaries; ordinary peer
credit and send queues remain bounded to 256 KiB. Core framework receive limits
remain in force. This cap is specific to circuit messages, not SDK JSON responses.

Logout, ticket expiry, hosted lease expiry and disconnect cancel the streams.
An RBAC configuration change closes the circuit peer so framework reconnection
receives current roles, including a downgrade that retains CanView. The worker's
hosted stop hook awaits peer/application cleanup before disposing services.
Already committed actions cannot be undone; the adapter never replays clicks.

## Browser integration and compatibility

The root app disables automatic framework startup and loads the Quasar-owned
boot module. Verified Nebula-hosted pages automatically choose the adapter with
no separate user toggle. Local pages use normal Blazor startup without a Nebula
or peer dependency. Framework negotiation and document/module loads still use
HTTPS; interactive events, render batches, JS interop and circuit file uploads
use the peer. The public SignalR HttpClient hook waits for peer/ICE setup before
requesting the framework negotiation token, including reconnects. This avoids
allocating a short-lived framework token while Internet/TURN setup is pending.
The framework still owns negotiation and its reconnect protocol. SignalR retains its own circuit reconnect behavior.

Core analytics, console logs and backup downloads use the public SDK. Backup
aliases keep their admin policy, exact bytes and ranges, while the current UI
uses SDK streaming downloads. Other browser-native navigations and resources,
Steam callbacks and health/version recovery probes remain HTTP. Machine APIs
retain their own socket/credential/Origin policy.

The implementation is verified against **ASP.NET Core 10.0.12**. A framework
upgrade must rerun the real circuit fixture; the use of public extension points
does not imply an untested Microsoft-provided WebRTC server transport.

## Evidence and remaining gates

`tests/Quasar.Rtc.CircuitChecks` starts an isolated real Razor Components app with
the actual SDK boot module, cookie/session runtime and native peer. Chromium and
Firefox pass authenticated identity, clicks, JS interop, a 200,000-character
render, a 250,000-byte InputFile upload with exact SHA-256, disconnect/reconnect
with state retained, current roles after RBAC downgrade, CSRF/Origin denial,
cross-session token refusal and replay refusal. Neither browser opens a WebSocket.
The fixture never starts the user's Quasar or game service.

Run with the released Magnetar SDK, built native peer and Playwright:

```bash
QUASAR_GO=/path/to/go1.27.3 python3 scripts/build-peer.py \
  --runtime linux-x64 --race --output /path/to/quasar-peer-race
MagnetarBin=/path/to/MagnetarInterim \
QUASAR_RTC_TEST_PEER=/path/to/quasar-peer-race \
PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs \
CHROME_PATH=/opt/google/chrome/chrome \
dotnet run --project tests/Quasar.Rtc.CircuitChecks
```

The Linux release archive also builds with the native binary, integrity manifest
and complete dependency notices. Native Windows execution, installed whole-panel
Steam acceptance, real game rendering and representative Internet NAT/capacity
acceptance remain release gates. Isolated real Pion TURN/UDP and TURN/TCP checks
exercise the same circuit fixture through production Nebula lease routes. This is not a deployment or whole-panel capacity
measurement.

## Source references

- [ASP.NET IHttpWebSocketFeature](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.features.ihttpwebsocketfeature?view=aspnetcore-10.0)
- [Microsoft's OWIN WebSocket adapter precedent](https://source.dot.net/Microsoft.AspNetCore.Owin/WebSockets/OwinWebSocketAcceptAdapter.cs.html)
- [Blazor configureSignalR hook](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Components/Web.JS/src/Platform/Circuits/CircuitStartOptions.ts)
- [SignalR ITransport selection](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/SignalR/clients/ts/signalr/src/HttpConnection.ts)

The reviewed third-party WebRTC projects primarily cover media/signaling, not a
ready-made transport for framework-owned Blazor Server circuits. The existing
Pion endpoint plus the public ASP.NET boundary avoids replacing either stack.
