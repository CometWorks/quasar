#!/usr/bin/env python3
"""Guided installer routing with fake dialogs/elevation. No native installs or GUI."""
import getpass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time
import unittest

REPO = Path(__file__).resolve().parents[2]
GIO_PYTHON = '/usr/bin/python3'
GIO_AVAILABLE = Path(GIO_PYTHON).is_file() and subprocess.run(
    [GIO_PYTHON, '-c', 'from gi.repository import Gio'], capture_output=True).returncode == 0

@unittest.skipIf(os.geteuid() == 0, 'Desktop installer requires an unelevated user')
class GuidedInstaller(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="quasar gui % ' ")
        self.root = Path(self.temp.name)
        self.package = self.root / "package % ' shell; $literal"
        self.package.mkdir()
        self.home = self.root / 'home'
        self.home.mkdir()
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        shutil.copy2(REPO / 'scripts/install-gui.sh', self.package)
        (self.package / 'launcher-files.txt').write_text('Quasar\n')
        (self.package / 'Updater').mkdir()
        self.executable(self.package / 'Updater/Quasar.Updater', '#!/bin/sh\nexit 0\n')
        self.executable(self.package / 'install.sh', '#!/bin/bash\nexec /usr/bin/python3 - "$@" <<\'PY\'\nimport json,os,sys\nfrom pathlib import Path\nPath(os.environ["CALLS"]).write_text(json.dumps(sys.argv))\nraise SystemExit(int(os.environ.get("BACKEND_EXIT","0")))\nPY\n')
        self.executable(self.bin / 'kdialog', '#!/usr/bin/python3\nimport json,os,sys\nwith open(os.environ["DIALOGS"],"a") as f: f.write(json.dumps(sys.argv)+"\\n")\nraise SystemExit(int(os.environ.get("CHOICE","0")) if "--yesnocancel" in sys.argv else 0)\n')
        self.executable(self.bin / 'pkexec', '#!/usr/bin/python3\nimport json,os,sys\nfrom pathlib import Path\nPath(os.environ["CALLS"]).write_text(json.dumps(sys.argv))\nraise SystemExit(int(os.environ.get("BACKEND_EXIT","0")))\n')
        # Browser commands stay inert even when a desktop is available on the test machine.
        self.executable(self.bin / 'xdg-open', '#!/usr/bin/python3\nimport os,sys\nfrom pathlib import Path\nPath(os.environ["BROWSER_CALL"]).write_text(sys.argv[1])\nraise SystemExit(int(os.environ.get("BROWSER_EXIT","0")))\n')
        self.executable(self.bin / 'gio', '#!/bin/sh\nexit 1\n')
        self.env = dict(os.environ, HOME=str(self.home), PATH=str(self.bin)+os.pathsep+os.defpath,
                        CALLS=str(self.root / 'calls'), DIALOGS=str(self.root / 'dialogs'),
                        BROWSER_CALL=str(self.root / 'browser'),
                        XDG_STATE_HOME=str(self.root / 'state'))

    def tearDown(self):
        self.temp.cleanup()

    def executable(self, path, content):
        path.write_text(content); path.chmod(0o755)

    def run_gui(self, **options):
        return subprocess.run(['/bin/bash', str(self.package / 'install-gui.sh')], env=dict(self.env, **options), capture_output=True, text=True, timeout=10)

    @unittest.skipUnless(GIO_AVAILABLE, 'GIO Python desktop-file API unavailable')
    def test_desktop_entry_resolves_its_extracted_package(self):
        desktop = self.package / 'Install.desktop'
        shutil.copy2(REPO / 'scripts/Install.desktop', desktop)
        original = desktop.read_text()
        # Test file-manager launch semantics with both %k and the launch environment.
        # Older `gio launch` loads an anonymous key file and loses both locations.
        for entry in [original, original.replace('quasar-install %k', 'quasar-install')]:
            desktop.write_text(entry)
            (self.root / 'dialogs').unlink(missing_ok=True)
            result = subprocess.run([GIO_PYTHON, '-c',
                'import sys; from gi.repository import Gio; Gio.DesktopAppInfo.new_from_filename(sys.argv[1]).launch([], None)',
                str(desktop)], env=dict(self.env, CHOICE='2'), capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            deadline = time.monotonic()+5
            while not (self.root / 'dialogs').exists() and time.monotonic() < deadline:
                time.sleep(.05)
            self.assertTrue((self.root / 'dialogs').exists(), result.stderr)
            self.assertFalse((self.root / 'calls').exists())

    def test_native_elevation_preserves_literal_paths_and_user(self):
        result = self.run_gui()
        self.assertEqual(result.returncode, 0, result.stderr)
        args = json.loads((self.root / 'calls').read_text())
        self.assertEqual(args[1:3], ['/bin/bash', str(self.package / 'install.sh')])
        self.assertEqual(args[args.index('--user')+1], getpass.getuser())
        self.assertEqual(args[args.index('--install-dir')+1], str(self.home / '.local/share/Quasar'))
        self.assertIn('--system', args)
        self.assertIn('--install-dependencies', args)
        self.assertIn('--yes', args)

    def test_manual_and_cancel_never_elevate(self):
        for choice in ['1','2']:
            self.assertEqual(self.run_gui(CHOICE=choice).returncode, 0)
            self.assertFalse((self.root / 'calls').exists())
            self.assertFalse((self.root / 'browser').exists())

    def test_success_opens_configured_address_after_health_check(self):
        calls = []
        browser = self.root / 'browser'
        class Health(BaseHTTPRequestHandler):
            def do_GET(self):
                calls.append(self.path)
                assert not browser.exists(), 'Browser opened before readiness check'
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{"status":"ok"}')

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(('127.0.0.1', 0), Health) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                url = f'http://127.0.0.1:{server.server_port}'
                installed = self.home / '.local/share/Quasar'
                installed.mkdir(parents=True)
                protected = self.root / 'metadata.json'
                protected.write_text(json.dumps(dict(installDirectory=str(installed), healthUrl=url,
                    account=getpass.getuser(), mainService='quasar.service', userService=False)))
                (installed / 'service-installation.json').write_text(json.dumps(dict(metadataPath=str(protected))))
                result = self.run_gui()
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(calls, ['/api/health'])
                self.assertEqual(browser.read_text(), url + '/setup')
                self.assertIn('Opening Quasar in your browser', (self.root / 'dialogs').read_text())
                browser.unlink()
                result = self.run_gui(BROWSER_EXIT='1')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('Could not open the browser', (self.root / 'dialogs').read_text())
            finally:
                server.shutdown()
                thread.join()

    def test_zenity_manual_and_cancel_choices_remain_unelevated(self):
        (self.bin / 'kdialog').unlink()
        # Keep the real desktop dialogs out of PATH; only these inert fixtures run.
        for utility in ['dirname','id','mkdir','mktemp','python3','rm']:
            (self.bin / utility).symlink_to(shutil.which(utility))
        self.env['PATH'] = str(self.bin)
        self.executable(self.bin / 'zenity', '#!/usr/bin/python3\nimport os,sys\nif "--question" in sys.argv:\n choice=os.environ.get("CHOICE","0")\n if choice=="1": print("Manual install")\n raise SystemExit(0 if choice=="0" else 1)\nraise SystemExit(0)\n')
        for choice in ['1','2']:
            self.assertEqual(self.run_gui(CHOICE=choice).returncode, 0)
            self.assertFalse((self.root / 'calls').exists())
        self.assertEqual(self.run_gui(CHOICE='0').returncode, 0)
        self.assertTrue((self.root / 'calls').exists())

    def test_elevation_failure_never_reports_success(self):
        result = self.run_gui(BACKEND_EXIT='23')
        self.assertEqual(result.returncode, 23)
        dialogs = (self.root / 'dialogs').read_text()
        self.assertIn('Installation did not complete', dialogs)
        self.assertNotIn('are installed.', dialogs)
        self.assertFalse((self.root / 'browser').exists())
        self.assertEqual(len(list((self.root / 'state/Quasar').glob('install-*.log'))), 1)

    def test_polkit_cancel_never_reports_success(self):
        self.assertEqual(self.run_gui(BACKEND_EXIT='126').returncode, 0)
        self.assertNotIn('are installed.', (self.root / 'dialogs').read_text())
        self.assertFalse((self.root / 'browser').exists())

    def test_existing_user_scope_path_and_name_survive(self):
        protected = self.root / 'metadata.json'
        protected.write_text(json.dumps(dict(installDirectory=str(self.root / "existing ' % data"), account=getpass.getuser(), mainService='custom-quasar.service', userService=True)))
        (self.package / 'service-installation.json').write_text(json.dumps(dict(metadataPath=str(protected))))
        self.assertEqual(self.run_gui().returncode, 0)
        args = json.loads((self.root / 'calls').read_text())
        self.assertEqual(args[0], '-')
        self.assertEqual(args[args.index('--service-name')+1], 'custom-quasar')
        self.assertEqual(args[args.index('--install-dir')+1], str(self.root / "existing ' % data"))
        self.assertNotIn('--system', args)
        self.assertNotIn('--yes', args)

@unittest.skipIf(os.geteuid() == 0, 'User-service installer requires an unelevated user')
class InstallerProcess(unittest.TestCase):
    """Run the real copy/repair path; service and runtime commands stay inert."""
    def test_fresh_repeat_and_repair_preserve_private_setup_without_a_bridge(self):
        with tempfile.TemporaryDirectory(prefix='quasar-install-') as temporary:
            root = Path(temporary)
            package, install, home, binaries = [root / name for name in ('package', 'install', 'home', 'bin')]
            for folder in (package, home, binaries, package / 'Updater'):
                folder.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO / 'install.sh', package / 'install.sh')
            def executable(path, content):
                path.write_text(content)
                path.chmod(0o755)
            executable(package / 'Quasar', '#!/bin/sh\nexit 0\n')
            executable(package / 'Updater/Quasar.Updater', '#!/bin/sh\necho "$1" >> "$CALLS"\ncase "$1" in check-upgrade-path) exit 3;; check-stopped) exit "${STOP_CHECK_EXIT:-0}";; esac\n')
            (package / 'release.json').write_text('{"version":"1.6.0.6","updaterProtocol":1}')
            (package / 'launcher-files.txt').write_text('Quasar\nrelease.json\n')
            (package / 'appsettings.json').write_text('{"packageDefaults":true}')
            executable(binaries / 'getent', '#!/bin/sh\nprintf "%s:x:1000:1000:Test:%s:/bin/bash\\n" "$USER" "$TEST_HOME"\n')
            executable(binaries / 'systemctl', '#!/bin/sh\nexit 0\n')
            executable(binaries / 'dotnet', '#!/bin/sh\nprintf "Microsoft.NETCore.App 10.0.0 [/fixture]\\nMicrosoft.AspNetCore.App 10.0.0 [/fixture]\\n"\n')
            env = dict(os.environ, HOME=str(home), TEST_HOME=str(home), USER=getpass.getuser(),
                       PATH=str(binaries)+os.pathsep+os.defpath, CALLS=str(root / 'calls'))
            def run(*options, in_place=False, **extra_env):
                source = install if in_place else package
                return subprocess.run(['/bin/bash', str(source / 'install.sh'), '--no-build', '--no-enable',
                    '--install-dir', str(install), *options], env=dict(env, **extra_env), capture_output=True, text=True, timeout=15)
            first = run()
            self.assertEqual(first.returncode, 0, first.stderr)
            self.assertIn('http://localhost:8080/setup', first.stdout)
            private = {
                'HostedPanel/enrollment.json': '{"Choice":"hosted","SetupCompleted":false,"InstallationSecret":"private"}',
                'rbac.json': '{"administrator":"retained"}',
                'cluster-credentials.json': 'protected credentials',
                'DataProtectionKeys/key.xml': 'protected keyring',
                'appsettings.json': '{"customPort":18080}',
            }
            for name, value in private.items():
                target = install / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(value)
            for options, in_place in [((), False), ((), True), (('--repair-supervision',), False)]:
                repeated = run(*options, in_place=in_place)
                self.assertEqual(repeated.returncode, 0, repeated.stderr)
                for name, value in private.items():
                    self.assertEqual((install / name).read_text(), value)
            # A completed choice must also survive reinstall without being reset.
            private['HostedPanel/enrollment.json'] = '{"Choice":"self","SetupCompleted":true}'
            (install / 'HostedPanel/enrollment.json').write_text(private['HostedPanel/enrollment.json'])
            completed = run(in_place=True)
            self.assertEqual(completed.returncode, 0, completed.stderr)
            for name, value in private.items():
                self.assertEqual((install / name).read_text(), value)
            self.assertNotIn('check-upgrade-path', (root / 'calls').read_text())
            self.assertFalse((install / 'Updates/migration-1.4.1.json').exists())
            self.assertIn('check-stopped', (root / 'calls').read_text())
            (package / 'Quasar').write_text('replacement must not be copied')
            failed = run(STOP_CHECK_EXIT='9')
            self.assertNotEqual(failed.returncode, 0)
            self.assertEqual((install / 'Quasar').read_text(), '#!/bin/sh\nexit 0\n')
            for name, value in private.items():
                self.assertEqual((install / name).read_text(), value)

if __name__ == '__main__':
    unittest.main()
