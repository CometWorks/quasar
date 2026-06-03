# Remote Host SSH Plan

## Goal

Allow Quasar to start Space Engineers dedicated servers on prepared remote Linux hosts over SSH, keep the agent connection reachable through SSH port forwarding, and survive tunnel reconnects without losing bounded logs.

## Assumptions

- Admin prepares passwordless SSH outside Quasar.
- Host key is already trusted by the OS user running Quasar.
- Remote host has SteamCMD/runtime prerequisites or uses Quasar-managed runtime paths.
- Remote agent connects back only through the SSH tunnel; public inbound ports are not required for Quasar control traffic.

## Model Additions

- Host definition: name, hostname, SSH user, SSH port, remote base directory, labels, enabled flag.
- Instance placement: local host or remote host id.
- Tunnel policy: local bind port, remote bind port, reconnect interval, max backoff.
- Remote log policy: buffer size, drop-oldest behavior, last uploaded offset.

## Supervisor Flow

1. Select host from instance definition.
2. For local host, use existing process supervisor.
3. For remote host:
   - Establish SSH control connection.
   - Ensure remote directories exist.
   - Upload or render instance config files.
   - Start SSH port forward for Quasar web/agent endpoint.
   - Start remote server detached from SSH session.
   - Track remote pid file and heartbeat file.
4. Agent connects through tunnel and becomes normal live-data source.
5. If tunnel drops, reconnect with backoff and keep process state as `STARTING` or `OPEN` based on last heartbeat age.
6. Stop sends agent graceful stop first, then remote process termination if needed.

## Logging

- stdout/stderr are not primary remote transport.
- Agent sends live logs through protocol.
- Remote side keeps a bounded file/ring buffer.
- On reconnect, agent sends entries newer than last acknowledged sequence.
- Buffer default: size capped by bytes and line count; drop oldest first.

## UI

- Hosts page lists local and remote hosts.
- Instance editor gets Host dropdown.
- Server card shows host name and tunnel state.
- Remote errors surface in dashboard problem banner.

## Security

- No password storage in Quasar for first version.
- No host-key bypass.
- SSH command arguments must be generated, not shell-concatenated from user text.
- Remote paths validated and quoted.

## Rollout

1. Host catalog and UI.
2. SSH tunnel lifecycle service.
3. Remote process launcher with pid tracking.
4. Agent log replay protocol.
5. Full remote stop/restart/reconnect tests.
