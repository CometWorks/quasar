#!/usr/bin/env python3
"""Native main/updater service integration using an isolated fixture application.
Production updater operation/control/recovery code; local sealed package provider.
No Quasar web host, real game server, remote deployment or GitHub download.
"""
import argparse
import getpass
import json
import os
import pwd
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--system', action='store_true')
options = parser.parse_args()
service_account = os.environ.get('SUDO_USER') or getpass.getuser()
repo = Path(__file__).resolve().parents[2]
project = repo / 'Tests/Updates/UpdateIntegration.csproj'
bin_dir = repo / 'Tests/Updates/bin/Debug/net10.0'
dll = bin_dir / 'Quasar.UpdateIntegration.dll'
subprocess.run(['dotnet', 'build', str(project), '-v', 'quiet'], check=True)
subprocess.run(['dotnet', str(dll), 'self-test'], check=True)
manager = [] if options.system else ['--user']
if subprocess.run(['systemctl', *manager, 'show-environment'], capture_output=True).returncode:
    print('SKIP native systemd: manager unavailable', flush=True)
    raise SystemExit(77)
root = Path(tempfile.mkdtemp(prefix="quasar native % services ' "))
name = 'quasar-update-fixture-' + uuid.uuid4().hex
unit = name + '.service'
updater_unit = name + '-updater.service'
unit_dir = Path('/etc/systemd/system') if options.system else Path.home() / '.config/systemd/user'
env = dict(os.environ, QUASAR_INSTALL_DIR=str(root))
with socket.socket() as listener:
    listener.bind(('127.0.0.1', 0))
    port = listener.getsockname()[1]

def run(*args, check=True):
    result = subprocess.run([str(arg) for arg in args], env=env, capture_output=True, text=True)
    if check and result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    return result

def fixture(*args, check=True):
    prefix = ['runuser', '-u', service_account, '--'] if options.system and service_account != getpass.getuser() else []
    return run(*prefix, 'dotnet', dll, *args, check=check)

def wait(predicate, message, timeout=100):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            if predicate():
                return
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        time.sleep(.1)
    print(run('journalctl', *manager, '-u', unit, '-u', updater_unit, '-n', '35', '--no-pager', check=False).stdout)
    raise AssertionError(message)

def journal():
    return json.loads((root / 'Updates/operation.json').read_text())

def phase():
    return (root / 'Updates/LauncherTransaction/phase').read_text().strip()

def alive():
    os.kill(int((root / 'server.pid').read_text()), 0)
    return True

def request(payload):
    fixture('seal', payload)
    return fixture('request', payload).stdout.strip()

try:
    for source in bin_dir.iterdir():
        if source.is_file():
            shutil.copy2(source, root / source.name)
        elif source.is_dir():
            shutil.copytree(source, root / source.name)
    shutil.copy2(bin_dir / 'Quasar.UpdateIntegration', root / 'Quasar')
    shutil.copytree(bin_dir, root / 'Updater')
    shutil.copy2(bin_dir / 'Quasar.UpdateIntegration', root / 'Updater/Quasar.Updater')
    (root / 'fixture-release.txt').write_text('1.4.1')
    (root / 'release.json').write_text(json.dumps(dict(version='1.4.1', updaterProtocol=1)))
    owned = [str(path.relative_to(root)) for path in root.rglob('*') if path.is_file() and 'Updater' not in path.relative_to(root).parts]
    (root / 'launcher-files.txt').write_text('\n'.join(owned) + '\n')
    (root / 'appsettings.json').write_text(json.dumps({'Quasar': {'Port': port}, 'adminValue': 42}))
    shutil.copy2(repo / 'install.sh', root / 'install.sh')
    # Manual processes cannot satisfy native ownership even with invented flags.
    if options.system:
        account = pwd.getpwnam(service_account)
        for path in [root, *root.rglob('*')]:
            os.chown(path, account.pw_uid, account.pw_gid)
    manual = fixture('probe', check=False)
    assert manual.returncode == 4
    print('PASS manual launch refuses inferred restart capability', flush=True)
    install = [root / 'install.sh', '--repair-supervision', '--install-dir', root, '--service-name', name, '--start']
    if options.system:
        install += ['--system', '--user', service_account]
    run(*install)
    wait(lambda: (root / 'server.pid').exists(), 'Native main startup')
    wait(lambda: json.loads((root / 'Updates/restart-capability.json').read_text())['canRestart'], 'Native service ownership verification', timeout=10) if (root / 'Updates/restart-capability.json').exists() else None
    fixture('verify')
    assert fixture('verify', '--wrong-pid', check=False).returncode == 4
    assert fixture('verify', '--wrong-account', check=False).returncode == 4
    installed_unit = unit_dir / unit
    original_unit = installed_unit.read_text()
    try:
        for invalid_unit in [original_unit.replace('Restart=on-failure', 'Restart=always'),
                             original_unit.replace('serve --service', 'serve --service --invalid-action')]:
            installed_unit.write_text(invalid_unit)
            run('systemctl', *manager, 'daemon-reload')
            assert fixture('verify', check=False).returncode == 4
    finally:
        installed_unit.write_text(original_unit)
        run('systemctl', *manager, 'daemon-reload')
    fixture('verify')
    print('PASS native action, PID ownership, account and restart-policy rejection', flush=True)
    # The real updater identity reads the existing saved credential/keyring.
    fixture('token-create')
    run('systemctl', *manager, 'restart', updater_unit)
    wait(lambda: (root / 'token-reader-ok').exists(), 'Saved-token sharing across native roles')
    before_starts = len((root / 'starts.txt').read_text().splitlines())
    (root / 'fatal-background').write_text('fail')
    wait(lambda: len((root / 'starts.txt').read_text().splitlines()) > before_starts, 'Reported background failure must invoke native recovery')
    assert alive()
    print('PASS saved token/keyring reuse and native reported-failure recovery', flush=True)
    # Queue through the shared durable operation, not a process-spawn shortcut.
    payload = root / 'fixture-payload'
    payload.mkdir()
    if options.system:
        os.chown(payload, account.pw_uid, account.pw_gid)
    for name_in_manifest in (root / 'launcher-files.txt').read_text().splitlines() + ['launcher-files.txt']:
        target = payload / name_in_manifest
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(root / name_in_manifest, target)
    (payload / 'fixture-release.txt').write_text('1.5.0')
    original_executable = (payload / 'Quasar').read_bytes()
    (payload / 'Quasar').write_text('#!/bin/sh\nexit 127\n')
    operation = request(payload)
    assert fixture('request', payload).stdout.strip() == operation
    wait(lambda: journal()['stage'] == 'Failed', 'Failed native launch must record failure')
    wait(lambda: phase() == 'rolled-back' and not (root / 'Updates/application-maintenance').exists(), 'Complete prior application recovery')
    assert alive()
    assert (root / 'Quasar').read_bytes() == original_executable
    print('PASS actual installer services, duplicate requests, failed executable, complete rollback and live server sentinel', flush=True)
    (payload / 'Quasar').write_bytes(original_executable)
    (payload / 'Quasar').chmod(0o755)
    request(payload)
    wait(lambda: journal()['stage'] == 'Healthy', 'Healthy requested version acknowledgement')
    assert journal()['version'] == '1.5.0' and alive()
    print('PASS native updater stops/replaces/restarts main and acknowledges exact healthy release', flush=True)
    # Interrupt the actual updater after file replacement, before health commit.
    (payload / 'fixture-release.txt').write_text('1.6.0')
    (root / 'hold-health').write_text('1.6.0')
    request(payload)
    wait(lambda: phase() == 'attempted' and journal()['stage'] == 'Restarting', 'Replacement reached uncommitted phase')
    run('systemctl', *manager, 'kill', '--signal=KILL', updater_unit)
    (root / 'fixture-release.txt').write_text('partial corruption')
    (root / 'hold-health').unlink()
    wait(lambda: journal()['stage'] == 'Failed' and phase() == 'rolled-back' and not (root / 'Updates/application-maintenance').exists(), 'Updater restart restores interrupted complete installation')
    assert (root / 'fixture-release.txt').read_text().strip() == '1.5.0' and alive()
    print('PASS updater crash/restart recovery restores complete previous release; sentinel survives', flush=True)
    if os.geteuid() != 0:
        root.chmod(0o555)
        try:
            request(payload)
            wait(lambda: journal()['stage'] == 'Failed', 'Insufficient install permissions must fail')
        finally:
            root.chmod(0o700)
        wait(lambda: not (root / 'Updates/application-maintenance').exists(), 'Recovery resumes after permissions repaired')
        assert alive()
        print('PASS real directory permission failure and recovery', flush=True)
    else:
        print('SKIP directory mode denial as root; non-root user-service job covers it', flush=True)
    before = (root / 'starts.txt').read_text()
    (root / 'exit').write_text('0')
    wait(lambda: run('systemctl', *manager, 'is-active', unit, check=False).returncode != 0, 'Intentional shutdown')
    time.sleep(2)
    assert (root / 'starts.txt').read_text() == before and alive()
    print('PASS intentional main shutdown remains stopped; detached server sentinel stays alive', flush=True)
finally:
    for service in [updater_unit, unit]:
        run('systemctl', *manager, 'stop', service, check=False)
        run('systemctl', *manager, 'reset-failed', service, check=False)
    for service in [unit, updater_unit, name + '-clusters.service']:
        (unit_dir / service).unlink(missing_ok=True)
    if options.system:
        (Path('/etc/quasar') / (name + '.json')).unlink(missing_ok=True)
        (Path('/etc/polkit-1/rules.d') / ('60-quasar-' + name + '.rules')).unlink(missing_ok=True)
    run('systemctl', *manager, 'daemon-reload', check=False)
    if (root / 'server.pid').exists():
        try:
            os.kill(int((root / 'server.pid').read_text()), signal.SIGKILL)
        except ProcessLookupError:
            pass
    shutil.rmtree(root)
