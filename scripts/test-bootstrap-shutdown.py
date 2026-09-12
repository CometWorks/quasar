#!/usr/bin/env python3
"""Exercise real Bootstrap with a fake HTTP worker; never launches Quasar/SE.

Build Quasar.Bootstrap first, then run this script (Python 3, no dependencies).
Optionally pass a path to a different Quasar.Bootstrap.dll build.
"""

import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request


def run_worker(port):
    root = Path(os.environ["QUASAR_INSTALL_DIR"])

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.end_headers()
            self.wfile.write(str(os.getpid()).encode())

        def do_POST(self):
            if self.path == "/shutdown":
                (root / "launcher-shutdown-request").write_text("intentional shutdown")
            self.do_GET()
            self.server.exit_code = 7 if self.path == "/crash" else 0

        def log_message(self, *args):
            pass

    with http.server.HTTPServer(("127.0.0.1", port), Handler) as server:
        server.timeout = 0.2
        server.exit_code = None
        # Bound even when a failing test kills Bootstrap before its worker exits.
        deadline = time.monotonic() + 45
        while server.exit_code is None and time.monotonic() < deadline:
            server.handle_request()
        return server.exit_code or 0


def check_shutdown(bootstrap, service):
    with tempfile.TemporaryDirectory(prefix="quasar-shutdown-") as directory:
        root = Path(directory)
        # Keep Bootstrap outside the source tree so worker discovery cannot fall
        # back to a real Quasar build if this fixture's release pointer is invalid.
        bootstrap_root = root / "Bootstrap"
        bootstrap_root.mkdir()
        for name in (bootstrap.name, bootstrap.stem + ".deps.json",
                     bootstrap.stem + ".runtimeconfig.json", "Magnetar.Protocol.dll"):
            shutil.copy2(bootstrap.parent / name, bootstrap_root / name)
        worker_root = root / "WebService" / "FakeWorker"
        worker_root.mkdir(parents=True)
        updates = root / "Updates"
        updates.mkdir()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        (root / "appsettings.json").write_text(json.dumps({
            "Quasar": {"Host": "127.0.0.1", "Port": port},
        }))
        (updates / "active-release.json").write_text(json.dumps({
            "version": "1.1.0",
            "fileName": sys.executable,
            "arguments": f'"{Path(__file__).resolve()}" --fake-worker {port}',
            "workingDirectory": str(worker_root),
        }))
        marker = root / "launcher-shutdown-request"
        marker.write_text("stale request from a previous run")
        environment = {key: value for key, value in os.environ.items()
                       if not key.startswith(("QUASAR_", "MAGNETAR_WEB_"))}
        environment.update(QUASAR_INSTALL_DIR=str(root), QUASAR_UPDATES_ENABLED="false")
        command = ["dotnet", str(bootstrap_root / bootstrap.name), "serve", "--foreground"]
        if service:
            command.append("--service")
        client = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def request(path, method="GET"):
            url = f"http://127.0.0.1:{port}{path}"
            with client.open(urllib.request.Request(url, method=method), timeout=2) as response:
                return int(response.read())

        log_path = root / "bootstrap.log"
        with log_path.open("w") as log:
            process = subprocess.Popen(command, cwd=root, env=environment, stdout=log, stderr=log)
            try:
                def wait_for_worker(activation_count):
                    deadline = time.monotonic() + 15
                    while time.monotonic() < deadline:
                        assert process.poll() is None, log_path.read_text()
                        if log_path.read_text().count("Activated Quasar worker version") >= activation_count:
                            return request("/api/health")
                        time.sleep(0.05)
                    raise AssertionError("Worker was not activated:\n" + log_path.read_text())

                first_pid = wait_for_worker(1)
                assert not marker.exists(), "Stale request was not cleared on startup"
                request("/crash", "POST")
                second_pid = wait_for_worker(2)
                assert first_pid != second_pid, "Unexpected worker exit must still restart"
                request("/shutdown", "POST")
                assert process.wait(timeout=10) == 0, log_path.read_text()
                assert not marker.exists(), "Shutdown request was not consumed"
                assert log_path.read_text().count("Activated Quasar worker version") == 2
                with socket.socket() as probe:
                    probe.settimeout(1)
                    assert probe.connect_ex(("127.0.0.1", port)) != 0, "Worker still listening"
            except Exception:
                print(log_path.read_text(), file=sys.stderr)
                raise
            finally:
                if process.poll() is None:
                    try:
                        request("/stop", "POST")
                    except OSError:
                        pass
                    process.kill()
                    process.wait(timeout=5)
        print(f"PASS {'service' if service else 'foreground'}: stale request, crash restart, full shutdown (exit 0)")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--fake-worker":
        sys.exit(run_worker(int(sys.argv[2])))
    default = Path(__file__).resolve().parents[1] / "Quasar.Bootstrap/bin/Debug/net10.0/Quasar.Bootstrap.dll"
    bootstrap = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else default
    assert bootstrap.is_file(), f"Build Quasar.Bootstrap first: {bootstrap}"
    for service in (False, True):
        check_shutdown(bootstrap, service)
