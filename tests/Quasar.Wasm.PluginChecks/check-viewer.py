"""Run the Viewer's post-install browser scenarios with Quasar's WASM trimming rules."""
import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET


def check(viewer: Path) -> None:
    viewer = viewer.resolve()
    quasar = Path(__file__).resolve().parents[2]
    source = viewer / "tests" / "WasmChecks"
    with tempfile.TemporaryDirectory(prefix="quasar-viewer-wasm-") as temp:
        scratch = Path(temp)
        fixture = scratch / "tests" / "WasmChecks"
        shutil.copytree(source, fixture, ignore=shutil.ignore_patterns("obj", "bin"))
        # The Viewer driver uses its own published adapter DLL, assets and SDK fixture.
        (scratch / "src").symlink_to(viewer / "src", target_is_directory=True)
        project = fixture / "WasmChecks.csproj"
        xml = ET.parse(project)
        for group in xml.getroot().findall("ItemGroup"):
            for setting in list(group):
                if setting.tag in ("TrimmerRootAssembly", "TrimmerRootDescriptor"):
                    group.remove(setting)
        ET.SubElement(xml.getroot(), "Import", Project=str(quasar / "Quasar.Client" / "PluginTrimming.props"))
        xml.write(project, encoding="utf-8")
        subprocess.run(["dotnet", "publish", str(project), "-c", "Release", "-p:PublishTrimmed=true"], check=True)
        subprocess.run(["node", "--test", str(fixture / "browser.test.mjs")], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("viewer", type=Path, help="Viewer checkout with its Release adapter built")
    check(parser.parse_args().viewer)
