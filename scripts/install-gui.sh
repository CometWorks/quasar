#!/usr/bin/env bash
# Graphical front end only; install.sh owns dependencies, files and systemd.
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if command -v kdialog >/dev/null 2>&1; then dialog=kdialog
elif command -v zenity >/dev/null 2>&1; then dialog=zenity
else
    echo 'Graphical installation needs kdialog or zenity. Manual installation: ./install.sh --start (user services), or sudo ./install.sh --system --user <account> --start.' >&2
    if command -v x-terminal-emulator >/dev/null 2>&1; then
        exec x-terminal-emulator -e /bin/bash -c 'printf "%s\n" "Graphical installation needs kdialog or zenity. Use ./install.sh --help for manual options."; read -r -p "Press Enter to close."'
    fi
    exit 3
fi
message() {
    local kind="$1" text="$2"
    if [[ "$dialog" == kdialog ]]; then "$dialog" "--$kind" "$text" --title 'Install Quasar'
    elif [[ "$kind" == error ]]; then "$dialog" --error --no-markup --text="$text" --title='Install Quasar' --width=560
    else "$dialog" --info --no-markup --text="$text" --title='Install Quasar' --width=560; fi
}
manual() {
    message msgbox $'From the extracted package:\n\n./install.sh --start\nInstalls user services without administrator privileges when dependencies are present.\n\nsudo ./install.sh --system --user <account> --install-dir <folder> --start\nInstalls system services.\n\n./install.sh --help lists custom paths, accounts, scope and repair options. See INSTALL.md for migration.\n\nFor a portable session: ./Quasar serve --foreground'
}
trap 'status=$?; trap - ERR; message error "Installer could not continue (exit $status). Use ./install.sh --help for manual installation."; exit "$status"' ERR
if [[ "${1:-}" == --manual ]]; then manual; exit 0; fi
if [[ ! -f "$script_dir/launcher-files.txt" || ! -x "$script_dir/Updater/Quasar.Updater" ]]; then
    message error 'Extract the complete Linux platform package first, then launch Install.desktop from its Quasar folder.'; exit 3
fi
if [[ "$EUID" -eq 0 ]]; then message error 'Launch the guided installer as your desktop user. It requests administrator approval only when needed.'; exit 3; fi
account="$(id -un)"
install_dir="$HOME/.local/share/Quasar"
mode=system
service=quasar
# An installer opened in an existing installation must preserve its identity.
metadata="$install_dir/service-installation.json"
if [[ -f "$script_dir/service-installation.json" ]]; then metadata="$script_dir/service-installation.json"; fi
if [[ -f "$metadata" ]]; then
    if ! command -v python3 >/dev/null 2>&1; then message error 'Reading existing service metadata needs python3 for the graphical installer. Use manual install to preserve the existing scope, path, service name and account.'; exit 3; fi
    # NUL-delimited output preserves whitespace and metacharacters as data.
    metadata_options="$(mktemp)"
    trap 'rm -f -- "$metadata_options"' EXIT
    if ! python3 - "$metadata" >"$metadata_options" <<'PY'
import json,sys
from pathlib import Path
path=Path(sys.argv[1]); value=json.loads(path.read_text())
if 'installDirectory' not in value: value=json.loads(Path(value['metadataPath']).read_text())
for item in [value['installDirectory'],value['account'],value['mainService'].removesuffix('.service'),'user' if value['userService'] else 'system']:
    sys.stdout.buffer.write(item.encode()+b'\0')
PY
    then message error 'Cannot read existing service metadata. Use manual install with the existing path, account, service name and scope.'; exit 3; fi
    mapfile -d '' -t options <"$metadata_options"
    install_dir="${options[0]}"; account="${options[1]}"; service="${options[2]}"; mode="${options[3]}"
    rm -f -- "$metadata_options"; trap - EXIT
    if [[ "$account" != "$(id -un)" ]]; then message error 'This installation uses another execution account. Use manual installation to preserve that account and its data permissions.'; exit 3; fi
fi
text="Install Quasar and its updater as $mode systemd services.

Application and data: $install_dir
Execution account: $account
Service: $service

System installation requests administrator approval through polkit. Required runtime packages may be installed. Manual install offers custom paths and service options."
if [[ "$dialog" == kdialog ]]; then
    if "$dialog" --yesnocancel "$text" --yes-label Install --no-label 'Manual install' --title 'Install Quasar'; then choice=0; else choice=$?; fi
else
    if answer="$("$dialog" --question --no-markup --text="$text" --ok-label=Install --cancel-label=Cancel --extra-button='Manual install' --title='Install Quasar' --width=560)"; then choice=0; else choice=$?; fi
    if [[ "$answer" == 'Manual install' ]]; then choice=1; elif (( choice != 0 )); then choice=2; fi
fi
case "$choice" in 0) ;; 1) manual; exit 0;; *) exit 0;; esac
arguments=(/bin/bash "$script_dir/install.sh" --no-build --install-dir "$install_dir" --service-name "$service" --start)
if [[ "$mode" == system ]]; then
    if ! command -v pkexec >/dev/null 2>&1; then message error 'polkit (pkexec) is unavailable. Use manual installation with sudo or install a polkit authentication agent.'; exit 3; fi
    arguments=(pkexec "${arguments[@]}" --system --user "$account" --install-dependencies --yes)
fi
log_dir="${XDG_STATE_HOME:-$HOME/.local/state}/Quasar"
mkdir -p -- "$log_dir"
log="$(mktemp "$log_dir/install-XXXXXXXX.log")"
if "${arguments[@]}" >"$log" 2>&1; then
    # Read the configured address, including custom ports, without exposing credentials.
    if url="$(python3 - "$install_dir/service-installation.json" 2>>"$log" <<'PY'
import json, sys, time, urllib.parse, urllib.request
from pathlib import Path
path = Path(sys.argv[1])
value = json.loads(path.read_text())
if 'installDirectory' not in value:
    value = json.loads(Path(value['metadataPath']).read_text())
url = value['healthUrl'].rstrip('/')
if urllib.parse.urlparse(url).scheme not in ('http', 'https'):
    raise ValueError('Invalid Quasar address')
print(url, flush=True)
deadline = time.monotonic() + 90
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
while True:
    try:
        with opener.open(url + '/api/health', timeout=2) as response:
            if json.load(response).get('status') == 'ok':
                break
    except (OSError, ValueError):
        pass
    if time.monotonic() >= deadline:
        sys.exit('Quasar did not become ready within 90 seconds')
    time.sleep(.5)
PY
    )"; then
        if { command -v xdg-open >/dev/null 2>&1 && xdg-open "$url" >>"$log" 2>&1; } ||
           { command -v gio >/dev/null 2>&1 && gio open "$url" >>"$log" 2>&1; }; then
            browser_message="Opening Quasar in your browser: $url"
        else
            browser_message="Could not open the browser. Open Quasar manually: $url"
        fi
    else
        browser_message="Could not open Quasar automatically. Check the installation log and open the configured address manually${url:+: $url}."
    fi
    message msgbox "Quasar and its updater are installed. $browser_message

Installation log: $log"
else
    code=$?
    if (( code == 126 )); then exit 0; fi # polkit cancellation.
    message error "Installation did not complete (exit $code). No success was reported.

Read the installation log: $log

Use Manual install for custom accounts, scope or migration."
    exit "$code"
fi
