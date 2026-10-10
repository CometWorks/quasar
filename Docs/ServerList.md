# Server directory uplink

Quasar's optional outbound uplink supplies CometWorks/server-list with public
server statistics and a private description-management roster. It is disabled
by default. No production endpoint or credential is bundled.

**Rollout dependency:** the directory is currently a design prototype. The
diagnostics uplink is separate work in progress. Production activation requires
both that uplink and the directory endpoints below, including their independent
diagnostics verification. This change does not implement those backend endpoints
or claim that a deployment is unmodified.

## Consent and eligibility

Listings require all of the following:

- The existing `ConsentGranted` decision is explicitly true.
- Consent policy version 2 explicitly grants diagnostic reports, with a valid
  generation and UTC cutoff. Legacy statistics YES never grants this permission.
- CometWorks can retrieve recent logging/error-reporting evidence for this
  enrolled diagnostics installation and generation. The **directory backend**
  verifies this through its trusted connection to the logging backoffice.
- At least one explicitly mapped Steam administrator exists in Quasar's RBAC.
- The listing has been deliberately configured with a public name and join
  address, and the directory grants a short publication lease.

The Dashboard and Security YES/NO actions now disclose and save statistics and
diagnostic consent together. YES does not enable memory dumps. A legacy YES is
prompted again before diagnostic sharing. The statistics-only catalog overload
preserves diagnostic/dump choices for existing programmatic callers.

Logs may contain private player/server information. Public listing data is
limited to the chosen name and join address, observed time, running state,
player/max-player counts and simulation speed. Steam admin subjects travel only
in the private authenticated management envelope. No raw snapshots, chat, player
identities, internal hostnames, secrets or diagnostic content enter the public
feed. Diagnostics has its own private storage and retention policy.

NO cancels active publication, wakes the sender and requests withdrawal without
waiting for a Magnetar restart. External consent edits and RBAC changes also
invalidate active publication. A disconnected instance can remain visible only
until its last backend lease expires, **at most two minutes**. Instant remote
withdrawal is impossible during a network outage. No publication retries retain
old statistics; each cycle collects fresh data after eligibility verification.

## Configuration

Add an explicitly reviewed `Quasar:ServerList` section to service configuration:

```json
{
  "Quasar": {
    "ServerList": {
      "Enabled": true,
      "DirectoryUrl": "https://<directory-host>/",
      "Listings": [
        {
          "ListingId": "<directory-issued-lowercase-guid-without-hyphens>",
          "Kind": "server",
          "UniqueName": "local-server-name",
          "Name": "Public server name",
          "PublicHost": "play.example.org",
          "PublicPort": 27016
        }
      ]
    }
  }
}
```

Supply `Quasar__ServerList__InstallationToken` through secret configuration. It
must be directory-issued and bound to this installation and its assigned listing
IDs; a listing token must not be a Quasar admin or diagnostics upload token.
HTTPS is required. Credentials, queries and fragments in the URL are rejected;
requests do not follow redirects. Configuration is read at startup. Set
`Enabled=false` to stop renewing; the directory expires existing leases. To hide
immediately while online, withdraw consent first or use the directory's own
administration controls. Keep Quasar's management port private.

The diagnostics uplink owns `Diagnostics/installation-id` under Quasar's data
directory. The listing sender waits for that identity instead of inventing one.
Preserve it and `ServerList/epoch` across upgrades. A process lock prevents two
listing publishers from using the same local state. Restart reserves a higher
durable epoch before network activity; each attempt advances its sequence.
If local state is restored backwards, the backend refuses it until re-enrollment.

Configure `Kind=cluster` for a cluster's unique name and its **public Steam
gateway address**, never its administrative GatewayUrl. The gateway's authoritative
connected-client count is published once per cluster. Node counts are not summed.
Cluster max-player count and simulation speed remain unknown. Standalone metrics
require one connected agent and an observation at most 60 seconds old. Missing,
ambiguous, future-dated or stale observations remain unknown rather than appearing
as live zero-player servers. Deleted sources are omitted from the replacement set.

## Directory protocol v1 (required backend behavior)

All routes authenticate `Authorization: Bearer <installation-token>` and
`X-Installation-Id`. Validate the token-to-installation binding and each assigned
listing ID. Do not expose these machine routes to browser sessions.

1. `GET /v1/quasar/challenge`, with `X-Diagnostics-Generation`, returns
   `{ "nonce": "<32–256-character-random-value>", "expiresAtUtc": "<UTC>",
   "diagnosticsGeneration": 1 }` only after independent evidence verification.
   Expiry is at most 30 seconds from backend issuance. Bind the single-use nonce
   to the installation, diagnostics generation and verified evidence. A client
   health boolean, successful key fetch, or a quiet error queue is insufficient.
   Verification must use fresh, decryptable log evidence or a backend-initiated
   log-retrieval probe; do not require a new crash to demonstrate availability.
   These probes/receipts are not yet implemented in the diagnostics backoffice.
2. `PUT /v1/quasar/publication` receives `ServerListPublication`: schema version,
   installation ID, epoch, sequence, diagnostic generation, consent decision time,
   challenge, private admins and the complete public listings set. Atomically
   consume the nonce, verify evidence still matches, and replace both the listing
   set and admin roster. Use **server time** for a lease of at most two minutes.
   Renewals arrive approximately every 30 seconds. Reject expired/used challenges,
   earlier epochs and non-increasing sequences. An identical replay must never
   extend visibility. Persist ordering state across backend restarts.
3. `POST /v1/quasar/withdraw` receives `ServerListWithdrawal`: schema version,
   installation ID, epoch, sequence. No diagnostic permission or challenge is
   required to withdraw. Atomically tombstone publication and admin authority,
   rejecting older delayed updates. Return success for an already withdrawn
   instance without renewing any lease.

Every public access, including detail routes, join actions, search, feeds,
aggregates and caches, checks the live lease and diagnostic eligibility. Loss of
verification hides the listing. Private description drafts need not be deleted
on withdrawal; document their retention separately.

Description edits require directory Steam sign-in whose subject is in the latest
unexpired roster for the **linked installation**, and active listing eligibility.
No Quasar `editor`, trusted-network identity or service principal inherits this
authority. RBAC changes wake the sender; network failure bounds stale authority
by the same two-minute lease. Check each write, including instance ownership and
normal CSRF/content/concurrency controls. This first integration supports explicit
Steam mappings only: OIDC issuer/subject and claim-based mappings require an
authenticated authority exchange before they can grant directory permissions.

## Verification

`ServerListPublisherTests` covers consent migration, master/diagnostic conjunction,
external invalid settings, invalid/unavailable eligibility verification, withdrawal
during collection and active sends, role removal, restart ordering, public payload
exclusion, stale/ambiguous observations and endpoint restrictions. Tests exercise
the real sender with an HTTP handler; they do not launch Quasar or prove the
unimplemented directory's lease and visibility enforcement.
