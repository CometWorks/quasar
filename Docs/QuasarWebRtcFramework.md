# Blazor and remaining HTTPS traffic

RTC5 is an unresolved completion gate. The SDK operation/asset/download adapter
is working, but it does not carry the interactive Blazor circuit. Nebula-hosted
SDK clients automatically select RTC; that does not imply whole-panel offload.

## Framework evidence

Quasar targets .NET 10 and uses the InteractiveServer render mode with the
framework's automatic `blazor.web.js` startup in `Components/App.razor`.
The locally checked runtime/ref pack is ASP.NET Core 10.0.12. The matching 10.0
framework source exposes:

- [CircuitStartOptions.configureSignalR](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Components/Web.JS/src/Platform/Circuits/CircuitStartOptions.ts): a supported client HubConnectionBuilder hook.
- [HttpConnection custom ITransport selection](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/SignalR/clients/ts/signalr/src/HttpConnection.ts): a custom client transport can be supplied, but skipNegotiation specifically requires WebSockets. A client hook alone does not implement the server path.
- [HttpConnectionDispatcherOptions.Transports](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/SignalR/common/Http.Connections/src/HttpConnectionDispatcherOptions.cs): built-in HTTP transports are WebSockets, ServerSentEvents and LongPolling. No WebRTC server transport registration is exposed here.
- [ComponentHub](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Components/Server/src/ComponentHub.cs): the circuit hub is internal, so directly instantiating a replacement generic HubConnectionHandler would depend on framework internals.

These findings do not prove an adapter is impossible. They rule out treating a
custom client transport or a sample hub as a completed supported circuit adapter.
A further implementation must establish a server integration using public APIs,
with the exact core session principal, current authorization, bounded duplex
backpressure and connection teardown. It must run actual interactive components
through negotiation, render batches, JS interop, reconnect/disposal and upgrades.
No global WebSocket patch, localhost HTTP/WebSocket proxy, copied portal ticket
or reflection into ComponentHub is an accepted production shortcut.

## Current traffic inventory

- Viewer scenes, assets, SDK JSON/reads/uploads/subscriptions and browser downloads
  use the direct adapter on verified hosted sessions.
- Core analytics/Fleet requests and console downloads already use the SDK.
- Blazor render events/batches, JS interop and circuit uploads still use SignalR
  through the complete existing authenticated HTTP/WebSocket application.
- Documents, modules, CSS, fonts, images, Steam callbacks, health/version recovery
  probes, SDK session setup, peer signaling and hosting leases still use HTTPS.
  Recovery probes must remain available while a worker or peer restarts.
- Existing machine endpoints preserve their own authentication, socket peer and
  Origin checks. They are not redirected into the browser SDK.

The remaining SignalR load must be measured against an explicit operator budget
or replaced by a proved supported adapter before the full redesign can be called
delivered. Retaining it is an explicit outstanding scope decision, not a completed
RTC5 implementation. Native Windows, Internet NAT and installed Steam acceptance
remain separate gates even after a circuit adapter is established.
