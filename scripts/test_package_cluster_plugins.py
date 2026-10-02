import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("exporter", Path(__file__).with_name("package-cluster-plugins.py"))
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


class PluginBundleExportTests(unittest.TestCase):
    def test_resolved_assets_are_relocated_and_original_provenance_retained(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            cache = root / "cache"
            (cache / "Bin").mkdir(parents=True)
            (cache / "Bin/plugin.dll").write_bytes(b"compiled plugin")
            (cache / "Bin/libHavok.so").write_bytes(b"native")
            (cache / "manifest.xml").write_text(
                f"<CacheManifest><RuntimeIdentifier>linux-x64</RuntimeIdentifier><Assets>"
                f"<Asset><Name>NativeWrappers</Name><Path>{cache / 'Bin'}</Path></Asset>"
                "</Assets></CacheManifest>")
            manifest = root / "source.xml"
            original = ("<PluginData><Id>linux-compat</Id><Commit>" + "a" * 40
                        + "</Commit><Runtimes>CoreCLR</Runtimes><Platforms>Linux</Platforms>"
                        '<Asset Name="NativeWrappers" Url="https://example.test/pinned.tar.gz" '
                        'Sha256="archive-hash" Extract="true" Placement="Bin"/></PluginData>')
            manifest.write_text(original)
            output = root / "bundles"
            exporter.export(manifest, cache, output)
            bundle = output / "linux-compat"
            self.assertEqual((bundle / "linux-compat.dll").read_bytes(), b"compiled plugin")
            self.assertFalse((bundle / "plugin.dll").exists())
            self.assertEqual((bundle / "libHavok.so").read_bytes(), b"native")
            asset = ET.parse(bundle / "linux-compat.xml").getroot().find("Asset")
            self.assertEqual(asset.get("Path"), ".")
            self.assertIsNone(asset.get("Url"))
            self.assertEqual((bundle / "source-metadata.xml").read_text(), original)
            with self.assertRaises(ValueError):
                exporter.export(manifest, cache, output)

    def test_unpinned_source_and_linked_cache_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest = root / "source.xml"
            manifest.write_text("<PluginData><Id>dotnet-compat</Id><Commit>TODO</Commit></PluginData>")
            cache = root / "cache"
            cache.mkdir()
            with self.assertRaises(ValueError):
                exporter.export(manifest, cache, root / "output")
            (cache / "linked").symlink_to(manifest)
            with self.assertRaises(ValueError):
                exporter.export(manifest, cache, root / "output")
            self.assertFalse((root / "output").exists())


if __name__ == "__main__":
    unittest.main()
