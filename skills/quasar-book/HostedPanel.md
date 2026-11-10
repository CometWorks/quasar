# Hosted access

Nebula publishes the complete local Quasar at a public address such as `https://dash.se-servers.net/my-server-panel/`. Servers, worlds, supervision and credentials stay on the Quasar machine. You need no DNS entry, router port forwarding or certificate. The alternative is your own HTTPS reverse proxy, see [Access control](AccessControl.md#behind-a-reverse-proxy). This page covers availability, setup, how the connection works, what changes for security, shared administration and billing, moving a panel, and the local files.

Post-install access setup starts at `/setup` on the configured Quasar address.
Guided installers open it after installation and health checks; manual installs
can open it after starting Quasar. It uses the same administrator/access dialog
as **Security → Manage access setup**. A saved self-deploy choice completes setup
and opens Security. Hosted setup completes only after activation and fresh
matching tunnel/application health. The existing private enrollment file retains
completion across restarts and repeat installs. Pending, cancelled or failed
attempts remain resumable and may prompt again; completion stops automatic prompts.
Cancellation preserves private identity and does not cancel a paid subscription.
Existing saved choices remain compatible. Explicit reopening is always available.

## Availability and price

The hosted service is still being integrated. Stripe billing is implemented in Nebula, but checkout stays disabled until the operator's onboarding and acceptance are done. Until then only Nebula's configured platform super admins can enroll, for free. Other accounts stay pending until a verified payment activates them. Free hosting does not grant any role in Quasar.

The setup dialog shows the price: €2.99 per month or €29.99 per year, in EUR including applicable taxes. You choose the plan on Nebula, which shows the recurring total, the seller and the tax treatment before payment. One subscription covers one panel and its tunnel. Before checkout Quasar says: "Your subscription provides hosted access to your Quasar panel and also supports ongoing development and future CometWorks projects." It does not entitle you to unreleased projects. Seller and tax details are in [Nebula's billing setup](https://github.com/CometWorks/nebula/blob/main/docs/billing.md).

## Set up hosted access

You need admin permission in Quasar. On a fresh install that is normally the trusted network bypass, a browser on the Quasar machine. Quasar authentication and Steam sign-in must be on; otherwise the dialog warns "Enable Steam authentication in Quasar before enrolling a public panel." and won't start.

1. Open **Security → Manage access setup**. The same dialog ("Set up administrator access") opens by itself for an admin while `rbac.json` has no administrator or no access method has been chosen yet. Headless Quasar shows no dialog and doesn't wait for one.
2. If no mapping grants `admin`, enter your **Administrator SteamID64 or profile URL** and choose **Save administrator and continue**. Quasar accepts a 17-digit ID or Steam Community profile URL and resolves vanity URLs before saving the numeric ID. A failed lookup saves nothing. See [The first administrator](AccessControl.md#the-first-administrator).
3. Choose **Use hosted panel**, then **Next**. (**Self-deploy** closes the dialog and opens the **Security** page. **Cancel** closes the dialog.)
4. Enter the **Public panel name**: 3 to 63 lowercase letters and digits, in groups separated by single dashes. Names that Nebula uses for its own pages, such as `account`, `api`, `login` or `billing`, are refused. The dialog previews the public address.
5. Choose **Start hosted enrollment**. Quasar stores its private installation identity, registers the name with Nebula and opens the Nebula account page in a new tab. If the browser blocks the tab, use **Continue enrollment with Steam**. Sign in with Steam on Nebula, confirm this installation and activate hosting.
6. The dialog follows four stages: **Create enrollment**, **Confirm Steam account and hosting activation**, **Connect secure tunnel** and **Verify Quasar dashboard**. Once the tunnel and the local Quasar both report healthy, it shows **Open Quasar dashboard**, **Done** and **Manage Nebula hosting and billing**.

**Cancel** while connecting stops hosted setup and the connector. It keeps the private identity and does not cancel a subscription. Start again with the same name to resume. The Nebula link expires after 15 minutes ("Enrollment link expired. Choose hosted setup again."); start hosted enrollment again. Nebula reports a name that is already taken before activation; pick another one. An enrolled installation can't switch to a different name: "This installation is already enrolled. Use its existing public name to reconnect."

After that, sign in at the public address with Steam. Your roles from `rbac.json` apply there as on the local address.

## How the connection works

- Quasar runs the frp client (`frpc` 0.71.1) as its own child process on Linux x64 and Windows x64. On first use it downloads the official release archive from GitHub, checks it against a pinned SHA-256 and keeps the binary with its license in `HostedPanel/frp-0.71.1/`. You run no frp commands and paste no credentials.
- The connector connects out over WSS to `dash.se-servers.net` port 443. It verifies the server certificate against the machine's trusted root certificates, which Quasar exports to `HostedPanel/trusted-roots.pem`. With no trusted roots, hosting stays off.
- Quasar checks the connector every 10 seconds and restarts it when it exited or its configuration changed. When Quasar stops, it stops the connector, so the public address is down while Quasar restarts or updates. A connector left behind by a crash is recognized by its PID, start time and path and stopped at the next start.
- Each check reports two things to Nebula and to `HostedPanel/connector-health.json`: whether the tunnel is registered, and whether the local `/api/ready` answers. The panel counts as ready only when both are true in a report younger than 35 seconds. Enrollment alone does not mean it is reachable.
- Quasar also asks Nebula every 10 seconds for the enrollment state, and picks up the connector credential once hosting is active.
- If Nebula is down, or hosting expires or is suspended, only the public address stops working. Local access and the servers' goal state don't change.
- Confirmed inactive access, including a confirmed refund, clears the connector credential and readiness. Quasar retains the enrollment and private installation proof so later paid activation can reconnect without relinking. A completed setup stays completed. Fresh tunnel/worker health is required after reactivation.
- Bootstrap stops its own connector when Nebula's authenticated heartbeat returns 401/403 and requires a successful heartbeat before starting it again. A late heartbeat cannot rewrite enrollment. The normal check cadence is 10 seconds, not an instant push: the worker allows 15 seconds per Nebula request; Bootstrap allows 3 seconds per local probe and 8 seconds for a heartbeat, plus connector shutdown time. Provider processing, networking and scheduling can add delay.
- Hosted RTC sessions also check installation authority, normally once per second. Their independent 120-second monotonic lease cannot survive failed entitlement renewal indefinitely; the existing forwarding bound is 130 seconds from the last successful renewal request starting under normal scheduling. See `Docs/HostedPanel.md` for the detailed bounds.

Status texts in the dialog:

| Status | Meaning |
| --- | --- |
| "Waiting for Steam enrollment" | Continue on the Nebula page. |
| "Waiting for hosted access activation. Continue on the Nebula account page." | The Steam confirmation or the activation (payment) is not done yet. |
| "Enrolled; Bootstrap is connecting the hosted tunnel" | Hosting is active; the tunnel is not registered yet. The text names Bootstrap, but Quasar itself runs the connector. |
| "Tunnel connected; local Quasar is restarting" | The tunnel is up, `/api/ready` does not answer. |
| "Hosted dashboard ready" | Both are healthy. |
| "Hosting service unavailable; local Quasar continues working" | Quasar can't reach Nebula. |
| "Hosted access inactive; local Quasar remains available. Manage hosting on the Nebula account page." | Nebula denied hosting access. Local Quasar remains available; the enrollment can reactivate after later verified payment. |
| "Installation disconnected. Restart hosted setup to relink your panel." | Nebula no longer knows this installation, for example after a disconnect or a recovery on another machine. Quasar has cleared its connector credential. |

## Security of hosted requests

- The connector forwards requests for `/<panel-name>/` to Quasar on loopback and adds a private header with a secret only it and Quasar know. Quasar treats a request as hosted only with that exact secret, from a loopback socket, for the enrolled prefix, and while authentication and Steam sign-in are on. Any other request with that header gets 403.
- Quasar uses the prefix as the application base, so links, redirects, the Steam callback, downloads and the browser push service worker stay under `/<panel-name>/`. Cookies get the panel name as a suffix, the prefix as their path, and are always Secure.
- Hosted requests never get the [trusted network bypass](AccessControl.md#trusted-network-bypass), although they arrive from loopback. Everyone signs in with Steam and gets roles from `rbac.json`.
- Machine routes stay protected by their credentials and the [machine endpoint](AccessControl.md#machine-endpoints) policy, which counts a hosted request as `127.0.0.1:<port>`. A Host executor or agent can use the exact hosted panel URL, with its trailing slash, as its Quasar address; enrollment, Host updates and the Host and agent WebSockets keep the prefix. Quasar never replaces existing local Host or agent addresses with the public one.
- URL paths are not browser origins. Every panel and the Nebula portal share the origin `https://dash.se-servers.net`, so a script that a UI plugin brings into one panel can reach the others in the same browser. Sign-in and RBAC don't change that. The operator accepts this risk and answers abuse by suspending or revoking access afterwards, which does not undo what already happened. See [Nebula's operating decision](https://github.com/CometWorks/nebula/blob/main/docs/architecture.md#shared-origin-operating-decision) and [UI plugins](UiPlugins.md).

## Administrators and billing

- Every 10 seconds Quasar sends Nebula the Steam IDs that `rbac.json` maps to `admin`. They all can manage the panel in Nebula. Per the Quasar docs, Nebula stops accepting management when it has had no list for two minutes. Nebula membership comes only from these Steam IDs; a trusted-network session can run the setup but is not a member. Quasar roles never grant Nebula platform super admin rights.
- **Manage Nebula hosting and billing** on **Security** and in the ready dialog opens `https://dash.se-servers.net/account`.
- One payer holds the subscription, sees the billing details and controls renewal. Another current admin can request a billing transfer there. The payer approves or declines; a request expires after seven days. Approval stops the old subscription without refund or proration and keeps the paid access until the paid period ends. The new payer then has to check out with their own details and payment method to keep hosting. Cards and old invoices are not transferred.
- Billing requests and results reach the payer's Steam ID in Quasar's notification bell and through browser push. Enable push on **Notifications** through the hosted address to subscribe that browser for this panel; a subscription made through the local address is separate. Push needs Quasar running and the browser's permission. A payer who lost their Quasar role still gets the notices on an existing subscription. Trusted-network subscriptions don't get them. If push is off, the request is still waiting on the Nebula account page. See [Browser push notifications](Monitoring.md#browser-push-notifications).

## Move or recover a panel

To move Quasar to another machine or reinstall it, start hosted setup in the new installation and enter the existing public name. Follow the Steam link and confirm the replacement on Nebula, as the current payer or as a currently verified admin. The payer can do this even while the old installation is offline. The confirmation revokes the old connector and installation credentials, deletes the old registration and health records, and drops the old admin list; the new installation sends its own. The URL, subscription, billing details and paid period stay with the panel. Game servers, worlds and local settings are not moved or deleted.

Disconnecting an installation on the Nebula account page does not cancel renewal; manage billing separately. Quasar then clears its connector credential, and you can enroll again. Renaming a panel is not implemented.

## Local files

`HostedPanel/` in the Quasar data directory holds:

| File | Content |
| --- | --- |
| `enrollment.json` | Access choice, panel name, installation secret, local proxy secret, enrollment ID, connector credential and payer SteamID. |
| `frpc.toml` | Generated connector configuration, including secrets. |
| `trusted-roots.pem` | Exported trusted root certificates. |
| `connector-health.json`, `connector-process.json` | Last health report and the running connector's PID record. |
| `frp-0.71.1/` | The verified `frpc` binary and its license. |

On Linux the folder is owner-only. On Windows only the account running Quasar, SYSTEM and Administrators have access. Treat backups of it as secrets. A copy restores the same identity, so run only one restored copy per enrollment.

If `enrollment.json` can't be read, Quasar keeps it, turns hosting off and keeps running locally ("Hosted enrollment state is unreadable; local Quasar continues working"). Changing the setup is refused until you restore it from a backup: "Hosted state is unreadable. Restore its private backup before changing enrollment. Local Quasar remains available."

The detailed operator guide, including the acceptance checks for push delivery, is `Docs/HostedPanel.md` in the Quasar repository.

## Sources

- `Quasar/Components/Pages/PostInstallSetup.razor`
- `Quasar/Components/Shared/AccessSetupPrompt.razor`

- `Docs/HostedPanel.md`
- `Shared/Hosting/HostedPanelState.cs`
- `Quasar/Services/HostedPanelService.cs`
- `Quasar/Services/HostedPanelRequest.cs`
- `Quasar.Bootstrap/HostedPanelConnector.cs`
- `Quasar/Program.cs`
- `Quasar/Services/Updates/PushNotificationService.cs`
- `Quasar/wwwroot/quasar-push.js`
- `Quasar/wwwroot/push-worker.js`
- `Quasar/Services/MachineEndpointSecurity.cs`
- `Quasar/Services/Auth/TrustedNetworkEvaluator.cs`
- `Quasar/Services/Auth/RbacConfigCatalog.cs`
- `Quasar/Services/Auth/SteamProfileClient.cs`
- `Quasar/Components/Shared/AccessSetupDialog.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Pages/Security.razor`
- `Shared/Networking/QuasarControlAddress.cs`
- `Quasar.Host/HostConnection.cs`
- `Quasar.Agent/WebServiceLocator.cs`
- `Quasar/Assets/host-update.py`
