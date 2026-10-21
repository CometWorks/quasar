# Urgent security upgrade: DIS-0042

This update closes unauthenticated agent impersonation and hardens related machine
control routes. Install promptly in a planned maintenance window. A game-server
restart is required to load the authenticated agent. There is no tokenless grace
period, including on localhost. Source restrictions do not replace credentials.

## Save and upgrade in this order

1. Disable automatic activation for the maintenance window. Tell players when
   servers will stop. Save every world while the old agents are still connected,
   then stop standalone servers from Quasar. Stop clusters through their Gateway
   lifecycle controls, with a save. Verify saves and take a backup before proceeding.
2. Back up the complete Quasar install/data directory, including
   `cluster-credentials.json` **and** `DataProtection-Keys/`. These are required
   together even on standalone-only installations. Keep both on the persistent
   Docker volume and carry both when moving/restoring an install, with the same
   service account/access protections. The keyring unlocks the credential store;
   losing it while retaining the store prevents authenticated server launches.
   Do not delete either file to work around an error. Restore the matching backup.
   Back up each enrolled Host's `host.json` and complete `state/` directory too;
   `state/credentials.json` contains its enrollment and cluster secrets.
3. Configure the machine source and Host lists below before bringing remote Hosts
   back. Keep listener/UI exposure in the existing web-host configuration.
4. Update Quasar (worker and Bootstrap) first. It supplies the matching Host binary
   and new standalone agent. Update every remote Host from that Quasar release
   before starting cluster nodes. Guided Hosts have an update timer; verify the
   installed binary matches Quasar's `/api/v1/hosts/<id>/update` SHA-256, or rerun
   that Host's enrollment installer. The local Host updates from Quasar's bundled
   binary. For manual Hosts, replace the executable from the new manual package
   while preserving their existing configuration and state; restart the Host.
   Updating a Host alone does not replace an already running node's environment.
5. Existing clusters pin `quasar-agent` to the Quasar source commit used at
   preparation. A node restart alone retains that old pin. On the cluster's
   settings, select its existing configuration profile and **Stage profile
   changes**, even if no profile settings changed. This exports the newly bundled
   agent with this Quasar release's commit, freezes a new dependency snapshot,
   and prepares it on every Host. Review the candidate and **Apply pending
   update** in Maintenance using the normal saved-world update flow. Verify the
   candidate `CommonPlugins/Quasar.Agent/Quasar.Agent.xml` has the new release
   commit. Finish any pending update before staging. Do not edit frozen manifests
   or deployment hashes by hand. Unsupported old cluster packages must first be
   migrated to a package supporting preserved-world profile updates; keep them
   stopped until that succeeds. A Quasar build without source-commit provenance
   cannot prepare cluster agents; use a published build.
6. Start standalone servers from Quasar to deploy the bundled agent and issue the
   per-server launch token. Start/apply the prepared cluster deployment only after
   all Hosts are updated. Confirm every expected server/node has authenticated
   telemetry and that save/chat/control operations work before reopening to players.

If Quasar was already upgraded with worlds running, **save in game before any
manual restart**. An unauthenticated agent cannot receive Quasar's save command.
A process adopted after worker turnover stays visible as unhealthy/disconnected,
with automatic attach recovery, health restarts and scheduled/maximum-uptime
restarts suppressed until authenticated running telemetry returns. Explicit admin
stop/restart is still allowed and may have to terminate without an agent save.
This suppression does not change crash recovery for newly launched processes.

An old standalone agent that connected before the upgrade still uses its own
save-and-stop offline timer (one hour by default); Quasar cannot change that loaded
code. A standalone agent that never connected does not arm this timer. Current
cluster agents never self-stop because Quasar is offline; Gateway/Host owns their
lifecycle. Do not rely on these policies as an upgrade procedure.

## Machine endpoint network policy

All credentials remain mandatory. Configure Quasar with exact private IPs and/or
CIDRs for all physical Host/agent peers, including multiple LAN/VPN sites:

```json
{
  "Quasar": {
    "MachineEndpoints": {
      "AllowedSources": ["127.0.0.1", "::1", "10.42.0.12", "10.43.8.0/24", "fd12:3456:789a::/64"],
      "AllowedHosts": ["127.0.0.1:8080", "localhost:8080", "quasar.vpn:8080", "quasar.example.com"]
    }
  }
}
```

Environment overrides are semicolon-separated lists:
`QUASAR_MACHINE_ALLOWED_SOURCES` and `QUASAR_MACHINE_ALLOWED_HOSTS`. Overrides
replace the entire list. Keep local destinations needed by standalone agents,
Bootstrap drain and the local Host. Without source configuration, only
`127.0.0.1` and `::1` are allowed. IPv4-mapped IPv6 socket peers are normalized.
Without Host configuration, the public base URL's authority and the web port's
three loopback authorities are allowed. Authorities are case-insensitive exact
values, including the port when present; there are no wildcards or suffix matches.
For a default HTTP/HTTPS port, configure the authority actually sent by the client
(e.g. `quasar.example.com` for normal HTTPS, or `quasar.example.com:443` if a client
explicitly sends that port). A trailing DNS dot is not an alias.

Empty, malformed, public or overly broad sources fail configuration. IPv4 prefixes
must be at least /16 and IPv6 at least /48, contained within RFC1918, loopback or
IPv6 ULA space. Use narrower site subnets or individual peers; `0.0.0.0/0`,
`10.0.0.0/8`, `::/0` and whole ULA space are rejected. Empty lists are not a way
to expose routes. Wildcard/unspecified destination authorities fail configuration.

Any `Origin` header, including empty or literal `null`, is rejected on machine
routes. Browser-facing login, UI, enrollment management and cluster administration
APIs retain their existing authorization and browser behavior.

The policy uses the original socket peer and Host **before forwarded-header
processing**. Quasar disables ASP.NET's automatic forwarded-header startup filter
(`ASPNETCORE_FORWARDEDHEADERS_ENABLED`); configure the existing explicit Quasar
trusted-proxy settings for browser forwarding instead. `Forwarded`, `X-Forwarded-For`, `X-Original-For`, `X-Real-IP` and
`X-Forwarded-Host` cannot grant source/Host access. Behind a reverse proxy, allow
only the proxy's actual private socket IP and the exact backend Host it sends;
restrict the original client IPs at that proxy as well. A backend sees all clients
of an allowed proxy as that proxy. Keep proxy-to-backend access private, preserve
Origin, and forward WebSocket upgrades and credential headers without logging
credentials. Do not strip Origin to admit browser requests. Browser trusted-network
bypass and its proxy settings do not bypass machine credentials or this policy.

`Quasar:Host` / `QUASAR_WEB_HOST` and `Quasar:Port` continue to own listener binding.
For multiple Kestrel bindings use the existing `ASPNETCORE_URLS` configuration;
configure TLS certificates through Kestrel or terminate TLS at a private proxy.
Machine policy does not change where the browser UI listens.

Provisioned Host command listeners remain `http://127.0.0.1:<command-port>`.
Quasar sends commands through the Host's outbound authenticated WSS connection to
its configured Quasar HTTPS origin; tunnels are bound to Host identity and a
single pending tunnel ID. The command hop additionally requires the enrollment
bearer credential, loopback socket source and exact configured local Host. A
physical Host need not open an inbound LAN control port. Multiple Hosts/sites
work through independent enrolled tunnels over the site-to-site VPN. Manual
`host.json` commands can narrow/explicitly list `allowedSources` and `allowedHosts`;
the listener URL still must be loopback. Do not use these lists to expose a
provisioned Host's local control port.

Use HTTPS/WSS for remote agent and Host traffic. Plain HTTP/WS is acceptable only
on local loopback or within a deliberately private, encrypted site-to-site VPN;
source filtering does **not** encrypt tokens, telemetry, chat or commands. Enrollment
requires HTTPS except for loopback. Certificates must verify normally; do not
use insecure TLS bypasses. Put credentials only in launch environments, private
credential files and request headers, never URL query strings, userinfo, or logs.
Enrollment ticket IDs and tunnel IDs in paths are not bearer credentials.

## Trust-boundary inventory and ownership

- `/ws/agent`: the transport policy precedes upgrade; `AgentSocketHandler` requires
  a token and bounded initial Hello. `AgentSocketAuthenticator` checks standalone
  unique-name HMACs or enrolled Host/cluster/slot/node/role/launch-attempt HMACs. Nodes get no reusable
  Host agent key. `AgentRegistry` atomically refuses live duplicate identities,
  allows a stale holder after 30 seconds, and rejects a reused AgentId belonging
  to a different authenticated identity. Old sockets cannot update replacements.
  Epoch/receipt validation and cluster lifecycle authority remain in existing
  cluster identity and Gateway/Host code; independent node incarnations may overlap.
- `/api/v1/hosts/<host>/connect`, `/tunnels/<id>`, `/update`, `/update/binary`:
  transport policy plus Host-ID-bound enrollment bearer authentication.
  Duplicate Host control sockets do not replace an existing control socket.
- `/api/v1/host-enrollment/<ticket-guid>`: transport policy plus expiring,
  one-use bearer ticket. Other enrollment routes remain admin/RBAC browser APIs.
- `/api/internal/drain`: transport policy plus the per-Bootstrap-run launcher
  secret, compared in constant time. Auth/trusted-network UI settings do not
  substitute for that secret. Bootstrap has no separate inbound HTTP listener.
- Host `/host/v1/*`: transport policy and bearer validation before acquiring
  execution/storage gates or reading command bodies. One shared command boundary
  covers status, credentials, attachments, deployments, transfers, snapshots,
  shutdown and diagnostics. Local CLI calls use the same boundary. Gateway executor
  polling is outbound and retains its separate cluster-scoped credentials.
- Shared source/authority validation lives in `MachineEndpointPolicy`; Quasar's
  middleware owns route selection before forwarding, and Host's command handler
  uses the same policy at its own boundary. Listener binding stays with each host.

## Acceptance and rollout limits

Automated Linux checks use in-memory handlers/TestServer and isolated temporary
credential stores. Upgrade-safety tests adopt a harmless bounded `sleep` process,
never a game. Full Windows/.NET Framework and real enrolled multi-machine game
acceptance still require a human before release. This note is release preparation;
it does not authorize publication, announcements or deployment.

## Viewer follow-up rollout

The source-based Viewer follow-up for [ticket 1364](https://tasks.cometworks.se/tasks/1364)
is separate from [urgent agent/Host PR242](https://github.com/CometWorks/quasar/pull/242).
Apply the original urgent fix independently. The follow-up has two parts:

1. Update Quasar with the shared trusted-network principal guard. It validates
   original/effective destinations and same-origin browser context before
   granting automatic loopback or subnet roles. Configure the intended public
   BaseUrl and the proxy's backend Host as described in
   [reverse proxy auth](Configuration.md#reverse-proxy-auth). Cookies and service
   principals retain their normal precedence; machine routes retain PR242's policy.
2. Update the Entity Viewer Quasar UI plugin with approved-root asset containment
   and final-open checks, then restart the worker through the usual plugin update
   flow and reload Viewer. Install its adapter and static assets from the same
   pinned Viewer revision. See [Viewer asset boundaries](https://github.com/CometWorks/viewer/blob/main/Docs/EntityViewer.md#asset-boundary).
   No new companion protocol or agent change is introduced by this follow-up.

Neither update substitutes for the other. Viewer scene/assets retain `CanView`,
replay/settings retain administrator policies, and asset tokens remain expiring
and bound to their user/session. Streaming consent and private/no-store responses
remain required. No bearer secrets are added to browser links.

Proof is limited to in-memory request unit tests and synthetic temporary asset
fixtures on Linux. Windows link/reparse behavior and installed browser/proxy
acceptance remain for human verification after review. Managed filesystem checks
are not atomic against concurrent local directory/link replacement; protect asset
roots and their ancestors from untrusted writers. Native atomic-open evaluation
is tracked in [ticket 1367](https://tasks.cometworks.se/tasks/1367).
