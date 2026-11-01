# Hosted public access with Nebula

Nebula provides a public URL for the complete existing Quasar application:
`https://dash.se-servers.net/my-server-panel/`. Servers, worlds, supervision
and credentials stay on the Quasar machine. One shared public hostname serves all
panels; customers do not configure subdomains, router forwarding or certificates.

This implementation is under integration. **Stripe billing is
implemented with checkout disabled by default pending operator onboarding.**
Configured Nebula platform super admins can enroll free; ordinary accounts remain
pending until checkout is enabled and payment is independently verified.
Live deployment and provider verification remain required before customer launch.
The operator accepts shared-origin plugin risk and uses retrospective enforcement
without customer preapproval.

## Subscription

The confirmed price is **€2.99 per month in EUR, including applicable customer
taxes**, with processing fees absorbed. Exactly one customer corresponds to one
panel/tunnel and one subscription; combined panel billing is not offered.
The existing yearly plan is €29.99/year in EUR, including applicable taxes.
Choose the billing period on Nebula and review the recurring total before payment.
The hosted setup dialog explains before checkout:

> Your subscription provides hosted access to your Quasar panel and also supports ongoing development and future CometWorks projects.

This is a paid service subscription. It does not grant entitlement to unreleased
projects or promise specific future deliverables.

Qualifying private EU consumer purchases can use ordinary Stripe under Dutch KOR
while the operator's dated, whole-business eligibility approval is current. Other
supported purchases use Stripe Managed Payments (Sold through Link) as merchant of
record. Nebula shows the seller/tax treatment before checkout and binds it to the
billing account. KOR does not exempt Link's sales. Renewals beyond KOR approval
pause at Stripe; valid paid access remains. Explicit owner cancellations remain
canceled after an operator approval update. Customer-type or region changes never
automatically change the seller; contact support to review an incompatible change.
Cards, Apple Pay, Google Pay and Link are supported where available. Paid checkout
stays disabled pending runtime credentials, real payment/country controls and
existing release acceptance. See [Nebula billing setup](https://github.com/CometWorks/nebula/blob/main/docs/billing.md).

## Setup

1. Open Quasar through an existing administrator session (normally trusted local
   access for a fresh installation). If no subject or claim mapping grants the
   admin role, setup first asks for a 17-digit **Administrator SteamID64**.
   **Save administrator and continue** persists the admin mapping to `rbac.json`.
   Existing admin mappings skip this step. A remote Steam sign-in alone cannot
   claim an unconfigured panel. A platform super-admin entitlement supplies free
   hosting; it does not grant a role in local Quasar. Steam must be enabled before
   public enrollment.
2. The first interactive administrator session shows **Set up administrator
   access**. Like the server import wizard, it shows a step progress indicator and
   two large choices: **Self-deploy** and **Use hosted panel**. Select one, then
   choose **Next**. **Cancel** closes setup; **Back** returns from hosted settings
   without discarding the name you entered. Existing
   network/security settings are preserved. Headless startup does not wait for a
   browser decision. Reopen the dialog using **Security → Manage access setup**.
3. For hosted access, enter a name of 3–63 lowercase letters/digits separated by
   single dashes. No underscores, leading/trailing/doubled dashes or reserved
   portal route names. The dialog previews the public address.
4. Choose **Start hosted enrollment**, then sign into Steam on Nebula and confirm
   the installation you just configured. The larger setup dialog switches to a
   connection screen with an indeterminate progress bar, live status and stages
   for enrollment, activation, tunnel connection and dashboard verification.
   **Continue enrollment with Steam** remains available if the new tab is blocked.
   While connecting, **Cancel** is the only footer action: it stops the local
   hosted setup/connection without canceling an existing subscription or deleting
   the private identity. A name collision is reported before
   activation; return to Quasar and choose another name. The browser link expires
   after 15 minutes; Quasar notices an unclaimed expiry and permits a new attempt.
5. Quasar automatically retrieves the bound installation credential after
   activation. Run Quasar through its packaged **Bootstrap** launcher/service to
   maintain the hosted connection. Direct worker development runs do not start a
   connector. Readiness requires fresh confirmation of both frpc registration and
   local worker readiness. Enrollment alone does not confirm connectivity.
   **Open Quasar dashboard** appears as a primary button only after active hosting
   and fresh health for this panel confirm both are ready. **Done** closes setup;
   **Self-deploy** disconnects hosted access and opens Security.

There are no customer frp commands or pasted credentials. Selecting Self-deploy
later stops the connector without deleting the private identity; selecting the
same hosted name reconnects it. An enrolled public name cannot currently be
renamed through the wizard.

## Security and routing

Nebula checks hosting entitlement and selects the tunnel from the first URL
segment. Quasar retains Steam/RBAC, API/service credentials and machine endpoint
policies. A private proof added by the local connector is accepted only from a
loopback socket and for the enrolled prefix. Hosted requests never receive
trusted-loopback or same-subnet administrator bypass. Requests with Origin remain
forbidden on machine-only endpoints. Machine credentials are still required.

The prefix is applied as ASP.NET PathBase before endpoint routing and Steam
callbacks. Core navigation, styles/scripts, downloads, update reloads, cookies,
antiforgery and service-worker scopes use that base. Root-relative plugin menu
and stylesheet links are resolved through Quasar's application base. Plugins
must likewise resolve their own iframe/module/browser API URLs against the
application base or their module URL. The matching Entity Viewer changes do this.
The gateway forwards complete HTTP/WebSocket traffic without a feature allowlist.

The hosted browser origin never replaces existing Agent/Host discovery or local
control addresses. When an administrator explicitly supplies the exact hosted
HTTPS panel URL as a control address, Host enrollment/update downloads and
Host/Agent WebSockets preserve its prefix. Other arbitrary path-based control
addresses remain invalid. Bootstrap owns frpc outside web-worker updates; its upstream WSS
connection reconnects unattended. Manager outages, expiry or suspension close
hosted access without changing game-server goal state or local access.

**URL paths are not browser-origin isolation.** Cookie names/paths and server
permissions do not isolate arbitrary customer-controlled scripts from the portal
or other panels. The operator explicitly accepts this risk and chose open signup
without customer preapproval, using retrospective suspension and credential
revocation. These controls cannot undo previous script activity. See
[Nebula's operating decision](https://github.com/CometWorks/nebula/blob/main/docs/architecture.md#shared-origin-operating-decision).

## Local files and recovery

`HostedPanel/` sits under the Quasar install/data root. It contains
`enrollment.json` (installation proof, connector credential and local proxy
proof), private generated `frpc.toml`, exported public trust roots and a verified
frpc 0.71.1 download. The connector supports Linux x64 and Windows x64. Archives
come from the official upstream release and are checked against pinned SHA-256
values; the upstream Apache 2.0 license is preserved alongside the binary.
The TLS trust bundle is mandatory: frp otherwise does not verify server
certificates merely because TLS is enabled.

The directory uses owner-only permissions on Unix. Windows restricts it to the
running identity, SYSTEM and Administrators. Run Bootstrap and the worker under
the same service identity. Back up this directory as sensitive data and protect
backup archives. A copied secret restores the same identity; this is not TPM
attestation or clone prevention. Run only one restored connector per enrollment.

Unreadable/corrupt hosted state disables hosted enrollment, preserves the file
for recovery and leaves local Quasar running. Restore its private backup before
changing setup. Bootstrap retries unavailable connections; a previous process
lease is checked by PID, start time and executable path before retiring a
connector left by a launcher crash. The launcher stops its connector on shutdown.
A live Windows install, actual Steam callback, Entity Viewer session and update
cutover still require deployment acceptance testing.


## Billing and installation management

All configured Steam administrators share one Nebula panel. Quasar sends its
current Steam admin mappings through the private installation proof every ten
seconds, including while waiting for payment. Nebula expires management authority
after two minutes without a current snapshot. Browser claims and local roles do
not grant platform super-admin/free-enrollment rights. Trusted-network or other
provider sessions can run setup, but Nebula membership uses configured Steam IDs.
**Manage Nebula hosting and billing** on Security or the ready wizard opens the
account page for the signed-in Steam identity.

One payer controls the subscription, renewal and billing details. Other current
admins can manage the installation and choose **Request billing transfer to me**.
The payer reviews the durable request in Nebula, then approves or declines it.
Requests expire after seven days. Approval rechecks authorization, stops the old
subscription without refund or proration, and preserves verified paid access.
The incoming payer uses fresh checkout with their own billing details and payment
method after that paid period ends. They must complete checkout to continue
hosting at that point; no overlapping subscription or inherited card is created.
Historical invoices stay private to the old payer and refunds/disputes still
reconcile. Returning from Checkout never proves payment.

Billing requests and results appear in Quasar's notifications and use existing
Steam browser push subscriptions when enabled. Quasar must be running and the
browser must have granted notification permission. Enable push on
**Notifications** through the hosted URL to subscribe that browser under this
panel's scope; a local-origin subscription is separate. Existing subscriptions
can deliver targeted billing notices even after the payer loses local roles.
Trusted-network push subscriptions do not receive Steam billing notices.
The fixed billing link opens Nebula's separately authenticated account page.
Delivery is best-effort; review pending requests there if push is disabled.
Automated scope, icon, click, targeting and receipt checks pass, but a real browser
receiving Web Push through the live Nebula URL remains an acceptance test.

To move or reinstall Quasar, choose the **same public panel name** in the new
installation's hosted setup. Follow its Steam link and explicitly confirm
replacement. The current payer can do this even if the old deployment is offline
or the founding payer has changed; a currently verified administrator can also
replace it. Confirmation revokes the old connector, deletes previous registration
records and health, and drops the old deployment's admin roster atomically.
The public URL, subscription, payment details and paid dates stay linked to the
same panel. The new deployment publishes its own administrators. This moves
hosted registration only: game servers, worlds and local settings remain where
they were and are not deleted or migrated.

The account page can also disconnect an installation; that action does not cancel
renewal. Manage billing separately. Quasar clears revoked local enrollment
credentials so an administrator can start fresh pairing. Automatic hardware
attestation and public-name renaming are not implemented. Deploy the matching
Nebula manager before this Quasar update.

## Hosted push acceptance check

Automated tests cover the worker scope, icon/click URLs, targeted notices, receipt
persistence and generic HTTP transport. To verify real browser delivery:

1. Open `https://dash.se-servers.net/<panel-name>/notifications` with a Steam admin
   session in a browser that supports Web Push. Enable notifications and grant
   permission. Confirm the worker and subscription belong to `/<panel-name>/`.
2. In a second administrator's Steam session, open Nebula and request billing
   ownership. Within the polling/delivery interval, confirm the payer's subscribed
   browser receives **Nebula billing update**, including with the panel tab closed.
3. Click it and confirm it opens `https://dash.se-servers.net/account` and requires
   the correct Steam identity to approve. A third account cannot approve it or
   read private billing information.
4. Decline the request and check the targeted result and durable account state.
   Repeat with browser permission disabled: the request remains reviewable in
   Nebula and other Quasar notifications still use the panel prefix.

Use isolated sandbox billing for approval/payment tests. No completed provider
payment, real push delivery or live deployment is implied by automated checks.
