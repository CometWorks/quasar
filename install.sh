#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SERVICE_NAME="quasar"
INSTALL_DIR="/opt/quasar"
CONFIGURATION="Release"
RUNTIME="linux-x64"
ENABLE_SERVICE=true
START_SERVICE=false
SKIP_BUILD=false
RUN_USER="${SUDO_USER:-${USER:-}}"

usage() {
    cat <<EOF
Usage: sudo ./install.sh [options]

Publishes Quasar, installs a systemd service, and grants CAP_SYS_NICE through
systemd so Quasar can raise managed server priority with renice.

Options:
  --install-dir <dir>       Install directory (default: /opt/quasar)
  --service-name <name>     systemd service name (default: quasar)
  --user <user>             User to run Quasar as (default: sudo caller)
  --configuration <name>    Build configuration (default: Release)
  --runtime <rid>           Publish runtime identifier (default: linux-x64)
  --no-enable               Do not enable the service at boot
  --start                   Restart/start the service after installing
  --no-build                Install from an existing publish directory
  -h, --help                Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)
            INSTALL_DIR="${2:?Missing value for --install-dir}"
            shift 2
            ;;
        --service-name)
            SERVICE_NAME="${2:?Missing value for --service-name}"
            shift 2
            ;;
        --user)
            RUN_USER="${2:?Missing value for --user}"
            shift 2
            ;;
        --configuration)
            CONFIGURATION="${2:?Missing value for --configuration}"
            shift 2
            ;;
        --runtime)
            RUNTIME="${2:?Missing value for --runtime}"
            shift 2
            ;;
        --no-enable)
            ENABLE_SERVICE=false
            shift
            ;;
        --start)
            START_SERVICE=true
            shift
            ;;
        --no-build)
            SKIP_BUILD=true
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
    echo "install.sh currently supports Linux/systemd only." >&2
    exit 1
fi

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run as root, usually: sudo ./install.sh" >&2
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl not found; systemd install cannot continue." >&2
    exit 1
fi

if [[ -z "$RUN_USER" ]] || ! id "$RUN_USER" >/dev/null 2>&1; then
    echo "Install user '$RUN_USER' does not exist. Pass --user <user>." >&2
    exit 1
fi

RUN_GROUP="$(id -gn "$RUN_USER")"
RUN_HOME="$(getent passwd "$RUN_USER" | cut -d: -f6)"
if [[ -z "$RUN_HOME" ]]; then
    echo "Could not resolve home directory for '$RUN_USER'." >&2
    exit 1
fi

case "$INSTALL_DIR" in
    ""|"/"|"/bin"|"/boot"|"/dev"|"/etc"|"/home"|"/lib"|"/lib64"|"/proc"|"/root"|"/run"|"/sbin"|"/sys"|"/tmp"|"/usr"|"/var")
        echo "Refusing unsafe install directory: $INSTALL_DIR" >&2
        exit 1
        ;;
esac

PUBLISH_DIR="$(mktemp -d /tmp/quasar-publish.XXXXXX)"
cleanup() {
    rm -rf "$PUBLISH_DIR"
}
trap cleanup EXIT

if [[ "$SKIP_BUILD" == "true" ]]; then
    SOURCE_DIR="$SCRIPT_DIR/Quasar.Bootstrap/bin/$CONFIGURATION/net10.0/$RUNTIME/publish"
    if [[ ! -x "$SOURCE_DIR/Quasar" ]]; then
        echo "Existing publish output not found: $SOURCE_DIR" >&2
        exit 1
    fi
    cp -a "$SOURCE_DIR/." "$PUBLISH_DIR/"
else
    chown "$RUN_USER:$RUN_GROUP" "$PUBLISH_DIR"
    echo "Publishing Quasar ($CONFIGURATION, $RUNTIME)..."
    sudo -u "$RUN_USER" \
        env HOME="$RUN_HOME" \
        dotnet publish "$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" \
            -c "$CONFIGURATION" \
            -r "$RUNTIME" \
            -p:CopyToDeployDir=false \
            -o "$PUBLISH_DIR" \
            -v minimal
fi

echo "Installing Quasar to $INSTALL_DIR..."
install -d -m 0755 -o "$RUN_USER" -g "$RUN_GROUP" "$INSTALL_DIR"
find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
cp -a "$PUBLISH_DIR/." "$INSTALL_DIR/"
chown -R "$RUN_USER:$RUN_GROUP" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/Quasar"
if [[ -f "$INSTALL_DIR/WebService/Quasar" ]]; then
    chmod +x "$INSTALL_DIR/WebService/Quasar"
fi

SERVICE_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
echo "Writing $SERVICE_PATH..."
cat > "$SERVICE_PATH" <<EOF
[Unit]
Description=Quasar Space Engineers supervisor
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=$RUN_USER
Group=$RUN_GROUP
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/Quasar serve --quiet
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
KillMode=process
TimeoutStopSec=300
SuccessExitStatus=130 143
Environment=QUASAR_MODE=Service
Environment=QUASAR_OPEN_BROWSER_ON_START=false
AmbientCapabilities=CAP_SYS_NICE
CapabilityBoundingSet=CAP_SYS_NICE
NoNewPrivileges=false

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload

if [[ "$ENABLE_SERVICE" == "true" ]]; then
    systemctl enable "$SERVICE_NAME.service"
fi

if [[ "$START_SERVICE" == "true" ]]; then
    systemctl restart "$SERVICE_NAME.service"
fi

cat <<EOF
Installed Quasar.

Service:     $SERVICE_NAME.service
Install dir: $INSTALL_DIR
Run user:    $RUN_USER

CAP_SYS_NICE is configured through systemd:
  AmbientCapabilities=CAP_SYS_NICE
  CapabilityBoundingSet=CAP_SYS_NICE

Verify after starting:
  systemctl status $SERVICE_NAME.service
  PID=\$(systemctl show -p MainPID --value $SERVICE_NAME.service)
  grep CapAmb /proc/\$PID/status
  capsh --decode=\$(awk '/CapAmb/ {print \$2}' /proc/\$PID/status)

Start/restart when ready:
  sudo systemctl restart $SERVICE_NAME.service
EOF
