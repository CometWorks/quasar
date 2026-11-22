"""Run the Viewer's post-install browser scenarios with Quasar's untrimmed browser framework."""
import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path


def check(viewer: Path) -> None:
    viewer = viewer.resolve()
    source = viewer / "tests" / "WasmChecks"
    with tempfile.TemporaryDirectory(prefix="quasar-viewer-wasm-") as temp:
        scratch = Path(temp)
        fixture = scratch / "tests" / "WasmChecks"
        shutil.copytree(source, fixture, ignore=shutil.ignore_patterns("obj", "bin"))
        # The Viewer driver uses its own published adapter DLL, assets and SDK fixture.
        shutil.copy2(viewer / "quasar-plugin.json", scratch / "quasar-plugin.json")
        (scratch / "src").symlink_to(viewer / "src", target_is_directory=True)
        project = fixture / "WasmChecks.csproj"
        subprocess.run(["dotnet", "publish", str(project), "-c", "Release", "-p:PublishTrimmed=false"], check=True)
        subprocess.run(["node", "--test", str(fixture / "browser.test.mjs")], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("viewer", type=Path, help="Viewer checkout with its Release adapter built")
    check(parser.parse_args().viewer)
