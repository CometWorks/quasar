#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$HOME/Documents/Quasar"
CONFIGURATION="Release"
RUN=false

# Persisted runtime state lives in the Quasar data directory, NOT in the deploy
# directory. It defaults to ~/.config/Quasar (Environment.SpecialFolder.Application-
# Data on Linux) and can be overridden with QUASAR_DATA_DIR. See Magnetar.Protocol/
# Runtime/MagnetarPaths.cs.
DATA_DIR="${QUASAR_DATA_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/Quasar}"

for arg in "$@"; do
    case "$arg" in
        --debug) CONFIGURATION="Debug" ;;
        --run)   RUN=true ;;
    esac
done

# -----------------------------------------------------------------------------
# Stop any Quasar instance still running from a previous deploy.
#
# Without this, `ensure-running` would re-attach to the already-running (old)
# worker via the discovery manifest, and the launcher would respawn the worker
# from the stale active-release pointer even if we only killed the worker. We
# therefore kill the whole tree — launcher and worker alike.
# -----------------------------------------------------------------------------
stop_running_quasar() {
    echo "Stopping any running Quasar instance..."

    # Kill every process whose command line references the deploy directory. This
    # matches both the launcher ("…/Quasar serve") and the worker it spawned
    # ("dotnet …/WebService/Quasar.dll" or "…/WebService/Quasar"), and nothing
    # unrelated, since only Quasar processes are launched from that path.
    pkill -f "$DEPLOY_DIR" 2>/dev/null || true

    # Fallback: kill the process recorded in the discovery manifest, in case an
    # old worker was activated from a location outside the deploy directory.
    local manifest="$DATA_DIR/service-manifest.json"
    if [[ -f "$manifest" ]]; then
        local pid
        pid="$(grep -oE '"processId"[[:space:]]*:[[:space:]]*[0-9]+' "$manifest" \
            | grep -oE '[0-9]+' | tail -n1 || true)"
        if [[ -n "${pid:-}" ]]; then
            kill "$pid" 2>/dev/null || true
        fi
    fi

    # Give the processes a moment to release file handles before we wipe things.
    sleep 1
}

# -----------------------------------------------------------------------------
# Purge persisted state that could pin launches to a former build.
#
#  - Updates/active-release.json : the persisted pointer the launcher reuses
#    rather than re-resolving (EnsureActiveReleasePointerExists). This is the
#    primary staleness mechanism — removing it forces the launcher to rebuild
#    the pointer from the freshly deployed WebService/Quasar binary.
#  - Updates/Staged             : staged update bits that could be activated.
#  - service-manifest.json      : leftover discovery manifest from a hard-killed
#    worker that would otherwise be treated as a healthy running service.
#  - ManagedRuntime/Tools/Magnetar : the cached Magnetar install. Quasar reuses it
#    as soon as Bin/MagnetarInterim exists and never re-downloads, so a stale copy
#    would keep running after a new Magnetar build. Removing it forces a fresh
#    install on the next server launch.
# -----------------------------------------------------------------------------
purge_stale_state() {
    echo "Purging stale Quasar runtime state in $DATA_DIR..."
    rm -rf "$DATA_DIR/Updates"
    rm -f "$DATA_DIR/service-manifest.json"
    rm -rf "$DATA_DIR/ManagedRuntime/Tools/Magnetar"
}

stop_running_quasar
purge_stale_state

echo "Deploying Quasar ($CONFIGURATION) → $DEPLOY_DIR"
rm -rf "$DEPLOY_DIR"
mkdir -p "$DEPLOY_DIR"

dotnet publish "$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" \
    -c "$CONFIGURATION" \
    -r linux-x64 \
    -p:CopyToDeployDir=false \
    -o "$DEPLOY_DIR" \
    -v minimal

chmod +x "$DEPLOY_DIR/Quasar"
chmod +x "$DEPLOY_DIR/WebService/Quasar"

echo "Done. $DEPLOY_DIR"

if [[ "$RUN" == "true" ]]; then
    echo "Starting Quasar..."
    exec "$DEPLOY_DIR/Quasar" ensure-running
fi
