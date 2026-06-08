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
REQUIRED_DOTNET_MAJOR=10

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

normalize_version_component() {
    local value="${1:-0}"
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
        echo 0
        return
    fi
    if (( value > 65534 )); then
        value=$((value % 10000))
    fi
    echo "$value"
}

normalize_nuget_version() {
    local version="${1#v}"
    version="${version%%+*}"

    if [[ -z "$version" ]]; then
        echo "0.1.0"
        return
    fi

    if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]]; then
        echo "$version"
        return
    fi

    local suffix="${version//[^0-9A-Za-z.-]/-}"
    suffix="${suffix#.}"
    suffix="${suffix#-}"
    if [[ -z "$suffix" ]]; then
        suffix="local"
    fi
    echo "0.1.0-${suffix}"
}

build_assembly_file_version() {
    local raw_version="${1#v}"
    raw_version="${raw_version%%-*}"
    raw_version="${raw_version%%+*}"

    if [[ ! "$raw_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
        echo "0.1.0"
        return
    fi

    IFS='.' read -r -a version_parts <<< "$raw_version"
    echo "$(normalize_version_component "${version_parts[0]}").$(normalize_version_component "${version_parts[1]}").$(normalize_version_component "${version_parts[2]}")"
}

resolve_build_version() {
    if [[ -n "${VERSION:-}" ]]; then
        echo "$VERSION"
        return
    fi

    git -C "$SCRIPT_DIR" describe --tags --exact-match 2>/dev/null \
        || git -C "$SCRIPT_DIR" rev-parse --short HEAD 2>/dev/null \
        || echo "0.1.0-local"
}

require_dotnet_runtime() {
    if ! command -v dotnet >/dev/null 2>&1; then
        echo ".NET $REQUIRED_DOTNET_MAJOR runtime is required before installing Quasar." >&2
        echo "Install it first: https://dotnet.microsoft.com/download/dotnet/$REQUIRED_DOTNET_MAJOR.0" >&2
        exit 1
    fi

    local runtimes
    if ! runtimes="$(dotnet --list-runtimes 2>/dev/null)"; then
        echo "Could not list installed .NET runtimes." >&2
        echo ".NET $REQUIRED_DOTNET_MAJOR runtime is required before installing Quasar." >&2
        exit 1
    fi

    if ! grep -Eq "^Microsoft\.NETCore\.App[[:space:]]+$REQUIRED_DOTNET_MAJOR\." <<< "$runtimes"; then
        echo ".NET $REQUIRED_DOTNET_MAJOR runtime is required before installing Quasar." >&2
        echo "Installed .NET runtimes:" >&2
        if [[ -n "$runtimes" ]]; then
            sed 's/^/  /' <<< "$runtimes" >&2
        else
            echo "  (none reported)" >&2
        fi
        echo "Install it first: https://dotnet.microsoft.com/download/dotnet/$REQUIRED_DOTNET_MAJOR.0" >&2
        exit 1
    fi
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

if [[ "$SKIP_BUILD" == "false" && -x "$SCRIPT_DIR/Quasar" && ! -f "$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" ]]; then
    SKIP_BUILD=true
fi

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "install.sh currently supports Linux/systemd only." >&2
    exit 1
fi

require_dotnet_runtime

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
    if [[ -x "$SCRIPT_DIR/Quasar" ]]; then
        SOURCE_DIR="$SCRIPT_DIR"
    else
        SOURCE_DIR="$SCRIPT_DIR/Quasar.Bootstrap/bin/$CONFIGURATION/net10.0/$RUNTIME/publish"
    fi
    if [[ ! -x "$SOURCE_DIR/Quasar" ]]; then
        echo "Existing publish output not found: $SOURCE_DIR" >&2
        exit 1
    fi
    find "$SOURCE_DIR" -mindepth 1 -maxdepth 1 \
        ! -name "quasar-*.tar.gz" \
        ! -name "quasar-*.zip" \
        -exec cp -a -- {} "$PUBLISH_DIR/" \;
else
    BUILD_VERSION="$(resolve_build_version)"
    NUGET_VERSION="$(normalize_nuget_version "$BUILD_VERSION")"
    ASSEMBLY_FILE_VERSION="$(build_assembly_file_version "$BUILD_VERSION")"
    chown "$RUN_USER:$RUN_GROUP" "$PUBLISH_DIR"
    echo "Publishing Quasar ($CONFIGURATION, $RUNTIME, version $NUGET_VERSION)..."
    sudo -u "$RUN_USER" \
        env HOME="$RUN_HOME" \
        dotnet publish "$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" \
            -c "$CONFIGURATION" \
            -r "$RUNTIME" \
            -p:CopyToDeployDir=false \
            -p:Version="$NUGET_VERSION" \
            -p:AssemblyVersion="$ASSEMBLY_FILE_VERSION" \
            -p:FileVersion="$ASSEMBLY_FILE_VERSION" \
            -p:InformationalVersion="$NUGET_VERSION" \
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
TimeoutStopSec=1800
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
