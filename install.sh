#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

SERVICE_NAME="quasar"
INSTALL_DIR=""
PACKAGED_INSTALL=false
CONFIGURATION="Release"
RUNTIME="linux-x64"
ENABLE_SERVICE=true
START_SERVICE=false
SKIP_BUILD=false
REPAIR_SUPERVISION=false
INSTALL_MODE="user"
INSTALL_RENICE_HELPER=false
INSTALL_DEPENDENCIES=false
ASSUME_YES=false
RENICE_HELPER_PATH="/usr/local/bin/quasar-renice"
RUN_USER="${USER:-}"
RUN_USER_EXPLICIT=false
REQUIRED_DOTNET_MAJOR=10

usage() {
    cat <<EOF
Usage: ./install.sh [options]

Publishes Quasar and installs a systemd user service by default. Extracted
release installers install in place by default and keep Quasar state beside the
launcher. Next to the service it installs <service-name>-clusters.service, which
saves and stops the clusters on this PC at logout or shutdown.

For machine-wide service installation, pass --system and run with sudo.

If the required .NET 10 SDK/runtime is missing, the installer detects apt, dnf,
yum, pacman, or zypper, prints the exact install commands, and prompts before
running them. On Debian 13, apt installs first add Microsoft's Debian 13 package
feed. Packaged installs need only the runtime. When an administrator later
installs a source-built UI plugin, Quasar prefers a suitable SDK on PATH and can
otherwise download a pinned private SDK into its managed data directory after
confirmation. The administrator can cancel and install the SDK manually instead.

Options:
  --install-dir <dir>       Install directory (default: extracted installer root; source installs use <run-user-home>/.local/share/Quasar)
  --service-name <name>     systemd service name (default: quasar)
  --user <user>             User to run Quasar as (system installs only; default: sudo caller)
  --user-service            Install a user systemd service (default)
  --system                  Install a system service under /etc/systemd/system
  --install-renice-helper   Build and install /usr/local/bin/quasar-renice as setuid root
  --install-dependencies    Install CA certificates, SteamCMD libraries and polkit using the system package manager
  --configuration <name>    Build configuration (default: Release)
  --runtime <rid>           Publish runtime identifier (default: linux-x64)
  --no-enable               Do not enable the service at boot
  --start                   Restart/start the service after installing
  --repair-supervision      Repair only supervisor wiring; preserve installed files/config/data
  --no-build                Install from an existing publish directory
  --yes                     Run package install commands without confirmation
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
        if [[ "$ASSUME_YES" == "true" ]]; then
            printf 'sudo -n '
        else
            printf 'sudo '
        fi
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
    if [[ "$ASSUME_YES" == "true" ]]; then
        echo "Running commands without confirmation because --yes was passed."
    else
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
    fi

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

install_download_dependencies() {
    local manager commands=() prefix
    manager="$(detect_package_manager)"
    prefix="$(privileged_prefix)"
    case "$manager" in
        apt) commands=("${prefix}apt-get update" "${prefix}apt-get install -y ca-certificates lib32gcc-s1 polkitd") ;;
        dnf|yum) commands=("${prefix}$manager install -y ca-certificates glibc.i686 libgcc.i686 polkit") ;;
        pacman) commands=("${prefix}pacman -S --needed --noconfirm ca-certificates lib32-glibc lib32-gcc-libs polkit") ;;
        zypper) commands=("${prefix}zypper --non-interactive install ca-certificates glibc-32bit libgcc_s1-32bit polkit") ;;
        *) echo 'No supported package manager. Install CA certificates, SteamCMD 32-bit libraries and polkit manually.' >&2; exit 1 ;;
    esac
    echo 'Required download and service authorization packages:'
    printf '  %s\n' "${commands[@]}"
    if [[ "$ASSUME_YES" != true ]]; then
        [[ -t 0 ]] || { echo 'Use --yes to approve dependency installation in a non-interactive session.' >&2; exit 1; }
        local answer
        read -r -p 'Run these commands now? [y/N] ' answer
        [[ "$answer" =~ ^[Yy]([Ee][Ss])?$ ]] || { echo 'Dependency installation cancelled.' >&2; exit 1; }
    fi
    local command
    for command in "${commands[@]}"; do bash -c "$command"; done
}

warn_missing_download_packages() {
    # Quasar, Magnetar and SteamCMD download over HTTPS. SteamCMD's linux32/steamcmd is 32-bit
    # and exits 127 without the 32-bit loader. Without them Quasar can't download the Dedicated Server.
    local missing=() packages=()
    if ! compgen -G '/etc/ssl/certs/*' >/dev/null; then
        missing+=("CA certificates for HTTPS downloads (nothing in /etc/ssl/certs)")
        packages+=(ca-certificates)
    fi
    if [[ ! -e /lib/ld-linux.so.2 ]]; then
        missing+=("the 32-bit loader for SteamCMD (/lib/ld-linux.so.2 is missing)")
        packages+=(lib32gcc-s1)
    fi
    (( ${#packages[@]} )) || return 0

    echo "Warning: Quasar can't download or update the Dedicated Server on this host. It lacks:" >&2
    printf '  %s\n' "${missing[@]}" >&2
    echo "Install them before the first start, on Debian and Ubuntu: sudo apt install ${packages[*]}" >&2
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
        --install-dependencies)
            INSTALL_DEPENDENCIES=true
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
        --repair-supervision)
            REPAIR_SUPERVISION=true
            ENABLE_SERVICE=false
            shift
            ;;
        --no-build)
            SKIP_BUILD=true
            shift
            ;;
        --yes|-y)
            ASSUME_YES=true
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

[[ "$SERVICE_NAME" =~ ^[A-Za-z0-9_.@-]+$ ]] || { echo "Invalid service name" >&2; exit 2; }
if [[ -x "$SCRIPT_DIR/Quasar" && ! -f "$SCRIPT_DIR/Quasar/Quasar.csproj" ]]; then
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
INSTALL_DIR="$(realpath -m "$INSTALL_DIR")"
[[ "$INSTALL_DIR" != *$'\n'* && "$INSTALL_DIR" != *$'\r'* ]] || { echo "Install path cannot contain line breaks" >&2; exit 2; }

case "$INSTALL_DIR" in
    ""|"/"|"/bin"|"/boot"|"/dev"|"/etc"|"/home"|"/lib"|"/lib64"|"/proc"|"/root"|"/run"|"/sbin"|"/sys"|"/tmp"|"/usr"|"/var")
        echo "Refusing unsafe install directory: $INSTALL_DIR" >&2
        exit 1
        ;;
esac

if [[ "$INSTALL_DEPENDENCIES" == true ]]; then install_download_dependencies; fi
require_dotnet_installation
warn_missing_download_packages

CLUSTERS_UNIT="${SERVICE_NAME}-clusters.service"

systemd_value() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//%/%%}"
    printf '%s' "$value"
}

write_service_unit() {
    local unit_install unit_metadata
    unit_install="$(systemd_value "$INSTALL_DIR")"
    local scope_dir main_account updater_account updater_user metadata_path
    if [[ "$INSTALL_MODE" == "system" ]]; then
        scope_dir=/etc/systemd/system
        main_account="User=$RUN_USER
Group=$RUN_GROUP"
        updater_account="User=$RUN_USER
Group=$RUN_GROUP"
        updater_user="$RUN_USER"
        metadata_path="/etc/quasar/$SERVICE_NAME.json"
        install -d -m 0755 /etc/quasar
        if [[ ! -d /etc/polkit-1/rules.d ]]; then
            echo "System updater control needs polkit. Install polkit, then rerun repair-supervision." >&2
            exit 1
        fi
        [[ "$SERVICE_NAME" =~ ^[A-Za-z0-9_.@-]+$ && "$RUN_USER" =~ ^[A-Za-z0-9_.@-]+$ ]] || { echo "Unsupported service/account name" >&2; exit 2; }
        cat > "/etc/polkit-1/rules.d/60-quasar-$SERVICE_NAME.rules" <<EOF
polkit.addRule(function(action, subject) {
    if (action.id == "org.freedesktop.systemd1.manage-units" && subject.user == "$RUN_USER" &&
        action.lookup("unit") == "$SERVICE_NAME.service" &&
        ["start", "stop", "restart"].indexOf(action.lookup("verb")) >= 0) return polkit.Result.YES;
});
EOF
        chmod 0644 "/etc/polkit-1/rules.d/60-quasar-$SERVICE_NAME.rules"
    else
        scope_dir="$RUN_HOME/.config/systemd/user"
        main_account=""
        updater_account=""
        updater_user="$RUN_USER"
        metadata_path="$INSTALL_DIR/service-installation.json"
        install -d -m 0755 "$scope_dir"
    fi
    unit_metadata="$(systemd_value "$metadata_path")"
    local user_flag=()
    if [[ "$INSTALL_MODE" == "user" ]]; then user_flag=(--user-service); fi
    "$INSTALL_DIR/Updater/Quasar.Updater" configure --install-dir "$INSTALL_DIR" --data-dir "$INSTALL_DIR" \
        --metadata "$metadata_path" --main-service "$SERVICE_NAME.service" --updater-service "$SERVICE_NAME-updater.service" \
        --account "$RUN_USER" --updater-account "$updater_user" "${user_flag[@]}"
    if [[ "$INSTALL_MODE" == "system" ]]; then
        chown "$RUN_USER:$RUN_GROUP" "$metadata_path" "$INSTALL_DIR/service-installation.json"
        chmod 0600 "$metadata_path" "$INSTALL_DIR/service-installation.json"
    fi
    cat > "$scope_dir/$SERVICE_NAME.service" <<EOF
[Unit]
Description=Quasar Space Engineers supervisor
Wants=network-online.target $SERVICE_NAME-updater.service
After=network-online.target

[Service]
Type=simple
$main_account
WorkingDirectory=$unit_install
ExecStart=/usr/bin/env "$unit_install/Quasar" serve --service
Restart=on-failure
RestartSec=5
KillMode=process
TimeoutStopSec=120
Environment="QUASAR_INSTALL_DIR=$unit_install"
Environment="QUASAR_HOST_STOP_UNIT=$scope_dir/$CLUSTERS_UNIT"

[Install]
WantedBy=$([[ "$INSTALL_MODE" == "system" ]] && echo multi-user.target || echo default.target)
EOF
    cat > "$scope_dir/$SERVICE_NAME-updater.service" <<EOF
[Unit]
Description=Quasar application updater
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
$updater_account
WorkingDirectory=$unit_install
ExecStart=/usr/bin/env "$unit_install/Updater/Quasar.Updater" --installation "$unit_metadata"
Restart=on-failure
RestartSec=5
TimeoutStopSec=120

[Install]
WantedBy=$([[ "$INSTALL_MODE" == "system" ]] && echo multi-user.target || echo default.target)
EOF
    write_clusters_unit "$scope_dir/$CLUSTERS_UNIT" "$([[ "$INSTALL_MODE" == "system" ]] && echo multi-user.target || echo default.target)" "$main_account"
    if [[ "$INSTALL_MODE" == "system" ]]; then systemctl daemon-reload; else systemctl --user daemon-reload; fi
}

# Quasar runs the local Host executor itself, and a Quasar stop or restart leaves its running
# clusters alone. This unit saves and stops them at logout or shutdown instead: systemd stops
# it before Quasar's service, while the Host still runs. Keep TimeoutStopSec in sync with
# HostShutdown.StopTimeoutSeconds in Quasar.Host.
write_clusters_unit() {
    local path="$1" wanted_by="$2" account="$3" unit_install
    unit_install="$(systemd_value "$INSTALL_DIR")"
    echo "Writing $path..."
    cat > "$path" <<EOF
[Unit]
Description=Save and stop the clusters on this PC at logout or shutdown ($SERVICE_NAME)
After=$SERVICE_NAME.service

[Service]
Type=oneshot
RemainAfterExit=yes
${account:+$account
}ExecStart=/bin/true
ExecStop=/bin/sh -c 'test ! -x "\$1" || exec "\$1"' sh "$unit_install/LocalHost/stop-clusters"
TimeoutStopSec=110

[Install]
WantedBy=$wanted_by
EOF
}

enable_service() {
    if [[ "$INSTALL_MODE" == "system" ]]; then
        systemctl enable "$SERVICE_NAME.service" "$SERVICE_NAME-updater.service"
        systemctl enable --now "$CLUSTERS_UNIT"
    else
        systemctl --user enable "$SERVICE_NAME.service" "$SERVICE_NAME-updater.service"
        systemctl --user enable --now "$CLUSTERS_UNIT"
    fi
}

restart_service() {
    if [[ "$INSTALL_MODE" == "system" ]]; then
        systemctl restart "$SERVICE_NAME-updater.service"
        systemctl restart "$SERVICE_NAME.service"
    else
        systemctl --user restart "$SERVICE_NAME-updater.service"
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

publish_quasar() {
    local build_version="$1"
    local nuget_version="$2"
    local assembly_file_version="$3"
    local publish_project="$SCRIPT_DIR/Quasar/Quasar.csproj"

    if [[ "${EUID}" -eq 0 && "$RUN_USER" != "root" ]]; then
        chown "$RUN_USER:$RUN_GROUP" "$PUBLISH_DIR"
        if have runuser; then
            runuser -u "$RUN_USER" -- \
                env HOME="$RUN_HOME" \
                dotnet publish "$publish_project" \
                    -c "$CONFIGURATION" \
                    -r "$RUNTIME" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
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
                -r "$RUNTIME" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
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
            -r "$RUNTIME" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:Version="$nuget_version" \
            -p:AssemblyVersion="$assembly_file_version" \
            -p:FileVersion="$assembly_file_version" \
            -p:InformationalVersion="$nuget_version" \
            -o "$PUBLISH_DIR" \
            -v minimal
}

if [[ "$REPAIR_SUPERVISION" == "true" && "$INSTALL_MODE" == "system" && "$RUN_USER_EXPLICIT" == "false" ]]; then
    previous_user="$(systemctl show "$SERVICE_NAME.service" --property=User --value)"
    if [[ -n "$previous_user" ]]; then
        echo "Repairing a system service requires --user $previous_user to preserve its execution account." >&2
        exit 2
    fi
fi

# Repair mode migrates native supervision without replacing binaries or user data.
if [[ "$REPAIR_SUPERVISION" == "true" ]]; then
    [[ -x "$INSTALL_DIR/Quasar" && -x "$INSTALL_DIR/Updater/Quasar.Updater" && -f "$INSTALL_DIR/launcher-files.txt" ]] || { echo "Replace the complete current package before repairing native services." >&2; exit 1; }
    "$INSTALL_DIR/Updater/Quasar.Updater" check-upgrade-path --package "$INSTALL_DIR" --data-dir "$INSTALL_DIR"
    write_service_unit
    if [[ "$START_SERVICE" == "true" ]]; then restart_service; fi
    echo "Native services repaired; application configuration and data retained."
    exit 0
fi

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
        if [[ ! -f "$SOURCE_DIR/launcher-files.txt" ]]; then echo "Use the complete current platform package." >&2; exit 1; fi
        while IFS= read -r source_entry; do
            [[ "$source_entry" != /* && "/$source_entry/" != */../* ]] || exit 2
            mkdir -p "$PUBLISH_DIR/$(dirname "$source_entry")"
            cp -a -- "$SOURCE_DIR/$source_entry" "$PUBLISH_DIR/$source_entry"
        done < "$SOURCE_DIR/launcher-files.txt"
        cp -a "$SOURCE_DIR/Updater" "$SOURCE_DIR/launcher-files.txt" "$PUBLISH_DIR/"
        if [[ -f "$SOURCE_DIR/appsettings.json" ]]; then cp -a "$SOURCE_DIR/appsettings.json" "$PUBLISH_DIR/"; fi
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

if [[ "$SKIP_BUILD" != "true" ]]; then
    for component in Quasar.Updater Quasar.Host; do
        component_dir=Updater
        if [[ "$component" == Quasar.Host ]]; then component_dir=Host; fi
        dotnet publish "$SCRIPT_DIR/$component/$component.csproj" -c "$CONFIGURATION" -r "$RUNTIME" \
            -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:Version="$NUGET_VERSION" -o "$PUBLISH_DIR/$component_dir" -v minimal
    done
fi
if [[ "$SKIP_BUILD" != true ]]; then
    printf '{"version":"%s","updaterProtocol":1}\n' "$NUGET_VERSION" > "$PUBLISH_DIR/release.json"
fi
[[ -x "$PUBLISH_DIR/Updater/Quasar.Updater" ]] || { echo "Use a complete unified package containing Updater/Quasar.Updater." >&2; exit 1; }
"$PUBLISH_DIR/Updater/Quasar.Updater" check-upgrade-path --package "$PUBLISH_DIR" --data-dir "$INSTALL_DIR"
"$PUBLISH_DIR/Updater/Quasar.Updater" check-stopped --root "$INSTALL_DIR" --data-dir "$INSTALL_DIR"
echo "Installing Quasar to $INSTALL_DIR..."
if [[ "${EUID}" -eq 0 ]]; then
    install -d -m 0755 -o "$RUN_USER" -g "$RUN_GROUP" "$INSTALL_DIR"
else
    install -d -m 0755 "$INSTALL_DIR"
fi
if [[ -f "$INSTALL_DIR/appsettings.json" ]]; then
    rm -f -- "$PUBLISH_DIR/appsettings.json"
fi
cp -a "$PUBLISH_DIR/." "$INSTALL_DIR/"
copy_if_different "$SCRIPT_DIR/install.sh" "$INSTALL_DIR/install.sh"
copy_if_different "$SCRIPT_DIR/uninstall.sh" "$INSTALL_DIR/uninstall.sh"
if [[ -f "$SCRIPT_DIR/tools/quasar-renice.c" ]]; then
    copy_if_different "$SCRIPT_DIR/tools/quasar-renice.c" "$INSTALL_DIR/quasar-renice.c"
elif [[ -f "$SCRIPT_DIR/quasar-renice.c" ]]; then
    copy_if_different "$SCRIPT_DIR/quasar-renice.c" "$INSTALL_DIR/quasar-renice.c"
fi
# Track the complete application tree, excluding updater, defaults and mutable data.
if [[ ! -f "$PUBLISH_DIR/launcher-files.txt" ]]; then
    (cd "$PUBLISH_DIR"; find . -type f ! -path './Updater/*' ! -name appsettings.json ! -name service-installation.json ! -name launcher-files.txt -printf '%P\n' | LC_ALL=C sort) > "$INSTALL_DIR/launcher-files.txt"
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

if [[ "$START_SERVICE" == "true" ]]; then
    restart_service
fi

cat <<EOF
Installed Quasar.

Service:     $SERVICE_NAME.service ($INSTALL_MODE)
Clusters:    $CLUSTERS_UNIT saves the clusters on this PC at logout or shutdown
Install dir: $INSTALL_DIR
Run user:    $RUN_USER

Renice helper:
  $RENICE_HELPER_PATH
  Install when needed: $INSTALL_DIR/install.sh --install-renice-helper --no-build --no-enable

UI plugin SDK:
  No SDK is downloaded by this installer for packaged installs. When an admin
  first installs a source-built UI plugin, Quasar uses a .NET 10 SDK on PATH or
  offers a confirmed private download into
  $INSTALL_DIR/ManagedRuntime/Tools/DotNetSdk.
  The admin can cancel and install .NET 10 with the system package manager.

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
echo
echo "UI Shutdown Quasar drains the worker; run the restart command above to bring it back."
