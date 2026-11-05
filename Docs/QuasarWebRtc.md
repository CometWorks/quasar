# Quasar direct transport

**Development status: the real SDK adapter, streams and browser downloads pass
isolated Linux Chromium/Firefox checks. The Blazor bridge and Linux release
archive are also verified; installed whole-panel and native Windows execution
remain pending.** Ordinary HTTP/WebSocket operation,
Blazor, local supervision and the hosted connector remain available as before.
Do not enable this development component as a production bandwidth solution.

## Ownership and security boundary

The Quasar application worker owns a single native `Quasar.Peer` child using
Pion WebRTC 4.2.26, with pinned Go/module checksums. Pion implements ICE, DTLS and
SCTP. The child has no Steam identity or permissions. Quasar stores the exact
core `QuasarBrowserSession` under its own random peer ID and enforces current
`CanView` permission. It never accepts a principal or role from peer input.

Setup uses `POST /_quasar/sdk/v1/rtc/attempts` through the existing full HTTP path,
including the enrolled prefix. Existing Quasar authentication, `CanView` and
antiforgery validation precede session binding. The request carries protocol 1,
a fresh 256-bit nonce and a data-only SDP offer with one SHA-256 DTLS identity.
The worker returns an answer and a one-use 30-second setup grant. After the
channels open, the same browser binding confirms the grant through the protected
HTTP path. A different browser session, even for the same user, cannot claim it.
Nonce tombstones last two minutes. Admission limits are four peers per browser
session, 16 per user and 64 per worker, plus 16 setup attempts per session and
64 per user in that two-minute window.

Only reliable ordered `quasar.control.v1` and `quasar.bulk.v1` channels using the
`quasar-sdk.v1` protocol are accepted. DTLS verifies the browser fingerprint from
the authenticated offer; the browser verifies the answer received over HTTPS.
Native/browser metadata cannot select a different core binding. The adapter
uses the [SDK dispatcher and resource lifecycle](QuasarSdkTransport.md), with
authorization on every operation and its active resources.

The private child HTTP control listener binds only `127.0.0.1` on an ephemeral
port and requires a fresh worker-only token. Its fixed routes accept peer setup,
status, pulse and teardown; it is not an application URL proxy. Pion candidate logging is suppressed, so it cannot disclose addresses or fill
the readiness pipe. The child inherits no Quasar configuration environment or
browser cookies. Installation credentials,
portal cookies and data-protection keys are not sent to it or to JavaScript.
Closing the owner's stdin retires the child after normal shutdown or owner death.
A missing worker pulse closes a peer after five seconds, with a half-second reap
interval; an expired peer cannot be revived by a delayed pulse.

## Hosted authorization

A request established through the existing private hosted-hop proof additionally
requires the exact enrolled prefix and a core-authenticated Steam session. The
worker uses its current enrollment credential to obtain a Nebula setup grant
bound to hashes of the session, worker attempt and both DTLS identities. These
hashes confer no human role or platform super-admin rights.

Nebula grants last 30 seconds until one-use activation. Active leases last
120 seconds and renew every 30 seconds. Every create, activation and renewal
rechecks current credential and hosting entitlement. Credential rotation or
replacement prevents renewal and cannot inherit the old peer's lease. A manager
restart loses ephemeral leases and refuses their renewal. Manager errors leave
only already-valid time; they never mint local access or extend a cached lease.

The worker measures a lease from the start of the verified HTTPS request using
monotonic time, including the five-second request deadline in that interval.
A separate timer cancels on expiry even if a renewal awaits the network. Under
normal process scheduling, supported peer forwarding stops within **130 seconds**
of the last successful renewal request starting, including the native watchdog's
cleanup margin. This is below the existing five-minute compatibility ceiling.
Wall-clock changes cannot extend that monotonic lease. Paid-entitlement dates
still use Nebula's authoritative UTC clock and retain its existing clock/trust
assumptions. No hard real-time scheduling guarantee or instantaneous rollback of
an already committed action is implied.

Core session expiry/logout and a shorter verified ticket rebinding revoke the
stored session token. Closing a view cancels its peer, leaving other views'
binding intact. Current panel permission and local installation state are checked
at one-second intervals, including while idle. Each SDK operation/resource must
also enforce its own current policies. A transient ICE disconnected state has a five-second grace period on the
same reliable association. Failed/closed remains terminal; no new attempt or
feature replay runs during that grace. A committed mutation is not undone by a
disconnect; its outcome may be unknown and must not be blindly replayed.

Local requests use the existing core authentication and trusted-network rules
without obtaining a Nebula lease. They keep working when Nebula is unavailable.
Hosted sessions receive no trusted-network principal or private-machine API
shortcut. No Origin header is stripped, and DIS-0042 remains in force.

## Development configuration

Choosing Nebula automatically selects WebRTC for SDK traffic; there is no separate
user toggle. Local sessions continue using HTTP independently. Initial direct failure makes one bounded TURN-only setup attempt before
application work. Unavailable connectivity fails without replay or bulk HTTP fallback.
This branch is separate from the shipped SDK-first HTTP release.

A developer can build the pinned peer with Go 1.27.3 and place it in `Peer/quasar-peer` (`Peer/quasar-peer.exe` on Windows)
beside the worker, or set `Quasar:WebRtc:PeerExecutable` to its explicit path.
Missing components report unavailable; no plugin/browser starts Quasar and no
runtime download bypasses release integrity checks. Release staging supplies a checked SHA-256/runtime manifest and dependency
notices. Older complete HTTP archives remain rollback targets.

The isolated real-browser check uses `QUASAR_RTC_TEST_PEER` pointing to that built
executable, `PLAYWRIGHT_MODULE` pointing to Playwright 1.61.1 and an optional
`CHROME_PATH`. Run `Quasar.Tests.QuasarRtcTests` through the existing xUnit project.
It starts a separate loopback fixture with real core cookies, antiforgery, SDK
sessions, policies and the native peer. It does not launch a user Quasar service.
Its fake hosting provider and test-only `FakeTimeProvider` prove lease expiry
without waiting two minutes. Nebula's separate real-process checks exercise the
actual hosting API and retained frp/Caddy transport.

The browser fixtures normally omit STUN to run on isolated loopback networks.
Set `QUASAR_RTC_TEST_STUN=stun:rtc.se-servers.net:3478` (or a test STUN endpoint)
for `Quasar.Tests.QuasarRtcTests` and `tests/Quasar.Rtc.CircuitChecks` to also
exercise configured discovery through the real worker/native peer boundary.
This mode requires that endpoint to be reachable. ICE JSON omits absent STUN
credentials: Pion rejects a present null username before SDP negotiation, which
otherwise prevents hosted Blazor hydration even when both peers can connect
locally. The HTTP JSON contract check also verifies TURN credentials remain intact.

Linux Chromium checks pass authentication/CSRF, exact-session ownership, nonce
and grant replay, pending expiry, permission disablement, view closure, logout,
manager outage and installation replacement, including a native Go race build.
Windows cross-compilation works; native Windows/browser acceptance, real Steam,
installed packages and actual Viewer/core acceptance remain release gates.

Nebula self-hosts STUN discovery and a Pion TURN fallback. The worker may reserve
one-use, exact-session relay setup through `/_quasar/sdk/v1/rtc/relay`, receiving
independent short-lived transport credentials and a 60-second ticket. Only those
credentials reach native/browser ICE. Ticket consumption uses the worker-owned
peer ID, exact stored browser binding and current installation. Prepared tickets
share peer admission, expire, and retire on logout/replacement/worker stop.

A connected peer activates a 120-second relay lease after hosting activation;
renewal succeeds before extending the independent hosted deadline. Native relay
sockets enforce expiry and shared panel/aggregate budgets, including allocations
already open. Actual selected ICE candidates determine `direct`, `relayed` or
`unavailable`; a relay path without its authorized grant is refused. No established
stream or mutation is replayed. There is no automatic bulk HTTP fallback.
The operator relay requires an explicit reviewed budget. See
[Nebula's relay contract](https://github.com/CometWorks/nebula/blob/main/docs/webrtc-relay.md). Existing
compatibility HTTP traffic still crosses Nebula; it is not accounted away by
successful peer setup.

## SDK protocol 1

The browser retains the public SDK API. Hosted clients lazily establish one peer
per client and close their owned resources on disposal, navigation or bfcache
suspension. Restoring a page starts new work; it never replays a mutation.

Control envelopes contain `v:1`, a type and a positive uint32 request ID. IDs
increase strictly for `request`, `reserve`, `start` and `release`; duplicate IDs
close the connection. `credit`, `cancel` and `upload.end` refer to existing calls.
There are eight active calls per peer. Request payloads remain bounded to 64 KiB
and depth 32; the fixed wire envelope allows 66 KiB. Large JSON **responses** are
serialized once and promoted from the 8 KiB control prefix to the binary stream.
They are not limited to the request size.

`reserve` checks the registered operation and creates the same core resource as
HTTP. `start` claims it once; only its owning peer may start or release it.
Per-session/user/worker quotas and pending/active timeouts are shared with HTTP.
The application worker dispatches the operation under its stored authenticated
binding and current role/policy rules. It observes logout/rebinding revocation
and rechecks operation/read validation every second, including stalled streams
and large JSON responses. Cooperative cancellation cannot undo a committed write.

Binary messages contain uint32 little-endian request ID and sequence, then at
most 16 KiB of bytes. Initial pull credit is required. Absolute consumed-byte
acknowledgments bound outstanding read bytes to 256 KiB per call; native send
queues also stay at or below 256 KiB per channel. Private IPC receive queues hold
at most 128 control messages within the previous 528 KiB byte cap, or 16 bulk
messages. Cumulative read credits replenish half a window at a time so paced
TURN delivery does not flood control IPC. Overflow closes the peer. A stalled consumer
cannot grow a whole-file buffer. Reads retain metadata, exact bytes and single
seekable ranges. Subscriptions use bounded NDJSON items. Control and binary
channels are reliable and ordered independently, so EOF carries an exact byte
count and may precede the last binary message.

Uploads acknowledge a 64 KiB window and feed a bounded pipe into the existing
private delete-on-close staging helper. The upload handler receives only a
completed staged file and rechecks authority before commit. Disconnect, invalid
sequence, limit breach or incomplete EOF cancels staging. A failed or timed-out
mutation can have an unknown outcome; no transport retries it. Only an explicit
429 resource-reservation refusal may retry before any handler runs.

Browser downloads use a Quasar-owned service worker scoped only to
`_quasar/sdk/v1/download/`. A one-use random ticket connects a pull-driven
MessageChannel to the existing credited read. Attachment responses never build
a whole-file Blob and do not replace the panel/push worker. Tickets expire after
60 seconds; each pull has a 30-second deadline. The network route reports
unavailable if the worker cannot handle it, preventing a hidden HTTP data path.
The attachment navigation omits `download`, because Chromium bypasses this worker
for that attribute. Server Content-Disposition supplies the filename.

## RTC3 evidence

The normal complete test project compiles against released Magnetar 2.4.3.9.
The selected SDK/RBAC/hosted/machine/UI suite passes 177 cases with the native Go
race build. Real Chromium and Firefox each verify 32 MiB byte integrity and a
32 MiB browser-native download, 150,000-character JSON, ranges, uploads, partial
and oversized upload refusal, subscriptions, operation disablement, viewer upload
denial, duplicate IDs, invalid credit and excess depth. The read authorization
check stops an existing stream inside the 2.5-second fixture deadline.

An unread read opened 64 KiB of source data in the measured run. Small controls
took about 2–3 ms while that read remained open. Browser request observation
recorded zero feature-payload HTTP requests. These are loopback fixture results,
not Internet NAT or capacity measurements. `go vet` and the vulnerability scan
pass, with zero reachable/imported-package vulnerabilities and 11 advisories in
unused module code. The next task is real Viewer acceptance (RTC4).

RTC4's actual Viewer browser/handler fixture additionally measures a 16 MiB
archive through a real isolated Caddy boundary: 290 bytes of SDK session metadata
and zero asset-body bytes at the proxy; 16,785,936 framed bytes at the receiving
peer. Native sender counters and buffer maxima corroborate the direct path.
The SDK's read-only `transport` snapshot exposes path/byte/maximum-queue counters;
authenticated peer status exposes native byte/buffer totals without addresses.
This is feature/transport evidence; live Steam, actual game rendering, retained
history RTC replay and the complete Nebula gateway chain remain acceptance gates.

The [Blazor adapter](QuasarWebRtcFramework.md) uses the public server WebSocket
feature and SignalR client transport interfaces. Real framework render/interop,
upload and reconnect checks pass; installed whole-panel acceptance remains.


## Peer release payload

The Linux and Windows packaging scripts build with pinned Go 1.27.3 and locked
modules, include all dependency notices, and ship `Peer/quasar-peer[.exe]` plus
its SHA-256/protocol/runtime manifest. The worker checks that manifest before
execution. The complete archive checksum and existing verified launcher manifest
cover the peer. RTC releases declare `browserTransport:1`; the updater rejects
an incomplete declared payload, while older complete HTTP archives remain valid
rollback targets. Linux packaging and Windows cross-build pass locally. Native
Windows execution and installed upgrade/rollback remain acceptance gates.

NAT discovery defaults to the self-hosted `stun:rtc.se-servers.net:3478` for
both peers, without a browser setting. This hostname must be DNS only: Cloudflare's
ordinary HTTP proxy does not carry STUN/TURN UDP or raw TCP. The dashboard may
retain its HTTPS proxy. The endpoint must be deployed and UDP 3478 routed to
Nebula separately from HTTPS. Nebula's relay resolves the DDNS hostname at startup
and every minute; a verified public IPv4 change retires old relay allocations
without replaying application work. Automatic bounded TURN fallback is implemented
as described above; public deployment and Internet traversal acceptance remain
operator gates.
