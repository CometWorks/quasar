import hashlib
import importlib.util
import io
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("dev-override-assets.py")
spec = importlib.util.spec_from_file_location("assets", SCRIPT)
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


def tarball(path: Path, files: dict[str, bytes]) -> str:
    with tarfile.open(path, "w:gz") as tar:
        for name, data in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            tar.addfile(info, io.BytesIO(data))
    return hashlib.sha256(path.read_bytes()).hexdigest()


class DevOverrideAssetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.checkout = self.root / "se1/linux-compat"
        (self.checkout / "ServerPlugin").mkdir(parents=True)
        (self.checkout / "ServerPlugin/ServerPlugin.xml").write_text(
            "<PluginData><Id>linux-compat</Id>"
            '<Asset Name="LinuxDependencies" Path="../../linux-dependencies/dist/se1-dependencies.tar.gz" Extract="true" Placement="Bin"/>'
            '<Asset Name="NativeWrappers" Path="../../linux-native-wrappers/build" Placement="Bin"/></PluginData>'
        )
        self.dependencies = self.root / "release/se1-dependencies.tar.gz"
        self.wrappers = self.root / "release/se1-native-wrappers.tar.gz"
        self.dependencies.parent.mkdir()
        dependencies_sha = tarball(self.dependencies, {"libdep.so": b"dependency"})
        wrappers_sha = tarball(
            self.wrappers, {"libwrapper.so": b"wrapper", "sub/libnested.so": b"nested"}
        )
        self.hub = self.root / "LinuxCompat.xml"
        self.hub.write_text(
            "<PluginData><Id>linux-compat</Id>"
            f'<Asset Name="LinuxDependencies" Url="{self.dependencies.as_uri()}" Sha256="{dependencies_sha}" Extract="true" Placement="Bin"/>'
            f'<Asset Name="NativeWrappers" Url="{self.wrappers.as_uri()}" Sha256="{wrappers_sha}" Extract="true" Placement="Bin"/></PluginData>'
        )

    def tearDown(self):
        self.temp.cleanup()

    def run_script(self, *extra):
        return subprocess.run(
            [
                sys.executable,
                "-I",
                str(SCRIPT),
                str(self.checkout),
                "ServerPlugin/ServerPlugin.xml",
                str(self.hub),
                *extra,
            ],
            capture_output=True,
            text=True,
        )

    def test_archive_and_folder_assets_get_the_hub_pins_bytes(self):
        result = self.run_script()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            self.dependencies.read_bytes(),
            (
                self.root / "linux-dependencies/dist/se1-dependencies.tar.gz"
            ).read_bytes(),
        )
        build = self.root / "linux-native-wrappers/build"
        self.assertEqual(b"wrapper", (build / "libwrapper.so").read_bytes())
        self.assertEqual(b"nested", (build / "sub/libnested.so").read_bytes())
        self.assertIn("already the hub pin", self.run_script().stdout)

    def test_existing_folder_is_kept_without_force(self):
        stale = self.root / "linux-native-wrappers/build/libold.so"
        stale.parent.mkdir(parents=True)
        stale.write_bytes(b"old")
        self.assertIn("kept existing", self.run_script().stdout)
        self.assertTrue(stale.exists())
        self.assertEqual(0, self.run_script("--force").returncode)
        self.assertFalse(stale.exists())
        self.assertTrue((stale.parent / "libwrapper.so").exists())

    def test_wrong_hash_and_paths_inside_the_checkout_are_refused(self):
        self.hub.write_text(self.hub.read_text().replace('Sha256="', 'Sha256="0', 1))
        result = self.run_script()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SHA-256", result.stderr)
        self.assertFalse(
            (self.root / "linux-dependencies/dist/se1-dependencies.tar.gz").exists()
        )
        manifest = self.checkout / "ServerPlugin/ServerPlugin.xml"
        manifest.write_text(
            manifest.read_text().replace(
                "../../linux-native-wrappers/build", "native/build"
            )
        )
        with self.assertRaisesRegex(ValueError, "inside the checkout"):
            assets.place(
                self.checkout.resolve(),
                next(
                    a
                    for a in assets.read_xml(str(manifest)).iter("Asset")
                    if a.get("Name") == "NativeWrappers"
                ),
                next(assets.read_xml(str(self.hub)).iter("Asset")),
                False,
            )

    def test_missing_hub_download_is_refused(self):
        self.hub.write_text("<PluginData><Id>linux-compat</Id></PluginData>")
        result = self.run_script()
        self.assertEqual(1, result.returncode)
        self.assertIn("LinuxDependencies, NativeWrappers", result.stderr)


if __name__ == "__main__":
    unittest.main()
