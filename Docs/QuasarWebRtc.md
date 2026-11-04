# Quasar direct transport

**Development status: authenticated peer setup and hosting leases work in isolated
Linux browser checks. The SDK DataChannel application adapter, release packaging
and native Windows acceptance are pending.** Ordinary HTTP/WebSocket operation,
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
Native/browser metadata cannot select a different core binding. The next adapter
must use the [SDK dispatcher and resource lifecycle](QuasarSdkTransport.md), with
authorization on every operation and its active resources.

The private child HTTP control listener binds only `127.0.0.1` on an ephemeral
port and requires a fresh worker-only token. Its fixed routes accept peer setup,
status, pulse and teardown; it is not an application URL proxy. The child inherits
no Quasar configuration environment or browser cookies. Installation credentials,
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
also enforce its own current policies. A committed mutation is not undone by a
disconnect; its outcome may be unknown and must not be blindly replayed.

Local requests use the existing core authentication and trusted-network rules
without obtaining a Nebula lease. They keep working when Nebula is unavailable.
Hosted sessions receive no trusted-network principal or private-machine API
shortcut. No Origin header is stripped, and DIS-0042 remains in force.

## Development configuration

`Quasar:WebRtc:Enabled` defaults to `false`. A developer can build the pinned peer
with Go 1.27.3 and place it in `Peer/quasar-peer` (`Peer/quasar-peer.exe` on Windows)
beside the worker, or set `Quasar:WebRtc:PeerExecutable` to its explicit path.
Missing components report unavailable; no plugin/browser starts Quasar and no
runtime download bypasses release integrity checks. Native release staging and
rollback verification are separate pending packaging work.

The isolated real-browser check uses `QUASAR_RTC_TEST_PEER` pointing to that built
executable, `PLAYWRIGHT_MODULE` pointing to Playwright 1.61.1 and an optional
`CHROME_PATH`. Run `Quasar.Tests.QuasarRtcTests` through the existing xUnit project.
It starts a separate loopback fixture with real core cookies, antiforgery, SDK
sessions, policies and the native peer. It does not launch a user Quasar service.
Its fake hosting provider and test-only `FakeTimeProvider` prove lease expiry
without waiting two minutes. Nebula's separate real-process checks exercise the
actual hosting API and retained frp/Caddy transport.

Linux Chromium checks pass authentication/CSRF, exact-session ownership, nonce
and grant replay, pending expiry, permission disablement, view closure, logout,
manager outage and installation replacement, including a native Go race build.
Windows cross-compilation works; native Windows/browser acceptance, real Steam,
installed packages and actual Viewer/core DataChannel traffic remain release gates.

No TURN server or automatic bulk HTTP fallback is configured. The endpoint reports
direct, relayed or unavailable from actual selected ICE candidate types, and
refuses relayed activation in this direct-only development mode. Existing
compatibility HTTP traffic still crosses Nebula; it is not accounted away by
successful peer setup.
