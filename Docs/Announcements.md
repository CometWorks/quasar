# CometWorks announcements

Quasar shows CometWorks operational announcements in its existing notification
bell and **Notifications** page. Every authenticated viewer of this Quasar
installation can see them, including operators managing linked Hosts and cluster
nodes. This is centralized operator visibility through the existing Quasar UI;
there is no new Host protocol, inbound listener, server chat broadcast, remote
command or automatic restart. Headless mode does not poll this human-facing feed.

## Consent and authentication

Announcements require a current-policy **YES** data-handling decision and enabled
**Diagnostic reports** in Security. Crash-dump and public-listing consent are
unrelated. There is no new opt-in. Existing update/instance notification
preferences do not suppress these operational notices. Browser push still
requires the browser's existing, explicitly enabled subscription.

Quasar checks local consent before enrollment, before fetching, before committing
a response, and whenever returning or rendering a notice or starting a push.
Changing the consent decision cancels enrollment/fetch/delivery operations and
clears mismatched cached display state. A response captured before revocation
cannot become visible after regrant. UI circuits refresh through the existing
notification change event. Cancellation cannot recall a browser push already
accepted by a push provider or content a person already saw.

The backend registry cannot assert current local consent. Authentication proves
enrollment only; Quasar remains responsible for the consent gates.

Quasar uses `Quasar:Diagnostics:BackofficeUrl` (default
`https://log.se-servers.net/`) and DiagnosticsUplinkService's existing enrollment,
installation identity and upload credential. It creates no new secret. Only an
HTTPS configured origin is eligible, including for enrollment on this path; the
diagnostics-only loopback HTTP development exception does not enable
announcements. Enrollment and feed clients disable redirects. Credentials are
never forwarded to a redirect target or an announcement's external link.
Negotiated credentials rejected with 401 are reenrolled on a later poll;
configured legacy credentials must be corrected by the administrator.

## Polling and delivery limits

Polling is outbound and best effort, every five minutes with up to 30 seconds of
jitter in either direction, with a 30-second cycle deadline. Granting consent
wakes the poller. Revoking consent stops network polling until consent returns.
A successful full snapshot replaces the active set, so withdrawal removes the
notice on the next successful poll. Display stops at the exclusive announcement
expiry or six minutes after the last successful snapshot, whichever comes first.
An unavailable or malformed feed cannot extend this deadline. Open UI circuits
are notified within about one second of local expiry/staleness; every feed read
and render eligibility check also enforces the deadline.

`Announcements.json` under the Quasar application-state directory persists the
active snapshot, configured origin, consent decision/generation and successful
fetch time. After restart it is eligible only under the same current consent,
origin and deadline. This file contains announcement content, never credentials.
Announcement IDs are stable; receiving an ID does not dismiss its active display.

Browser push uses existing account authorization, subscriptions and protected
`PushNotifications.json` state. Each browser retains a durable receipt per
announcement ID, even after withdrawal, expiry or consent changes. Repeated
polls and Quasar restarts do not replay it. Receipts remain until that browser
subscription is removed (or its state is deliberately reset); a new subscription
can receive currently active notices. Announcement receipts are reserved before
the transport starts: delivery is **at most one attempt per subscribed browser
and ID**, not guaranteed delivery. An interrupted or failed attempt can be
missed, while the active notice remains in the bell and page. The normal push
sweep runs every 30 seconds and sends at most ten pending notices per browser per
sweep, prioritizing critical and warning notices.

Critical notices sort first, show an explicit **CRITICAL announcement** label and
error styling, and turn the bell red. Warning notices have warning styling.
Browser push includes up to 400 UTF-16 code units of text (plus an ellipsis when truncated), so maximum-length announcements fit the transport payload limit. The page retains the full text. Push titles include severity and open `/notifications`; external HTTPS
details require a separate human click there. Titles and text render as escaped
plain text. No markup or commands are executed.

## Wire contract and verification

`GET /v1/announcements` uses `Authorization: Bearer <existing upload credential>`
and `X-Installation-Id: <lowercase GUID N>`. The configured base path is preserved.
Only HTTP 200 is accepted. The response is bounded to 2 MiB before deserialization,
including streaming responses without Content-Length. The camelCase v1 envelope
contains `schemaVersion: 1` and `announcements`, at most 32 items. Each item has a
unique lowercase GUID N `id`, `severity` (`info`, `warning`, `critical`), `title`
(up to 160 characters), plain `text` (up to 4000), nullable absolute HTTPS `link`
(up to 2048, without credentials), and UTC ISO `publishedAtUtc` and
`expiresAtUtc`. Missing fields, duplicate IDs, malformed timestamps and unknown
schemas/fields reject the whole snapshot. Notices use newest publication first,
then ID ascending within each priority.

Implementation tracks [client ticket #1358](https://tasks.cometworks.se/tasks/1358)
and [parent #1356](https://tasks.cometworks.se/tasks/1356). Backend contract and
operator controls are in
[logging-backend PR #10](https://github.com/CometWorks/logging-backend/pull/10)
(head `87bfae5bc1b03b90b3e30b7af2a1d91b43e32da2`). That service authenticates
publish/list/withdraw operators and handles its own CSRF and durable state.

Run the focused offline regression tests with a current Magnetar SDK:

```sh
dotnet test Quasar.Tests/Quasar.Tests.csproj -m:2 --filter 'FullyQualifiedName~AnnouncementTests|FullyQualifiedName~DiagnosticsUplinkTests|FullyQualifiedName~PushNotificationTests|FullyQualifiedName~InstanceNotificationTests'
```

The tests use isolated state and fake HTTP/push transports, exercise the actual
notification feed and shared rendered bell and notice card, and require no running Quasar,
game, backend, browser push subscription or production announcement.
