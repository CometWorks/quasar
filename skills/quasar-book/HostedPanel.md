# Hosted access

Nebula exposes the complete local Quasar at
`https://dash.se-servers.net/my-server-panel/`. Servers and worlds remain
local. You need no per-panel DNS entry, inbound router port or certificate.

Open **Security → Manage access setup**. The same dialog appears in the first
interactive admin session. When no administrator mapping exists, first enter your
17-digit SteamID64 and choose **Save administrator and continue**. Existing
subject or claim admin mappings skip that step. Setup requires security-management
permission, normally through the existing trusted-network session on a fresh
installation; a remote Steam sign-in alone cannot claim it. Then choose
Self-deploy, Use hosted panel or Later. Headless startup remains unattended.
For hosted access choose a globally unique 3 to 63 character lowercase name using
letters/digits separated by single dashes, then follow the Steam enrollment link.
The reference expires after 15 minutes; retry locally if it expires or the name
is taken. Quasar retrieves its private connector credential automatically.

**Current availability:** Nebula's configured platform super admins can enroll
free. Stripe billing is implemented; checkout stays disabled
until operator onboarding and acceptance. Ordinary users remain pending until
verified payment activates access. Free hosting does not grant Quasar roles.
Live deployment and provider checks still gate customer launch. The operator
accepts shared-origin plugin risk without customer preapproval and uses
retrospective suspension/revocation.

The EUR price includes applicable customer taxes; processing fees are
absorbed. Nebula offers monthly or yearly billing when configured, displaying
the exact recurring price before consent. One customer corresponds to one panel/tunnel and one subscription.
Quasar explains before checkout: "Your subscription provides hosted access to
your Quasar panel and also supports ongoing development and future CometWorks
projects." This is a paid service with no entitlement to unreleased projects.

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

Run the packaged Bootstrap launcher/service, which manages the verified frpc
0.71.1 process independently of web-worker updates. Direct worker runs do not
start a connector. Linux x64 and Windows x64 are supported by provisioning code;
live Windows operation remains an acceptance check. Local access and game-server
goal state continue through hosting outages or expiry. Readiness requires fresh tunnel-registration and local-worker health reports;
enrollment alone does not confirm connectivity.

Hosted requests never use loopback/LAN admin bypass. Steam/RBAC and existing
machine credentials/Origin restrictions still apply. Public browser URLs do not
replace existing local Agent/Host addresses. Explicit hosted HTTPS control
addresses preserve the exact panel prefix for enrollment, Host updates and
Host/Agent WebSockets. Core links, cookies, service workers and the
matching Entity Viewer use the enrolled prefix. URL paths do not isolate arbitrary
plugin scripts from other same-origin panels or the portal. This is an accepted
operator risk, not browser isolation supplied by login or RBAC. Retrospective
suspension/revocation stops further hosted access within the transport limits;
it does not undo earlier actions.

Keep `HostedPanel/` under the data/install root private: it contains installation,
connector and local proxy secrets, generated configuration and the verified
connector download. Unix uses owner-only permissions; Windows grants the service
identity, SYSTEM and Administrators. Protect backups and run only one restored
connector for a copied enrollment identity. Corrupt state preserves its file and
disables hosting while local Quasar remains available. Restore a private backup
before editing enrollment. Self-deploy disconnects hosting and retains the local
identity; selecting the same hosted name resumes it. Renaming is not implemented.

Manage billing/payment methods and cancel or resume subscription renewal on Nebula's
account page. Canceling retains the paid service period; refunds and disputes
can retract it. A checkout return never proves payment. To reinstall or move,
enroll the same panel name as the verified Steam owner and explicitly confirm
replacement. Old credentials are revoked; billing and paid dates stay linked.
Disconnecting the installation does not cancel subscription renewal. A revoked
local enrollment clears credentials and permits fresh pairing.

See [the detailed deployment and recovery guide](https://github.com/CometWorks/quasar/blob/main/Docs/HostedPanel.md).

## Sources

- `Shared/Hosting/HostedPanelState.cs`
- `Quasar/Services/HostedPanelService.cs`
- `Quasar/Services/HostedPanelRequest.cs`
- `Quasar/Services/MachineEndpointSecurity.cs`
- `Quasar/Services/Auth/TrustedNetworkEvaluator.cs`
- `Quasar/Services/Auth/RbacConfigCatalog.cs`
- `Quasar/Components/Shared/AccessSetupDialog.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Pages/Security.razor`
- `Quasar.Bootstrap/HostedPanelConnector.cs`
- `Shared/Networking/QuasarControlAddress.cs`
- `Quasar.Host/HostConnection.cs`
- `Quasar.Agent/AgentConnection.cs`
- `Quasar.Agent/WebServiceLocator.cs`
- `Quasar/Assets/host-update.py`
- `Docs/HostedPanel.md`
