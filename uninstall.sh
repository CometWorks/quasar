#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="quasar"
INSTALL_DIR="/opt/quasar"
PURGE=false

usage() {
    cat <<EOF
Usage: sudo ./uninstall.sh [options]

Stops and removes the Quasar systemd service installed by install.sh.
Runtime/config data under the Quasar user's home directory is not removed.

Options:
  --service-name <name>     systemd service name (default: quasar)
  --install-dir <dir>       Install directory (default: /opt/quasar)
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

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run as root, usually: sudo ./uninstall.sh" >&2
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl not found; systemd uninstall cannot continue." >&2
    exit 1
fi

SERVICE_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
SERVICE_UNIT="${SERVICE_NAME}.service"
SERVICE_LISTING="$(systemctl list-unit-files "$SERVICE_UNIT" --no-legend 2>/dev/null || true)"
LOADED_SERVICE_LISTING="$(systemctl list-units "$SERVICE_UNIT" --all --no-legend 2>/dev/null || true)"
SERVICE_EXISTS=false
if [[ -n "$SERVICE_LISTING" || -n "$LOADED_SERVICE_LISTING" || -f "$SERVICE_PATH" ]]; then
    SERVICE_EXISTS=true
fi

if [[ "$SERVICE_EXISTS" == "true" ]]; then
    echo "Stopping $SERVICE_UNIT..."
    systemctl stop "$SERVICE_UNIT"

    if systemctl is-enabled --quiet "$SERVICE_UNIT" 2>/dev/null; then
        echo "Disabling $SERVICE_UNIT..."
        systemctl disable "$SERVICE_UNIT"
    fi
fi

if [[ -f "$SERVICE_PATH" ]]; then
    echo "Removing $SERVICE_PATH..."
    rm -f "$SERVICE_PATH"
fi

systemctl daemon-reload
systemctl reset-failed "$SERVICE_UNIT" >/dev/null 2>&1 || true

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
EOF

if [[ "$PURGE" == "true" ]]; then
    echo "Removed install dir: $INSTALL_DIR"
else
    cat <<EOF

Install dir left in place: $INSTALL_DIR
Remove binaries too:
  sudo ./uninstall.sh --purge
EOF
fi
