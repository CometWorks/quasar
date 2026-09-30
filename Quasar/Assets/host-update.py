#!/usr/bin/env python3
"""Update one enrolled Host from its authenticated Quasar origin."""

import fcntl
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
from urllib.parse import quote, urlparse
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


def digest(path):
    hash_value = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            hash_value.update(chunk)
    return hash_value.hexdigest()


def restart(unit):
    subprocess.run(["systemctl", "--user", "restart", unit], check=True, timeout=30)


def check_running(unit):
    pid = None
    for _ in range(10):
        result = subprocess.run(["systemctl", "--user", "show", unit,
                                 "--property=ActiveState,MainPID"], check=True,
                                capture_output=True, text=True, timeout=10)
        values = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
        current = values.get("MainPID", "0")
        if values.get("ActiveState") != "active" or current == "0" or pid is not None and current != pid:
            raise RuntimeError("Updated Host service did not remain active.")
        pid = current
        time.sleep(1)


def origin_url(config):
    value = config["connection"]["quasarUrl"]
    parsed = urlparse(value)
    if not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/"):
        raise ValueError("Invalid enrolled Quasar origin.")
    if parsed.scheme != "https":
        try:
            loopback = ipaddress.ip_address(parsed.hostname).is_loopback
        except ValueError:
            loopback = parsed.hostname == "localhost"
        if parsed.scheme != "http" or not loopback:
            raise ValueError("Host updates require HTTPS or loopback HTTP.")
    return value.rstrip("/")


def update(root):
    root = Path(root)
    if root.is_symlink() or not root.is_dir():
        raise ValueError("Host installation directory is missing or linked.")
    with open(root / "host.json") as source:
        config = json.load(source)
    host = config["hostId"]
    if host != root.name or not re.fullmatch(r"[a-z][a-z0-9-]{0,62}", host):
        raise ValueError("Host installation identity differs from its directory.")
    reference = config["connection"]["tokenEnvironmentVariable"]
    with open(root / "state/credentials.json") as source:
        credential = json.load(source)[reference]
    unit = "quasar-host-" + host + ".service"
    binary = root / "Quasar.Host"
    backup = root / ".Quasar.Host.previous"
    pending = root / ".host-update-pending"
    failed = root / "state/failed-host-update-sha256"
    lock_path = root.parent / ("." + host + ".install.lock")
    with open(lock_path, "a+b") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if pending.exists():
            if not backup.exists():
                raise RuntimeError("Incomplete Host update has no rollback binary.")
            attempted = pending.read_text().strip()
            os.replace(backup, binary)
            pending.unlink()
            failed.write_text(attempted)
            restart(unit)

        base = origin_url(config) + "/api/v1/hosts/" + quote(host) + "/update"
        opener = build_opener(NoRedirect(), ProxyHandler({}))
        headers = {"Authorization": "Bearer " + credential}
        with opener.open(Request(base, headers=headers), timeout=30) as response:
            metadata = json.load(response)
        expected = metadata["sha256"]
        size = metadata["size"]
        if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected) \
                or type(size) is not int or size < 1 or size > 512 * 1024 * 1024:
            raise ValueError("Invalid Host update metadata.")
        if binary.exists() and digest(binary) == expected:
            if failed.exists():
                failed.unlink()
            return
        if failed.exists() and failed.read_text().strip() == expected:
            raise RuntimeError("Host update failed previously; waiting for a different Quasar release.")

        descriptor, staged = tempfile.mkstemp(prefix=".host-update-", dir=root)
        try:
            with os.fdopen(descriptor, "wb") as output, opener.open(
                    Request(base + "/binary", headers=headers), timeout=120) as response:
                hash_value = hashlib.sha256()
                total = 0
                while chunk := response.read(1024 * 1024):
                    total += len(chunk)
                    if total > size:
                        raise ValueError("Host update exceeds advertised size.")
                    output.write(chunk)
                    hash_value.update(chunk)
                output.flush()
                os.fsync(output.fileno())
            if total != size or hash_value.hexdigest() != expected:
                raise ValueError("Host update failed SHA-256 verification.")
            os.chmod(staged, 0o700)
            shutil.copy2(binary, backup)
            pending.write_text(expected)
            os.replace(staged, binary)
            try:
                restart(unit)
                check_running(unit)
            except Exception:
                os.replace(backup, binary)
                pending.unlink()
                failed.write_text(expected)
                restart(unit)
                raise
            pending.unlink()
            backup.unlink()
            if failed.exists():
                failed.unlink()
            print("Quasar Host updated to " + expected[:12] + ".")
        finally:
            if os.path.exists(staged):
                os.unlink(staged)


if __name__ == "__main__":
    try:
        update(sys.argv[1])
    except Exception as error:
        print("Quasar Host update failed: " + str(error), file=sys.stderr)
        sys.exit(1)
