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

Stops and removes the Quasar systemd service installed by install.sh, and the
systemd units of the local Host executors this Quasar enrolled (Install on this
PC). Running clusters on those Hosts are saved and stopped first.
Runtime/config data under the install directory and the Host folders are not
removed unless --purge is passed.

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

# Local Host executors run as user units of the account Quasar runs as.
if [[ "$INSTALL_MODE" == "system" ]]; then
    HOST_USER="$(sed -n 's/^User=//p' "$SERVICE_PATH" 2>/dev/null | head -n 1)"
    HOST_USER="${HOST_USER:-root}"
    HOST_HOME="$(getent passwd "$HOST_USER" | cut -d: -f6)"
    HOST_SYSTEMCTL=(systemctl --user --machine="$HOST_USER@")
else
    HOST_HOME="$HOME"
    HOST_SYSTEMCTL=(systemctl --user)
fi
HOST_UNIT_DIR="$HOST_HOME/.config/systemd/user"

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

"${SYSTEMCTL[@]}" daemon-reload
"${SYSTEMCTL[@]}" reset-failed "$SERVICE_UNIT" >/dev/null 2>&1 || true

REMOVED_HOSTS=()
KEPT_HOST_DIRS=()

unit_host_root() { # unit_host_root <id>: folder of the Host that quasar-host-<id>.service runs
    local root
    root="$(sed -n 's/^ExecStart="\(.*\)\/Quasar\.Host".*/\1/p' "$HOST_UNIT_DIR/quasar-host-$1.service" 2>/dev/null | head -n 1)"
    [[ -n "$root" ]] && realpath -m "${root//%h/$HOST_HOME}"
}

# Quasar installs its local Host executors in HostExecutors/<id>. Unit names only carry the
# Host ID, so a unit is this install's only when it runs the Host from that folder.
for host_root in "$INSTALL_DIR"/HostExecutors/*/; do
    host_root="${host_root%/}"
    host_id="$(basename "$host_root")"
    [[ -d "$host_root" && "$(unit_host_root "$host_id")" == "$host_root" ]] || continue

    echo "Stopping local Host executor $host_id; its running clusters are saved first (up to 2 minutes)..."
    # Stopping the stop unit runs stop-clusters, which needs the Host itself still running.
    "${HOST_SYSTEMCTL[@]}" disable --now "quasar-host-$host_id-stop.service" || true
    "${HOST_SYSTEMCTL[@]}" disable --now "quasar-host-$host_id-update.timer" || true
    "${HOST_SYSTEMCTL[@]}" stop "quasar-host-$host_id-update.service" || true
    "${HOST_SYSTEMCTL[@]}" disable --now "quasar-host-$host_id.service" || true
    for suffix in .service -stop.service -update.service -update.timer; do
        rm -f "$HOST_UNIT_DIR/quasar-host-$host_id$suffix" "$HOST_UNIT_DIR"/*.wants/"quasar-host-$host_id$suffix"
        "${HOST_SYSTEMCTL[@]}" reset-failed "quasar-host-$host_id$suffix" >/dev/null 2>&1 || true
    done
    REMOVED_HOSTS+=("$host_id")
    [[ "$PURGE" == "true" ]] || KEPT_HOST_DIRS+=("$host_root")
done

# Older versions installed the local Host in the home folder, where nothing tells which
# Quasar install it belongs to.
LEGACY_HOSTS=()
for registration in "$INSTALL_DIR"/Hosts/*/host.json; do
    host_id="$(basename "$(dirname "$registration")")"
    if [[ -f "$registration" && "$(unit_host_root "$host_id")" == "$(realpath -m "$HOST_HOME/.local/share/Quasar/Hosts/$host_id")" ]]; then
        LEGACY_HOSTS+=("$host_id")
    fi
done
if [[ ${#REMOVED_HOSTS[@]} -gt 0 ]]; then
    "${HOST_SYSTEMCTL[@]}" daemon-reload || true
fi

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
if [[ ${#REMOVED_HOSTS[@]} -gt 0 ]]; then
    echo "Removed local Host executor units: ${REMOVED_HOSTS[*]}"
fi
for host_dir in "${KEPT_HOST_DIRS[@]}"; do
    echo "Host files left in place: $host_dir"
done
for host_id in "${LEGACY_HOSTS[@]}"; do
    cat <<EOF

quasar-host-$host_id.service was left alone: it runs a Host from
$HOST_HOME/.local/share/Quasar/Hosts/$host_id, which may belong to another
Quasar install. If no other Quasar uses it, remove it with:
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
