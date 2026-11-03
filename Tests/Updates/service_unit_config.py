#!/usr/bin/env python3
"""Render the installer's systemd units without installing or starting services."""
import configparser
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]


class ServiceUnitConfig(unittest.TestCase):
    def test_native_units_keep_recovery_and_enable_journal_logging(self):
        installer = (REPO / 'install.sh').read_text()
        with tempfile.TemporaryDirectory(prefix='quasar-unit-config-') as directory:
            root = Path(directory)
            for suffix in ('.service', '-updater.service'):
                # Use the installer's actual heredoc, including its substitutions.
                marker = f'cat > "$scope_dir/$SERVICE_NAME{suffix}" <<EOF\n'
                template = installer.split(marker, 1)[1].split('\nEOF', 1)[0]
                script = 'cat <<EOF\n' + template + '\nEOF\n'
                environment = dict(unit_install=str(root), unit_metadata=str(root / 'service-installation.json'),
                                   main_account='', updater_account='', SERVICE_NAME='quasar-fixture',
                                   CLUSTERS_UNIT='quasar-fixture-clusters.service', scope_dir=directory, INSTALL_MODE='user')
                result = subprocess.run(['bash'], input=script, env=environment, text=True,
                                        capture_output=True, check=True)
                path = root / ('quasar-fixture' + suffix)
                path.write_text(result.stdout)
                config = configparser.ConfigParser(interpolation=None, strict=False)
                config.read_string(re.sub(r'^Environment=.*\n', '', result.stdout, flags=re.MULTILINE))
                self.assertEqual('on-failure', config['Service']['Restart'])
                if suffix == '.service':
                    self.assertEqual('process', config['Service']['KillMode'])
                    self.assertEqual('journal', config['Service']['StandardOutput'])
                    self.assertEqual('journal', config['Service']['StandardError'])
                    self.assertEqual(1, result.stdout.count('Environment="QUASAR_CONSOLE_LOGGING=true"'))
                    self.assertTrue(config['Service']['ExecStart'].endswith('serve --service'))
                else:
                    self.assertNotIn('QUASAR_CONSOLE_LOGGING', result.stdout)
            if shutil.which('systemd-analyze'):
                subprocess.run(['systemd-analyze', 'verify', '--man=no',
                                str(root / 'quasar-fixture.service'), str(root / 'quasar-fixture-updater.service')],
                               text=True, capture_output=True, check=True)


if __name__ == '__main__':
    unittest.main()
