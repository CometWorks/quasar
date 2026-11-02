#!/usr/bin/env python3
"""Guided installer routing with fake dialogs/elevation. No native installs or GUI."""
import getpass
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

REPO = Path(__file__).resolve().parents[2]

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
        self.env = dict(os.environ, HOME=str(self.home), PATH=str(self.bin)+os.pathsep+os.defpath,
                        CALLS=str(self.root / 'calls'), DIALOGS=str(self.root / 'dialogs'),
                        XDG_STATE_HOME=str(self.root / 'state'))

    def tearDown(self):
        self.temp.cleanup()

    def executable(self, path, content):
        path.write_text(content); path.chmod(0o755)

    def run_gui(self, **options):
        return subprocess.run(['/bin/bash', str(self.package / 'install-gui.sh')], env=dict(self.env, **options), capture_output=True, text=True, timeout=10)

    @unittest.skipUnless(shutil.which('gio'), 'GIO desktop launcher unavailable')
    def test_desktop_entry_resolves_its_extracted_package(self):
        desktop = self.package / 'Install.desktop'
        shutil.copy2(REPO / 'scripts/Install.desktop', desktop)
        result = subprocess.run(['gio', 'launch', str(desktop)], env=dict(self.env, CHOICE='2'), capture_output=True, text=True, timeout=10)
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
        self.assertEqual(len(list((self.root / 'state/Quasar').glob('install-*.log'))), 1)

    def test_polkit_cancel_never_reports_success(self):
        self.assertEqual(self.run_gui(BACKEND_EXIT='126').returncode, 0)
        self.assertNotIn('are installed.', (self.root / 'dialogs').read_text())

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

if __name__ == '__main__':
    unittest.main()
