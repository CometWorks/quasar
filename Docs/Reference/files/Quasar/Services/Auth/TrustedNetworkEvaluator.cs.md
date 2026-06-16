# Quasar/Services/Auth/TrustedNetworkEvaluator.cs

**Module:** Quasar.Services.Auth  **Kind:** class  **Tier:** 2

## Summary
Determines whether an incoming HTTP request originates from a trusted network address (loopback or same local subnet), enabling the trusted-network authentication bypass. Requests carrying proxy/forwarding headers are rejected for bypass unless ASP.NET's forwarded-header middleware has accepted them from a known proxy first. Used during authentication middleware to decide whether to issue a trusted-network principal without requiring a Steam login.

## Structure
Namespace: `Quasar.Services.Auth`

`sealed class TrustedNetworkEvaluator`

Constructor: `(QuasarAuthOptions options)`

Public members:
- `IsTrusted(HttpContext context) : bool` — refuses unaccepted forwarding headers, extracts `RemoteIpAddress`, maps IPv4-in-IPv6 to plain IPv4, then checks loopback (if `AllowLoopback`) and local-subnet membership (if `AllowSameSubnet`)

Private helpers:
- `HasForwardingHeader(HttpRequest request)` / `HasHeader` — detect `Forwarded`, `X-Forwarded-*`, and `X-Real-IP`; `X-Original-For` must be present for bypass when forwarding headers were supplied, indicating accepted forwarded-header middleware processing
- `IsOnLocalSubnet(IPAddress remoteIp)` — enumerates all up, non-loopback, non-tunnel IPv4 unicast addresses via `NetworkInterface` and tests each subnet mask
- `GetLocalIPv4UnicastAddresses()` — yields `UnicastIPAddressInformation` for qualifying interfaces
- `IsSameSubnet(IPAddress remoteIp, IPAddress localIp, IPAddress mask)` — byte-level bitwise AND comparison

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthOptions.cs`](QuasarAuthOptions.cs.md) — `QuasarAuthOptions`, `TrustedNetworkBypassOptions`
- `System.Net`, `System.Net.NetworkInformation`, `System.Net.Sockets` (BCL)

## Notes
`IsOnLocalSubnet` calls `NetworkInterface.GetAllNetworkInterfaces()` on every evaluation; no caching. This is intentional (interfaces can change) but means a call per request when `AllowSameSubnet` is true. Only IPv4 is supported for subnet checks; IPv6 non-loopback addresses are not matched.
