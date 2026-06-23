#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SERVICE_NAME="quasar"
INSTALL_DIR=""
DATA_DIR=""
PACKAGED_INSTALL=false
CONFIGURATION="Release"
RUNTIME="linux-x64"
ENABLE_SERVICE=true
START_SERVICE=false
SKIP_BUILD=false
INSTALL_MODE="user"
INSTALL_RENICE_HELPER=false
RENICE_HELPER_PATH="/usr/local/bin/quasar-renice"
RUN_USER="${USER:-}"
RUN_USER_EXPLICIT=false
REQUIRED_DOTNET_MAJOR=10

usage() {
    cat <<EOF
Usage: ./install.sh [options]

Publishes Quasar and installs a systemd user service by default. Extracted
release installers install in place by default and keep Quasar state beside the
launcher.

For machine-wide service installation, pass --system and run with sudo.

If the required .NET 10 SDK/runtime is missing, the installer detects apt, dnf,
yum, pacman, or zypper, prints the exact install commands, and prompts before
running them. On Debian 13, apt installs first add Microsoft's Debian 13 package
feed.

Options:
  --install-dir <dir>       Install directory (default: extracted installer root; source installs use <run-user-home>/.local/share/Quasar)
  --data-dir <dir>          Quasar data directory (default: install directory)
  --service-name <name>     systemd service name (default: quasar)
  --user <user>             User to run Quasar as (system installs only; default: sudo caller)
  --user-service            Install a user systemd service (default)
  --system                  Install a system service under /etc/systemd/system
  --install-renice-helper   Build and install /usr/local/bin/quasar-renice as setuid root
  --configuration <name>    Build configuration (default: Release)
  --runtime <rid>           Publish runtime identifier (default: linux-x64)
  --no-enable               Do not enable the service at boot
  --start                   Restart/start the service after installing
  --no-build                Install from an existing publish directory
  -h, --help                Show this help
EOF
}

have() {
    command -v "$1" >/dev/null 2>&1
}

privileged_prefix() {
    if [[ "${EUID}" -eq 0 ]]; then
        return
    fi

    if have sudo; then
        printf 'sudo '
        return
    fi

    echo "sudo is required to install system packages or the renice helper." >&2
    exit 1
}

install_renice_helper() {
    local source_path="$SCRIPT_DIR/tools/quasar-renice.c"
    if [[ ! -f "$source_path" ]]; then
        source_path="$SCRIPT_DIR/quasar-renice.c"
    fi
    if [[ ! -f "$source_path" ]]; then
        echo "Renice helper source not found beside installer." >&2
        exit 1
    fi
    if ! have cc; then
        echo "A C compiler (cc) is required to build the renice helper." >&2
        exit 1
    fi

    local temp_binary
    temp_binary="$(mktemp /tmp/quasar-renice.XXXXXX)"
    cc -O2 -Wall -Wextra "$source_path" -o "$temp_binary"

    if [[ "${EUID}" -eq 0 ]]; then
        install -o root -g root -m 4755 "$temp_binary" "$RENICE_HELPER_PATH"
    else
        if ! have sudo; then
            rm -f "$temp_binary"
            echo "sudo is required to install the renice helper." >&2
            exit 1
        fi
        sudo install -o root -g root -m 4755 "$temp_binary" "$RENICE_HELPER_PATH"
    fi
    rm -f "$temp_binary"
    echo "Installed setuid renice helper: $RENICE_HELPER_PATH"
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
        echo "1.0.0"
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
    echo "1.0.0-${suffix}"
}

build_assembly_file_version() {
    local raw_version="${1#v}"
    raw_version="${raw_version%%-*}"
    raw_version="${raw_version%%+*}"

    if [[ ! "$raw_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
        echo "1.0.0"
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
        || echo "1.0.0-local"
}

dotnet_has_sdk() {
    have dotnet \
        && dotnet --list-sdks 2>/dev/null \
        | grep -Eq "^$REQUIRED_DOTNET_MAJOR\."
}

dotnet_has_aspnet_runtime() {
    have dotnet || return 1
    local runtimes
    runtimes="$(dotnet --list-runtimes 2>/dev/null)" || return 1

    grep -Eq "^Microsoft\.NETCore\.App[[:space:]]+$REQUIRED_DOTNET_MAJOR\." <<< "$runtimes" \
        && grep -Eq "^Microsoft\.AspNetCore\.App[[:space:]]+$REQUIRED_DOTNET_MAJOR\." <<< "$runtimes"
}

detect_package_manager() {
    if have apt-get; then echo apt; return; fi
    if have dnf; then echo dnf; return; fi
    if have yum; then echo yum; return; fi
    if have pacman; then echo pacman; return; fi
    if have zypper; then echo zypper; return; fi
    echo ""
}

is_debian_13() {
    [[ -r /etc/os-release ]] || return 1

    (
        # shellcheck disable=SC1091
        . /etc/os-release
        [[ "${ID:-}" == "debian" && "${VERSION_ID:-}" == "13" ]]
    )
}

dotnet_package_for_manager() {
    local package_kind="$1"
    local package_manager="$2"

    if [[ "$package_manager" == "pacman" ]]; then
        if [[ "$package_kind" == "sdk" ]]; then
            echo "dotnet-sdk aspnet-runtime"
        else
            echo "aspnet-runtime"
        fi
        return
    fi

    if [[ "$package_kind" == "sdk" ]]; then
        echo "dotnet-sdk-$REQUIRED_DOTNET_MAJOR.0"
    else
        echo "aspnetcore-runtime-$REQUIRED_DOTNET_MAJOR.0"
    fi
}

build_dotnet_install_commands() {
    local package_manager="$1"
    local packages="$2"

    DOTNET_INSTALL_COMMANDS=()
    local sudo_prefix
    sudo_prefix="$(privileged_prefix)"
    case "$package_manager" in
        apt)
            if is_debian_13; then
                DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}apt-get update")
                DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}apt-get install -y ca-certificates wget")
                DOTNET_INSTALL_COMMANDS+=("wget https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -O packages-microsoft-prod.deb")
                DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}dpkg -i packages-microsoft-prod.deb")
                DOTNET_INSTALL_COMMANDS+=("rm packages-microsoft-prod.deb")
                DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}apt-get update && ${sudo_prefix}apt-get install -y $packages")
            else
                DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}apt-get update")
                DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}apt-get install -y $packages")
            fi
            ;;
        dnf)
            DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}dnf install -y $packages")
            ;;
        yum)
            DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}yum install -y $packages")
            ;;
        pacman)
            DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}pacman -Sy --noconfirm $packages")
            ;;
        zypper)
            DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}zypper --non-interactive install $packages")
            ;;
        *)
            return 1
            ;;
    esac
    DOTNET_INSTALL_COMMANDS+=("${sudo_prefix}sh -c 'if ! command -v dotnet >/dev/null 2>&1; then for dotnet_path in /usr/share/dotnet/dotnet /usr/lib64/dotnet/dotnet /opt/dotnet/dotnet; do if [ -x \"\$dotnet_path\" ] && [ ! -e /usr/local/bin/dotnet ]; then mkdir -p /usr/local/bin && ln -s \"\$dotnet_path\" /usr/local/bin/dotnet; break; fi; done; fi'")
}

prompt_and_install_dotnet() {
    local label="$1"
    local package_kind="$2"
    local package_manager
    package_manager="$(detect_package_manager)"

    if [[ -z "$package_manager" ]]; then
        echo "$label is required before installing Quasar." >&2
        echo "No supported package manager found. Install it first: https://dotnet.microsoft.com/download/dotnet/$REQUIRED_DOTNET_MAJOR.0" >&2
        exit 1
    fi

    local packages
    packages="$(dotnet_package_for_manager "$package_kind" "$package_manager")"
    if ! build_dotnet_install_commands "$package_manager" "$packages"; then
        echo "$label is required before installing Quasar." >&2
        echo "Unsupported package manager '$package_manager'. Install it first: https://dotnet.microsoft.com/download/dotnet/$REQUIRED_DOTNET_MAJOR.0" >&2
        exit 1
    fi

    echo "$label is required before installing Quasar."
    echo "Quasar can install it with the detected package manager: $package_manager"
    echo
    echo "Exact commands to run:"
    local command
    for command in "${DOTNET_INSTALL_COMMANDS[@]}"; do
        echo "  $command"
    done
    echo
    echo "These commands may add or update system packages. The final command only links dotnet onto PATH if the package manager did not."
    if [[ ! -t 0 ]]; then
        echo "Non-interactive terminal; refusing automatic .NET installation." >&2
        exit 1
    fi

    local answer
    read -r -p "Run these commands now? [y/N] " answer
    case "$answer" in
        y|Y|yes|YES|Yes)
            ;;
        *)
            echo "Cancelled. Install $label manually, then rerun install.sh." >&2
            exit 1
            ;;
    esac

    for command in "${DOTNET_INSTALL_COMMANDS[@]}"; do
        echo "+ $command"
        bash -c "$command"
    done
    hash -r
}

print_installed_dotnet_state() {
    if have dotnet; then
        echo "Installed .NET SDKs:" >&2
        dotnet --list-sdks 2>/dev/null | sed 's/^/  /' >&2 || echo "  (could not list SDKs)" >&2
        echo "Installed .NET runtimes:" >&2
        dotnet --list-runtimes 2>/dev/null | sed 's/^/  /' >&2 || echo "  (could not list runtimes)" >&2
    else
        echo "dotnet command not found on PATH." >&2
    fi
}

require_dotnet_installation() {
    if [[ "$SKIP_BUILD" == "false" ]]; then
        if dotnet_has_sdk; then
            return
        fi

        prompt_and_install_dotnet ".NET $REQUIRED_DOTNET_MAJOR SDK" "sdk"
        if dotnet_has_sdk; then
            return
        fi

        echo ".NET $REQUIRED_DOTNET_MAJOR SDK is still missing after installation attempt." >&2
        print_installed_dotnet_state
        exit 1
    fi

    if dotnet_has_aspnet_runtime; then
        return
    fi

    prompt_and_install_dotnet ".NET $REQUIRED_DOTNET_MAJOR ASP.NET Core runtime" "runtime"
    if dotnet_has_aspnet_runtime; then
        return
    fi

    echo ".NET $REQUIRED_DOTNET_MAJOR ASP.NET Core runtime is still missing after installation attempt." >&2
    print_installed_dotnet_state
    exit 1
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)
            INSTALL_DIR="${2:?Missing value for --install-dir}"
            shift 2
            ;;
        --data-dir)
            DATA_DIR="${2:?Missing value for --data-dir}"
            shift 2
            ;;
        --service-name)
            SERVICE_NAME="${2:?Missing value for --service-name}"
            shift 2
            ;;
        --user)
            RUN_USER="${2:?Missing value for --user}"
            RUN_USER_EXPLICIT=true
            shift 2
            ;;
        --user-service)
            INSTALL_MODE="user"
            shift
            ;;
        --system|--system-service)
            INSTALL_MODE="system"
            if [[ "$RUN_USER_EXPLICIT" == "false" ]]; then
                if [[ -z "${SUDO_USER:-}" ]]; then
                    RUN_USER="${RUN_USER:-root}"
                else
                    RUN_USER="$SUDO_USER"
                fi
            fi
            shift
            ;;
        --install-renice-helper)
            INSTALL_RENICE_HELPER=true
            shift
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

if [[ -x "$SCRIPT_DIR/Quasar" && ! -f "$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" ]]; then
    PACKAGED_INSTALL=true
fi

if [[ "$SKIP_BUILD" == "false" && "$PACKAGED_INSTALL" == "true" ]]; then
    SKIP_BUILD=true
fi

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "install.sh currently supports Linux/systemd only." >&2
    exit 1
fi

if [[ "$INSTALL_RENICE_HELPER" == "true" && "$SKIP_BUILD" == "true" && "$ENABLE_SERVICE" == "false" && "$START_SERVICE" == "false" ]]; then
    install_renice_helper
    exit 0
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl not found; systemd install cannot continue." >&2
    exit 1
fi

if [[ "$INSTALL_MODE" == "system" && "${EUID}" -ne 0 ]]; then
    echo "System service install needs root. Run: sudo ./install.sh --system" >&2
    exit 1
fi

if [[ "$INSTALL_MODE" == "user" && "${EUID}" -eq 0 ]]; then
    echo "User service install is the default; run install.sh without sudo." >&2
    echo "For a machine-wide service, rerun with: sudo ./install.sh --system" >&2
    exit 1
fi

if [[ "$INSTALL_MODE" == "user" && "$RUN_USER" != "${USER:-}" ]]; then
    echo "--user is only supported with --system installs." >&2
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
if [[ -z "$INSTALL_DIR" ]]; then
    if [[ "$PACKAGED_INSTALL" == "true" ]]; then
        INSTALL_DIR="$SCRIPT_DIR"
    else
        INSTALL_DIR="$RUN_HOME/.local/share/Quasar"
    fi
fi
if [[ -z "$DATA_DIR" ]]; then
    DATA_DIR="$INSTALL_DIR"
fi
INSTALL_DIR="$(realpath -m "$INSTALL_DIR")"
DATA_DIR="$(realpath -m "$DATA_DIR")"

case "$INSTALL_DIR" in
    ""|"/"|"/bin"|"/boot"|"/dev"|"/etc"|"/home"|"/lib"|"/lib64"|"/proc"|"/root"|"/run"|"/sbin"|"/sys"|"/tmp"|"/usr"|"/var")
        echo "Refusing unsafe install directory: $INSTALL_DIR" >&2
        exit 1
        ;;
esac

case "$DATA_DIR" in
    ""|"/"|"/bin"|"/boot"|"/dev"|"/etc"|"/home"|"/lib"|"/lib64"|"/proc"|"/root"|"/run"|"/sbin"|"/sys"|"/tmp"|"/usr"|"/var")
        echo "Refusing unsafe data directory: $DATA_DIR" >&2
        exit 1
        ;;
esac

require_dotnet_installation

write_service_unit() {
    if [[ "$INSTALL_MODE" == "system" ]]; then
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
Environment=HOME=$RUN_HOME
Environment=QUASAR_DATA_DIR=$DATA_DIR
Environment=QUASAR_SYSTEMD_SERVICE=$SERVICE_NAME.service
Environment=QUASAR_SYSTEMD_SCOPE=system

[Install]
WantedBy=multi-user.target
EOF
        systemctl daemon-reload
        return
    fi

    USER_SERVICE_DIR="$RUN_HOME/.config/systemd/user"
    SERVICE_PATH="$USER_SERVICE_DIR/${SERVICE_NAME}.service"
    install -d -m 0755 "$USER_SERVICE_DIR"
    echo "Writing $SERVICE_PATH..."
    cat > "$SERVICE_PATH" <<EOF
[Unit]
Description=Quasar Space Engineers supervisor
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
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
Environment=HOME=$RUN_HOME
Environment=QUASAR_DATA_DIR=$DATA_DIR
Environment=QUASAR_SYSTEMD_SERVICE=$SERVICE_NAME.service
Environment=QUASAR_SYSTEMD_SCOPE=user

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
}

enable_service() {
    if [[ "$INSTALL_MODE" == "system" ]]; then
        systemctl enable "$SERVICE_NAME.service"
    else
        systemctl --user enable "$SERVICE_NAME.service"
    fi
}

restart_service() {
    if [[ "$INSTALL_MODE" == "system" ]]; then
        systemctl restart "$SERVICE_NAME.service"
    else
        systemctl --user restart "$SERVICE_NAME.service"
    fi
}

copy_if_different() {
    local source="$1"
    local destination="$2"
    if [[ ! -f "$source" ]]; then
        return
    fi

    if [[ "$(realpath -m "$source")" == "$(realpath -m "$destination")" ]]; then
        return
    fi

    cp -a "$source" "$destination"
}

cleanup_old_opt_install() {
    local old_install_dir="/opt/quasar"
    if [[ "$INSTALL_MODE" != "user" || "$INSTALL_DIR" == "$old_install_dir" || ! -x "$old_install_dir/uninstall.sh" ]]; then
        return
    fi

    echo "Old system install found at $old_install_dir."
    local uninstall_args=(--install-dir "$old_install_dir" --purge)
    if grep -q -- "--system" "$old_install_dir/uninstall.sh"; then
        uninstall_args=(--system "${uninstall_args[@]}")
    fi

    if [[ "${EUID}" -eq 0 ]]; then
        "$old_install_dir/uninstall.sh" "${uninstall_args[@]}" || true
    elif have sudo; then
        echo "Removing old system install with sudo..."
        sudo "$old_install_dir/uninstall.sh" "${uninstall_args[@]}" || true
    else
        echo "sudo not found; old system install remains at $old_install_dir." >&2
    fi
}

publish_quasar() {
    local build_version="$1"
    local nuget_version="$2"
    local assembly_file_version="$3"
    local publish_project="$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj"

    if [[ "${EUID}" -eq 0 && "$RUN_USER" != "root" ]]; then
        chown "$RUN_USER:$RUN_GROUP" "$PUBLISH_DIR"
        if have runuser; then
            runuser -u "$RUN_USER" -- \
                env HOME="$RUN_HOME" \
                dotnet publish "$publish_project" \
                    -c "$CONFIGURATION" \
                    -r "$RUNTIME" \
                    -p:Version="$nuget_version" \
                    -p:AssemblyVersion="$assembly_file_version" \
                    -p:FileVersion="$assembly_file_version" \
                    -p:InformationalVersion="$nuget_version" \
                    -o "$PUBLISH_DIR" \
                    -v minimal
            return
        fi

        sudo -u "$RUN_USER" \
            env HOME="$RUN_HOME" \
            dotnet publish "$publish_project" \
                -c "$CONFIGURATION" \
                -r "$RUNTIME" \
                -p:Version="$nuget_version" \
                -p:AssemblyVersion="$assembly_file_version" \
                -p:FileVersion="$assembly_file_version" \
                -p:InformationalVersion="$nuget_version" \
                -o "$PUBLISH_DIR" \
                -v minimal
        return
    fi

    env HOME="$RUN_HOME" \
        dotnet publish "$publish_project" \
            -c "$CONFIGURATION" \
            -r "$RUNTIME" \
            -p:Version="$nuget_version" \
            -p:AssemblyVersion="$assembly_file_version" \
            -p:FileVersion="$assembly_file_version" \
            -p:InformationalVersion="$nuget_version" \
            -o "$PUBLISH_DIR" \
            -v minimal
}

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
    if [[ "$(realpath -m "$SOURCE_DIR")" == "$INSTALL_DIR" ]]; then
        for source_entry in Quasar appsettings.json install.sh uninstall.sh quasar-renice.c README.md WebService; do
            if [[ -e "$SOURCE_DIR/$source_entry" ]]; then
                cp -a -- "$SOURCE_DIR/$source_entry" "$PUBLISH_DIR/"
            fi
        done
    else
        find "$SOURCE_DIR" -mindepth 1 -maxdepth 1 \
            ! -name "quasar-*.tar.gz" \
            ! -name "quasar-*.zip" \
            -exec cp -a -- {} "$PUBLISH_DIR/" \;
    fi
else
    BUILD_VERSION="$(resolve_build_version)"
    NUGET_VERSION="$(normalize_nuget_version "$BUILD_VERSION")"
    ASSEMBLY_FILE_VERSION="$(build_assembly_file_version "$BUILD_VERSION")"
    echo "Publishing Quasar ($CONFIGURATION, $RUNTIME, version $NUGET_VERSION)..."
    publish_quasar "$BUILD_VERSION" "$NUGET_VERSION" "$ASSEMBLY_FILE_VERSION"
fi

echo "Installing Quasar to $INSTALL_DIR..."
if [[ "${EUID}" -eq 0 ]]; then
    install -d -m 0755 -o "$RUN_USER" -g "$RUN_GROUP" "$INSTALL_DIR"
    install -d -m 0755 -o "$RUN_USER" -g "$RUN_GROUP" "$DATA_DIR"
else
    install -d -m 0755 "$INSTALL_DIR"
    install -d -m 0755 "$DATA_DIR"
fi
if [[ "$DATA_DIR" != "$INSTALL_DIR" && "$DATA_DIR" != "$INSTALL_DIR"/* ]]; then
    find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
fi
cp -a "$PUBLISH_DIR/." "$INSTALL_DIR/"
copy_if_different "$SCRIPT_DIR/install.sh" "$INSTALL_DIR/install.sh"
copy_if_different "$SCRIPT_DIR/uninstall.sh" "$INSTALL_DIR/uninstall.sh"
if [[ -f "$SCRIPT_DIR/tools/quasar-renice.c" ]]; then
    copy_if_different "$SCRIPT_DIR/tools/quasar-renice.c" "$INSTALL_DIR/quasar-renice.c"
elif [[ -f "$SCRIPT_DIR/quasar-renice.c" ]]; then
    copy_if_different "$SCRIPT_DIR/quasar-renice.c" "$INSTALL_DIR/quasar-renice.c"
fi
if [[ "${EUID}" -eq 0 ]]; then
    chown -R "$RUN_USER:$RUN_GROUP" "$INSTALL_DIR"
fi
chmod +x "$INSTALL_DIR/Quasar"
if [[ -f "$INSTALL_DIR/install.sh" ]]; then
    chmod +x "$INSTALL_DIR/install.sh"
fi
if [[ -f "$INSTALL_DIR/uninstall.sh" ]]; then
    chmod +x "$INSTALL_DIR/uninstall.sh"
fi
if [[ -f "$INSTALL_DIR/WebService/Quasar" ]]; then
    chmod +x "$INSTALL_DIR/WebService/Quasar"
fi

if [[ "$INSTALL_RENICE_HELPER" == "true" ]]; then
    install_renice_helper
fi

write_service_unit

if [[ "$ENABLE_SERVICE" == "true" ]]; then
    enable_service
fi

cleanup_old_opt_install

if [[ "$START_SERVICE" == "true" ]]; then
    restart_service
fi

cat <<EOF
Installed Quasar.

Service:     $SERVICE_NAME.service ($INSTALL_MODE)
Install dir: $INSTALL_DIR
Data dir:    $DATA_DIR
Run user:    $RUN_USER

Renice helper:
  $RENICE_HELPER_PATH
  Install when needed: $INSTALL_DIR/install.sh --install-renice-helper --no-build --no-enable

Start/restart when ready:
EOF
if [[ "$INSTALL_MODE" == "system" ]]; then
    echo "  sudo systemctl restart $SERVICE_NAME.service"
else
    echo "  systemctl --user restart $SERVICE_NAME.service"
    echo
    echo "To run the user service before login, enable linger once:"
    echo "  sudo loginctl enable-linger $RUN_USER"
fi
