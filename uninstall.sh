#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_NAME="quasar"
INSTALL_DIR=""
INSTALL_MODE="user"
PURGE=false

usage() {
    cat <<EOF
Usage: ./uninstall.sh [options]

Stops and removes the Quasar systemd service installed by install.sh. The
clusters on this PC are saved and stopped first, and Quasar's local Host
executor is stopped with it.
Runtime/config data under the install directory is not removed unless --purge is
passed.

Options:
  --service-name <name>     systemd service name (default: quasar)
  --install-dir <dir>       Install directory (default: script directory)
  --user-service            Remove a user systemd service (default)
  --system                  Remove a system service under /etc/systemd/system
  --purge                   Also remove the install directory
  -h, --help                Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --service-name)
            SERVICE_NAME="${2:?Missing value for --service-name}"
            shift 2
            ;;
        --install-dir)
            INSTALL_DIR="${2:?Missing value for --install-dir}"
            shift 2
            ;;
        --user-service)
            INSTALL_MODE="user"
            shift
            ;;
        --system|--system-service)
            INSTALL_MODE="system"
            if [[ -z "$INSTALL_DIR" ]]; then
                INSTALL_DIR="/opt/quasar"
            fi
            shift
            ;;
        --purge)
            PURGE=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "uninstall.sh currently supports Linux/systemd only." >&2
    exit 1
fi

if [[ "$INSTALL_MODE" == "system" && "${EUID}" -ne 0 ]]; then
    echo "System service uninstall needs root. Run: sudo ./uninstall.sh --system" >&2
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl not found; systemd uninstall cannot continue." >&2
    exit 1
fi

if [[ -z "$INSTALL_DIR" ]]; then
    INSTALL_DIR="$SCRIPT_DIR"
fi
INSTALL_DIR="$(realpath -m "$INSTALL_DIR")"

if [[ "$INSTALL_MODE" == "system" ]]; then
    SERVICE_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
    SYSTEMCTL=(systemctl)
else
    SERVICE_PATH="$HOME/.config/systemd/user/${SERVICE_NAME}.service"
    SYSTEMCTL=(systemctl --user)
fi
SERVICE_UNIT="${SERVICE_NAME}.service"
CLUSTERS_UNIT="${SERVICE_NAME}-clusters.service"
CLUSTERS_PATH="$(dirname "$SERVICE_PATH")/$CLUSTERS_UNIT"
LOCAL_HOST_DIR="$INSTALL_DIR/LocalHost"
# The account Quasar runs as, read before its unit file is removed.
if [[ "$INSTALL_MODE" == "system" ]]; then
    HOST_HOME="$(getent passwd "$(sed -n 's/^User=//p' "$SERVICE_PATH" 2>/dev/null | head -n 1)" | cut -d: -f6)"
else
    HOST_HOME="$HOME"
fi

# Prints the PID of Quasar's local Host executor while the process recorded in host.pid runs.
# The record holds the boot ID and start time, so a reused PID doesn't match.
local_host_pid() {
    local record pid identity stat fields
    record="$(cat "$LOCAL_HOST_DIR/host.pid" 2>/dev/null)" || return 1
    pid="$(sed -n 's/.*"Pid":\([0-9]*\).*/\1/p' <<< "$record")"
    identity="$(sed -n 's/.*"Identity":"\([^"]*\)".*/\1/p' <<< "$record")"
    [[ -n "$pid" && -n "$identity" ]] || return 1
    stat="$(cat "/proc/$pid/stat" 2>/dev/null)" || return 1
    read -r -a fields <<< "${stat##*) }"
    [[ "${fields[0]}" != Z && "$(cat /proc/sys/kernel/random/boot_id)/${fields[19]}" == "$identity" ]] || return 1
    echo "$pid"
}

# The clusters unit's ExecStop saves and stops the clusters on this PC, and needs the
# local Host executor still running, so it goes before Quasar.
if [[ -f "$CLUSTERS_PATH" ]]; then
    echo "Saving and stopping the clusters on this PC (up to 2 minutes)..."
    "${SYSTEMCTL[@]}" disable --now "$CLUSTERS_UNIT" || true
elif local_host_pid >/dev/null && [[ -x "$LOCAL_HOST_DIR/stop-clusters" ]]; then
    echo "Saving and stopping the clusters on this PC (up to 2 minutes)..."
    "$LOCAL_HOST_DIR/stop-clusters" || true
fi

SERVICE_LISTING="$("${SYSTEMCTL[@]}" list-unit-files "$SERVICE_UNIT" --no-legend 2>/dev/null || true)"
LOADED_SERVICE_LISTING="$("${SYSTEMCTL[@]}" list-units "$SERVICE_UNIT" --all --no-legend 2>/dev/null || true)"
SERVICE_EXISTS=false
if [[ -n "$SERVICE_LISTING" || -n "$LOADED_SERVICE_LISTING" || -f "$SERVICE_PATH" ]]; then
    SERVICE_EXISTS=true
fi

if [[ "$SERVICE_EXISTS" == "true" ]]; then
    echo "Stopping $SERVICE_UNIT..."
    "${SYSTEMCTL[@]}" stop "$SERVICE_UNIT"

    if "${SYSTEMCTL[@]}" is-enabled --quiet "$SERVICE_UNIT" 2>/dev/null; then
        echo "Disabling $SERVICE_UNIT..."
        "${SYSTEMCTL[@]}" disable "$SERVICE_UNIT"
    fi
fi

if [[ -f "$SERVICE_PATH" ]]; then
    echo "Removing $SERVICE_PATH..."
    rm -f "$SERVICE_PATH"
fi

# Quasar leaves its local Host executor running for its clusters when it stops.
if host_pid="$(local_host_pid)"; then
    echo "Stopping the local Host executor (PID $host_pid)..."
    kill -TERM "$host_pid" 2>/dev/null || true
    for _ in $(seq 150); do
        local_host_pid >/dev/null || break
        sleep 0.1
    done
    if local_host_pid >/dev/null; then
        kill -KILL "$host_pid" 2>/dev/null || true
    fi
fi
rm -f "$LOCAL_HOST_DIR/host.pid"

if [[ -f "$CLUSTERS_PATH" ]]; then
    echo "Removing $CLUSTERS_PATH..."
    rm -f "$CLUSTERS_PATH"
fi

"${SYSTEMCTL[@]}" daemon-reload
"${SYSTEMCTL[@]}" reset-failed "$SERVICE_UNIT" "$CLUSTERS_UNIT" >/dev/null 2>&1 || true

# Older versions installed a local Host executor in the home folder with its own units.
# Nothing tells which Quasar install it belongs to, so it is only reported.
LEGACY_HOSTS=()
for registration in "$INSTALL_DIR"/Hosts/*/host.json; do
    host_id="$(basename "$(dirname "$registration")")"
    if [[ -f "$registration" && -n "$HOST_HOME" && -f "$HOST_HOME/.config/systemd/user/quasar-host-$host_id.service" \
        && -d "$HOST_HOME/.local/share/Quasar/Hosts/$host_id" ]]; then
        LEGACY_HOSTS+=("$host_id")
    fi
done

if [[ "$PURGE" == "true" ]]; then
    case "$INSTALL_DIR" in
        ""|"/"|"/bin"|"/boot"|"/dev"|"/etc"|"/home"|"/lib"|"/lib64"|"/proc"|"/root"|"/run"|"/sbin"|"/sys"|"/tmp"|"/usr"|"/var")
            echo "Refusing unsafe install directory: $INSTALL_DIR" >&2
            exit 1
            ;;
    esac

    if [[ -d "$INSTALL_DIR" ]]; then
        echo "Removing $INSTALL_DIR..."
        rm -rf "$INSTALL_DIR"
    fi
fi

cat <<EOF
Uninstalled Quasar service.

Removed service: ${SERVICE_NAME}.service
Mode: ${INSTALL_MODE}
EOF
for host_id in "${LEGACY_HOSTS[@]}"; do
    cat <<EOF

quasar-host-$host_id.service was left alone: an older Quasar version installed it
in $HOST_HOME/.local/share/Quasar/Hosts/$host_id, and another Quasar install
may use it. If none does, remove it as that user with:
  systemctl --user disable --now quasar-host-$host_id-stop.service quasar-host-$host_id-update.timer quasar-host-$host_id.service
  cd ~/.config/systemd/user && rm -f quasar-host-$host_id.service quasar-host-$host_id-stop.service quasar-host-$host_id-update.service quasar-host-$host_id-update.timer
  systemctl --user daemon-reload
  rm -rf ~/.local/share/Quasar/Hosts/$host_id ~/.local/share/Quasar/Hosts/.$host_id.install.lock
EOF
done

if [[ "$PURGE" == "true" ]]; then
    echo "Removed install dir: $INSTALL_DIR"
else
    if [[ "$INSTALL_MODE" == "system" ]]; then
        cat <<EOF

Install dir left in place: $INSTALL_DIR
Remove binaries too:
  sudo ./uninstall.sh --system --purge
EOF
    else
        cat <<EOF

Install dir left in place: $INSTALL_DIR
Remove binaries too:
  ./uninstall.sh --purge
EOF
    fi
fi
