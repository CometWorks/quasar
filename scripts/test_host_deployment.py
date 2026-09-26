"""Exercise only Host's offline preparation command with inert file fixtures."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class HostDeploymentCommandTest(unittest.TestCase):
    def test_prepare_reuse_and_tamper_rejection(self):
        host = Path(__file__).resolve().parents[1] / "Quasar.Host/bin/Debug/net10.0/Quasar.Host.dll"
        self.assertTrue(host.is_file(), "build Quasar.Host first")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            package, dependencies = root / "package", root / "dependencies"
            def write(base, name, data):
                path = base / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(data)
                return path
            def pins(base):
                return {p.relative_to(base).as_posix(): {"sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
                        "bytes": p.stat().st_size, "executable": bool(p.stat().st_mode & 0o100)}
                        for p in sorted(base.rglob("*")) if p.is_file()}
            write(package, "manifest.json", json.dumps({"name": "cluster", "version": "1.0.3", "commit": "b" * 40}))
            write(package, "cli/deployment-capabilities.json", '{"schemaVersion":1,"frozenPluginBundles":true}')
            payload = dependencies / "payload"
            for name in ("DedicatedServer/DedicatedServer64/SpaceEngineersDedicated.exe",
                         "Magnetar/MagnetarInterim.bin", "Magnetar/Libraries/MagnetarInterim/PluginSdk.dll",
                         "DirectTransport/DirectTransport.dll", "DirectTransport/DirectTransport.xml", "DirectTransport/LiteNetLib.dll"):
                write(payload, name, "inert fixture, must never execute")
            (payload / "Magnetar/MagnetarInterim.bin").chmod(0o700)
            for plugin_id in ("dotnet-compat", "linux-compat"):
                folder = payload / "CommonPlugins" / plugin_id
                write(folder, plugin_id + ".dll", "inert plugin")
                write(folder, plugin_id + ".xml",
                      '<PluginData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
                      f'<Id>{plugin_id}</Id><Commit>{"c" * 40}</Commit><Runtimes>CoreCLR</Runtimes></PluginData>')
            for native in ("libHavok.so", "libRecastDetour.so", "libVRageNative.so", "libsteam_api.so", "libEOSSDK-Linux-Shipping.so"):
                write(payload / "CommonPlugins/linux-compat", native, "inert native")
            manifest = {"schemaVersion": 2, "packageVersion": "1.0.3", "packageSha256": "a" * 64,
                        "packageCommit": "b" * 40, "directTransportCommit": "d" * 40, "files": pins(payload)}
            write(dependencies, "manifest.json", json.dumps(manifest))
            inputs = {"clusterId": "demo", "packageSelectionRevision": 1, "packageVersion": "1.0.3",
                      "packageSha256": "a" * 64, "packageCommit": "b" * 40,
                      "dependencyManifestSha256": hashlib.sha256((dependencies / "manifest.json").read_bytes()).hexdigest(),
                      "packageDirectory": str(package), "dependencyDirectory": str(dependencies),
                      "files": {**{"Package/" + p: pin for p, pin in pins(package).items()},
                                **{"Dependencies/" + p: pin for p, pin in pins(dependencies).items()}}}
            request = write(root, "inputs.json", json.dumps(inputs))
            sha = hashlib.sha256(request.read_bytes()).hexdigest()
            command = ["dotnet", str(host), "deployment", "prepare", "--file", str(request),
                       "--sha256", sha, "--directory", str(root / "host")]
            def run():
                return subprocess.run(command, capture_output=True, text=True, timeout=30)
            first = run()
            self.assertEqual(first.returncode, 0, first.stderr)
            prepared = json.loads(first.stdout)
            # Offline reuse only needs the approved request and the prepared copies.
            os.rename(package, root / "removed-package")
            os.rename(dependencies, root / "removed-dependencies")
            second = run()
            self.assertEqual(second.returncode, 0, second.stderr)
            self.assertEqual(json.loads(second.stdout), prepared)
            archive = root / "portable.tar"
            export = subprocess.run(["dotnet", str(host), "deployment", "export", "--installation", prepared["directory"],
                "--file", str(archive)], capture_output=True, text=True, timeout=30)
            self.assertEqual(export.returncode, 0, export.stderr)
            transfer = ["dotnet", str(host), "deployment", "import", "--file", str(archive), "--sha256", sha,
                "--directory", str(root / "remote")]
            imported = subprocess.run(transfer, capture_output=True, text=True, timeout=30)
            self.assertEqual(imported.returncode, 0, imported.stderr)
            self.assertEqual(subprocess.run(transfer, capture_output=True, timeout=30).returncode, 0)
            moved = Path(json.loads(imported.stdout)["directory"])
            self.assertIn(str(moved), (moved / "launch-environment.json").read_text())
            import tarfile
            import io
            with tarfile.open(archive, "a") as tar:
                bad = tarfile.TarInfo("../../escape")
                bad.size = 1
                tar.addfile(bad, io.BytesIO(b"x"))
            self.assertNotEqual(subprocess.run(transfer, capture_output=True, timeout=30).returncode, 0)
            self.assertFalse((root / "escape").exists())
            (Path(prepared["directory"]) / "Dependencies/payload/CommonPlugins/linux-compat/libHavok.so").write_text("changed")
            self.assertNotEqual(run().returncode, 0)


if __name__ == "__main__":
    unittest.main()
