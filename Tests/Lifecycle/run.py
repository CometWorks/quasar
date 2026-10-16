#!/usr/bin/env python3
"""Runs the lifecycle matrix under Quasar (cases.py) on Linux.

Each case gets a Quasar install root of its own (a copy of --template), its own
web port and DS port, and one Quasar worker in Production mode, so health
monitoring is on. The server runs the Magnetar build under test with
Essentials and the lifecycle probe of the Magnetar suite, which this script
imports from a magnetar checkout (--magnetar-src, Tests/Lifecycle). UI actions
go through the real Blazor UI in headless Chromium (Playwright); loopback
requests need no login.

Example:
  Tests/Lifecycle/run.py --magnetar-src ~/magnetar --install ~/magnetar-build \\
      --quasar Quasar/bin/Release/net10.0 --template ~/quasar-lifecycle-template \\
      --ds-root ~/.steam/steam/steamapps/common/SpaceEngineersDedicatedServer \\
      --steamcmd ~/Quasar/ManagedRuntime/Tools --work ~/quasar-lifecycle-run --jobs 4

Writes <work>/results.json and <work>/results.md. Exit code 0 when every case passed.
"""

import argparse
import concurrent.futures
import dataclasses
import fnmatch
import importlib.util
import json
import os
import queue
import shutil
import signal
import subprocess
import sys
import threading
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cases import CASES, CRASH, HEALTH  # noqa: E402

READY_TIMEOUT = 300
PROBE_ID = "lifecycle-probe"
STATE_NAMES = ["Stopped", "Starting", "Running", "Stopping", "Restarting", "Crashed", "Faulted"]
CAUSE_NAMES = [
    "Unknown",
    "HealthPolicy",
    "AgentAttachRecovery",
    "Scheduled",
    "MaximumUptime",
    "Manual",
    "InGame",
    "CrashRecovery",
]

rig = None  # the Magnetar suite's rig module, imported in main()


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def make_server_class():
    class QuasarServer(rig.Server):
        """A Quasar worker with one Magnetar server, rt1, in an install root of its own."""

        def __init__(self, root, platform, template, port, web_port, args, essentials, auto_restart):
            super().__init__(root, platform, template, port, essentials, auto_restart)
            self.web_port = web_port
            self.args = args
            self.server_dir = self.root / "Magnetars" / "rt1"
            self.config = self.server_dir / "Magnetar"
            self.data = self.server_dir / "DedicatedServer"
            self.world = self.data / "Saves" / "rt1"
            self.quasar = None
            self.hold_init = 0

        def setup(self):
            if self.root.exists():
                shutil.rmtree(self.root)
            shutil.copytree(self.template, self.root, symlinks=True)
            probe = self.root / "probe"
            shutil.copytree(rig.PROBE, probe, ignore=shutil.ignore_patterns("bin", "obj"))
            dev_folders = [
                {
                    "name": PROBE_ID,
                    "folderPath": self.platform.path(probe),
                    "dataFile": "LifecycleProbe.xml",
                    "pluginId": PROBE_ID,
                    "debugBuild": False,
                }
            ]
            if rig.ESSENTIALS_SRC:
                essentials = self.root / "essentials"
                shutil.copytree(
                    rig.ESSENTIALS_SRC, essentials, ignore=shutil.ignore_patterns("bin", "obj", ".git*", "*.user")
                )
                dev_folders.append(
                    {
                        "name": "essentials",
                        "folderPath": self.platform.path(essentials),
                        "dataFile": "Essentials.xml",
                        "pluginId": rig.ESSENTIALS_ID,
                        "debugBuild": False,
                    }
                )
            (self.root / "dev-folders.json").write_text(json.dumps(dev_folders, indent=2))

            server_json = self.server_dir / "server.json"
            server = json.loads(server_json.read_text())
            server.update(
                executablePath=self.platform.path(self.platform.install / self.platform.launcher),
                serverPort=self.port,
                goalState=1,
                autoStart=True,
                enableHealthMonitoring=True,
                restartOnCrash=True,
            )
            server_json.write_text(json.dumps(server, indent=2))

            profile_json = self.root / "ConfigProfiles" / "rt-config" / "profile.json"
            profile = json.loads(profile_json.read_text())
            profile["plugins"] = [
                {"pluginId": rig.ESSENTIALS_ID, "displayName": "Essentials", "selectedVersion": ""},
                {"pluginId": PROBE_ID, "displayName": "Lifecycle Probe", "selectedVersion": ""},
            ]
            profile["rootSettings"]["autoRestartEnabled"] = self.auto_restart
            profile["rootSettings"]["autoRestartTimeInMin"] = 1 if self.auto_restart else 0
            profile_json.write_text(json.dumps(profile, indent=2))
            self._write_essentials_config()
            if self.hold_init:
                (self.data / "probe-hold-init.txt").write_text(str(self.hold_init))

        def start(self):
            a, p = self.args, self.platform
            env = dict(
                os.environ,
                QUASAR_INSTALL_DIR=p.path(self.root),
                QUASAR_WEB_HOST="127.0.0.1",
                QUASAR_WEB_PORT=str(self.web_port),
                QUASAR_LOG_MIN_LEVEL="Info",
                QUASAR_OPEN_BROWSER_ON_START="false",  # the case runs headless Chromium itself
                QUASAR_STEAMCMD_INSTALL_DIR=p.path(Path(a.steamcmd).expanduser() / "SteamCMD"),
                QUASAR_STEAMCMD_HOME_DIR=p.path(Path(a.steamcmd).expanduser() / "SteamCmdHome"),
                QUASAR_DS_INSTALL_DIR=p.path(Path(a.ds_root).expanduser()),
                QUASAR_DS_LAUNCH_UPDATE_MODE="Skip",
                QUASAR_MAGNETAR_INSTALL_DIR=p.path(self.root / "ManagedMagnetar"),
            )
            quasar = Path(a.quasar).expanduser()
            if p.windows:
                env.update(
                    DISPLAY=p.display,
                    STEAM_COMPAT_DATA_PATH=str(p.prefix),
                    STEAM_COMPAT_CLIENT_INSTALL_PATH=str(Path.home() / ".steam/steam"),
                    SteamAppId="298740",
                    SteamGameId="298740",
                )
                env.pop("WAYLAND_DISPLAY", None)
                env.pop("STEAM_COMPAT_PROTON", None)
                cmd = ["setsid", "-w", str(p.proton / "proton"), "run", p.path(quasar / "Quasar.exe"), "--no-headless"]
            else:
                cmd = ["dotnet", "Quasar.dll", "--no-headless"]
            log = open(self.root / "quasar.out", "ab")
            self.quasar = subprocess.Popen(
                [*p.wrap, *cmd],
                cwd=self.root if p.windows else quasar,
                env=env,
                stdin=subprocess.DEVNULL,
                stdout=log,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            self.proc = self.quasar
            self._stop_watch.clear()
            self._watch = threading.Thread(target=self._sample, daemon=True)
            self._watch.start()

        def alive(self):
            return bool(self.pids())

        def ds_log(self):
            logs = sorted(self.data.glob("SpaceEngineersDedicated*.log"), key=lambda p: p.stat().st_mtime)
            return logs[-1].read_text(errors="replace") if logs else ""

        def supervisor(self):
            try:
                servers = json.loads((self.root / "supervisor-state.json").read_text())["servers"]
                return next(s for s in servers if s["uniqueName"] == "rt1")
            except (OSError, ValueError, StopIteration, KeyError):
                return {}

        def goal(self):
            return json.loads((self.server_dir / "server.json").read_text()).get("goalState")

        def quasar_log(self):
            return "".join(p.read_text(errors="replace") for p in sorted((self.root / "Logs").glob("quasar*.log")))

        def quasar_signal(self, name):
            os.killpg(self.quasar.pid, getattr(signal, "SIG" + name))

        def stop(self, timeout=90):
            if self.quasar and self.quasar.poll() is None:
                os.killpg(self.quasar.pid, signal.SIGTERM)
                try:
                    self.quasar.wait(timeout)
                except subprocess.TimeoutExpired:
                    os.killpg(self.quasar.pid, signal.SIGKILL)
                    self.quasar.wait(10)
            if self.alive():
                self.signal("TERM")
                self.wait(lambda: not self.alive(), timeout)
            self.kill()
            if self.platform.windows:
                # Wine processes are not in Proton's process group: end everything in the job's prefix.
                subprocess.run(
                    [str(self.platform.proton / "files/bin/wineserver"), "-k"],
                    env=dict(os.environ, WINEPREFIX=str(self.platform.prefix / "pfx")),
                    timeout=60,
                )
            self._stop_watch.set()

    return QuasarServer


def ui(server, label, timeout=90):
    """Clicks a server action in Quasar's UI and confirms its dialog."""
    from playwright.sync_api import sync_playwright

    with sync_playwright() as p:
        env = dict(os.environ)
        if os.environ.get("LIFECYCLE_CHROMIUM_LIBS"):
            env["LD_LIBRARY_PATH"] = os.environ["LIFECYCLE_CHROMIUM_LIBS"]
        browser = p.chromium.launch(executable_path=os.environ.get("LIFECYCLE_CHROMIUM") or None, env=env)
        try:
            page = browser.new_page()
            end = time.time() + timeout
            while True:  # Quasar may start the server before its web server listens
                try:
                    page.goto(f"http://127.0.0.1:{server.web_port}/", timeout=timeout * 1000)
                    break
                except Exception:
                    if time.time() > end:
                        raise
                    time.sleep(2)
            # Clicks on the prerendered page do nothing until the Blazor circuit is up.
            page.wait_for_function("() => window.Blazor !== undefined", timeout=timeout * 1000)
            page.wait_for_timeout(3000)
            # The landing page shows server cards; its list view has the labelled server actions.
            button = page.locator(f'button[aria-label^="{label}"]').first
            while not button.is_visible():
                if time.time() > end:
                    page.screenshot(path=str(server.root / f"ui-{label.split()[0].lower()}.png"))
                    raise TimeoutError(f"no '{label}' button in Quasar's UI")
                page.locator("button", has_text="List").first.click(timeout=timeout * 1000)
                page.wait_for_timeout(1000)
            # Stop and Kill ask first; their confirm button is the action's verb. Click again
            # until the dialog shows, in case the first click came too early.
            verb = label.split()[0]
            confirm = page.locator(".mud-dialog button", has_text=verb)
            for _ in range(5):
                button.click()
                if verb not in ("Stop", "Kill"):
                    break
                try:
                    confirm.first.click(timeout=5000)
                    break
                except Exception:
                    if not button.is_visible():
                        break
            page.wait_for_timeout(1500)
            page.screenshot(path=str(server.root / f"ui-{verb.lower()}-after.png"))
        finally:
            browser.close()


def chat_page(server, line, timeout=90):
    """Runs a line from Quasar's Chat page, the way an admin types it there."""
    from playwright.sync_api import sync_playwright

    with sync_playwright() as p:
        env = dict(os.environ)
        if os.environ.get("LIFECYCLE_CHROMIUM_LIBS"):
            env["LD_LIBRARY_PATH"] = os.environ["LIFECYCLE_CHROMIUM_LIBS"]
        browser = p.chromium.launch(executable_path=os.environ.get("LIFECYCLE_CHROMIUM") or None, env=env)
        try:
            page = browser.new_page()
            page.goto(f"http://127.0.0.1:{server.web_port}/chat", timeout=timeout * 1000)
            page.wait_for_function("() => window.Blazor !== undefined", timeout=timeout * 1000)
            page.wait_for_timeout(3000)
            box = page.locator("textarea, input[type=text]").last
            box.wait_for(timeout=timeout * 1000)
            box.fill(line)
            page.wait_for_timeout(500)
            page.keyboard.press("Escape")  # close the suggestions
            page.locator("button", has_text="Run").last.click(timeout=timeout * 1000)
            page.wait_for_timeout(3000)
            page.screenshot(path=str(server.root / "chat-page-after.png"))
        finally:
            browser.close()


def run_steps(server, case, log, result):
    marked = None
    for step in case.steps:
        kind = step[0]
        log.append(f"{time.strftime('%H:%M:%S')} {step}")
        if kind == "ready":
            n = step[1]
            if not server.wait(lambda: len(server.ready_runs()) >= n, READY_TIMEOUT):
                raise TimeoutError(f"no world load #{n}")
            continue
        if kind == "loading":
            if not server.wait(
                lambda: "Loading session:" in server.ds_log() and not server.ready_runs(), READY_TIMEOUT, 0.2
            ):
                raise TimeoutError("did not see the world loading")
            continue
        if kind == "sleep":
            time.sleep(step[1])
            continue
        if kind == "init":
            if not server.wait(lambda: any(m.startswith("init ") for _, _, m in server.events()), READY_TIMEOUT):
                raise TimeoutError("the probe did not start")
            continue
        if kind == "state":
            if not server.wait(lambda: server.supervisor().get("state") == step[1] and not server.alive(), 180):
                raise TimeoutError(f"Quasar state did not become {STATE_NAMES[step[1]]}")
            continue
        if marked is None:
            marked = (time.time(), server.world_mtime())
            result["sent"] = True
        if kind == "probe":
            server.probe(*step[1:])
        elif kind == "signal":
            server.signal(step[1])
        elif kind == "quasar":
            server.quasar_signal(step[1])
        elif kind == "ui":
            ui(server, step[1])
        elif kind == "chatpage":
            chat_page(server, step[1])
    return marked


def run_case(case, platform, args, port, web_port, gate, server_class):
    work = Path(args.work) / case.id
    server = server_class(work, platform, args.template, port, web_port, args, case.essentials, case.auto_restart)
    server.hold_init = case.hold_init
    log, failures = [], []
    result = {"id": case.id, "state": case.state, "command": case.command, "platform": platform.name + "-quasar"}
    try:
        server.setup()
        with gate:
            server.start()
        marked = run_steps(server, case, log, result)
        if case.launches:
            reached = server.wait(lambda: len(server.ready_runs()) >= 1 + case.launches, READY_TIMEOUT * case.launches)
        elif case.end_state == 0:
            reached = server.wait(lambda: not server.alive() and server.supervisor().get("state") == 0, 240)
        else:
            reached = True
        if not reached:
            failures.append("expected outcome not reached in time")
        time.sleep(case.settle)

        sup = server.supervisor()
        last = sup.get("lastRestart") or {}
        runs = server.ready_runs()
        quasar_log = server.quasar_log()
        result.update(
            world_loads=len(runs),
            pids=server.distinct_pids(),
            processes=server.cmdlines,
            alive=server.alive(),
            quasar_state=STATE_NAMES[sup["state"]] if "state" in sup else None,
            restart_attempts=sup.get("restartAttempts"),
            last_exit_code=sup.get("lastExitCode"),
            last_restart_cause=CAUSE_NAMES[last["cause"]] if "cause" in last else None,
            last_restart_reason=last.get("reason"),
            goal=server.goal(),
            transport_errors=quasar_log.count("transport error"),
            unhealthy=quasar_log.count("unhealthy"),
            terminating=[m.split()[1] for _, _, m in server.events() if m.startswith("terminating ")],
            save_calls=sum(1 for _, _, m in server.events() if m.startswith("save from")),
        )
        if marked:
            result["saved"] = server.world_mtime() > marked[1]

        # Under Wine a process shows its parent's command line until it execs, so count the
        # processes that loaded a world.
        launches = max(len({pid for pid, _ in runs}) - 1, 0)
        if launches != case.launches:
            failures.append(f"{launches} relaunches, expected {case.launches}")
        if case.end_state is not None and sup.get("state") != case.end_state:
            failures.append(f"Quasar state {result['quasar_state']}, expected {STATE_NAMES[case.end_state]}")
        if case.end_state == 2 and len(runs) != 1 + case.launches:
            failures.append(f"{len(runs)} world loads, expected {1 + case.launches}")
        if case.cause is not None and last.get("cause") != case.cause:
            failures.append(f"restart cause {result['last_restart_cause']}, expected {CAUSE_NAMES[case.cause]}")
        if case.end_state is not None and (sup.get("restartAttempts") or 0) != case.attempts:
            failures.append(f"restartAttempts {sup.get('restartAttempts')}, expected {case.attempts}")
        if case.end_state is not None and result["goal"] != case.goal:
            failures.append(f"goal {result['goal']}, expected {case.goal}")
        if case.saved is not None and marked and result["saved"] != case.saved:
            failures.append(f"saved={result['saved']}, expected {case.saved}")
        result["replies"] = [m[len("reply ") :] for _, _, m in server.events() if m.startswith("reply ")]
        for text in case.replies:
            if not any(text in reply for reply in result["replies"]):
                failures.append(f"no reply with {text!r}, got {result['replies']}")
        if platform.windows:
            result["overlap_s"] = round(server.overlap_seconds(), 1)
            if result["overlap_s"] > 15:
                failures.append(f"two Magnetar processes ran together for {result['overlap_s']} s")
        elif server.max_concurrent() > 1:
            failures.append(f"{server.max_concurrent()} Magnetar processes at once")
        # A killed or crashed process drops the agent socket without closing it.
        abrupt = case.cause in (CRASH, HEALTH) or any(s[-1] in ("KILL", "Kill starting server") for s in case.steps)
        if result["transport_errors"] and not abrupt:
            failures.append(f"{result['transport_errors']} agent socket transport errors")
        if "Error disposing plugins" in server.info_logs():
            failures.append("Error disposing plugins in the info log")
        racing = [m for _, _, m in server.events() if m.startswith("dispose updates=") and m != "dispose updates=0"]
        if racing:
            failures.append(f"plugins updated during disposal: {racing}")
    except Exception as e:
        failures.append(f"{type(e).__name__}: {e}")
        log.append(traceback.format_exc())
    finally:
        try:
            server.stop()
            result["after_quasar_stop"] = {"alive": server.alive(), "pids": server.pids()}
        except Exception as e:
            failures.append(f"cleanup: {e}")
    result.update(failures=failures, passed=not failures, log=log)
    (work / "result.json").write_text(json.dumps(result, indent=2))
    return result


def markdown(results, platform):
    lines = [f"| Case | State | Command | {platform}-quasar | Notes |", "|---|---|---|---|---|"]
    for r in results:
        detail = [f"{r.get('quasar_state')}", f"{len(r.get('pids', [])) - 1} relaunches"]
        if r.get("last_restart_cause"):
            detail.append(f"cause {r['last_restart_cause']}")
        if r.get("restart_attempts"):
            detail.append(f"attempts {r['restart_attempts']}")
        if "saved" in r:
            detail.append("saved" if r["saved"] else "not saved")
        detail += r["failures"]
        verdict = "pass" if r["passed"] else "**FAIL**"
        lines.append(f"| {r['id']} | {r['state']} | {r['command']} | {verdict} | {'; '.join(map(str, detail))} |")
    return "\n".join(lines) + "\n"


def main():
    global rig
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument(
        "--platform",
        choices=["linux", "proton-interim", "proton-legacy"],
        default="linux",
        help="proton-*: Quasar.exe and the Windows launchers under Proton",
    )
    ap.add_argument("--magnetar-src", required=True, help="magnetar checkout with Tests/Lifecycle")
    ap.add_argument("--install", required=True, help="Magnetar install folder under test")
    ap.add_argument("--quasar", required=True, help="folder with the built Quasar.dll")
    ap.add_argument("--template", required=True, help="Quasar install root to copy per case, see README.md")
    ap.add_argument("--ds-root", required=True, help="DS install folder (holds DedicatedServer64)")
    ap.add_argument("--steamcmd", required=True, help="folder with SteamCMD and SteamCmdHome")
    ap.add_argument("--work", required=True)
    ap.add_argument("--jobs", type=int, default=2)
    ap.add_argument("--port-base", type=int, default=28100)
    ap.add_argument("--web-port-base", type=int, default=25100)
    ap.add_argument("--cases", default="*")
    ap.add_argument("--wrap", default="")
    ap.add_argument("--ram-floor-gib", type=float, default=10)
    ap.add_argument("--proton", default=str(Path.home() / ".steam/steam/steamapps/common/Proton - Experimental"))
    ap.add_argument(
        "--prefix",
        help="Proton prefixes, one per job; each needs .NET 10 with ASP.NET Core, and "
        ".NET Framework 4.8 for proton-legacy",
    )
    ap.add_argument("--display", default=":1")
    ap.add_argument(
        "--essentials-src",
        help="Essentials working copy to compile as a DevFolder; the cases marked essentials_src need it",
    )
    args = ap.parse_args()

    suite = Path(args.magnetar_src).expanduser() / "Tests" / "Lifecycle"
    rig = load(suite / "rig.py", "magnetar_lifecycle_rig")
    magnetar_run = load(suite / "run.py", "magnetar_lifecycle_run")
    RamGate, steam_timeout = magnetar_run.RamGate, magnetar_run.steam_timeout
    rig.ESSENTIALS_SRC = Path(args.essentials_src).expanduser() if args.essentials_src else None
    server_class = make_server_class()
    ds64 = Path(args.ds_root).expanduser() / "DedicatedServer64"
    platform = rig.Platform(
        args.platform, Path(args.install).expanduser(), ds64, args.wrap.split(), Path(args.proton), None, args.display
    )
    prefixes = [Path(p).expanduser() for p in args.prefix.split(",")] if args.prefix else [None]
    if platform.windows and len(prefixes) < args.jobs:
        ap.error("--prefix needs one prefix per job")
    cases = [
        c
        for c in CASES
        if (c.windows or not platform.windows) and any(fnmatch.fnmatch(c.id, p) for p in args.cases.split(","))
    ]
    if not args.essentials_src:
        skipped = [c.id for c in cases if c.essentials_src]
        if skipped:
            print(f"skipped without --essentials-src: {', '.join(skipped)}", flush=True)
        cases = [c for c in cases if not c.essentials_src]
    Path(args.work).mkdir(parents=True, exist_ok=True)

    slots = queue.Queue()
    for i in range(args.jobs):
        slots.put((args.port_base + i, args.web_port_base + i, prefixes[i % len(prefixes)]))
    gate = RamGate(args.ram_floor_gib, 5)

    def job(case):
        port, web_port, prefix = slots.get()
        try:
            r = run_case(case, dataclasses.replace(platform, prefix=prefix), args, port, web_port, gate, server_class)
            if not r["passed"] and (not r.get("sent") or steam_timeout(Path(args.work) / case.id)):
                # Failed before the case sent anything, or a DS's Steam logon timed out (Quasar
                # then relaunches it as a crash): says nothing about the case, run it again.
                first = r["failures"]
                r = run_case(
                    case, dataclasses.replace(platform, prefix=prefix), args, port, web_port, gate, server_class
                )
                r["retried_after"] = first
        finally:
            slots.put((port, web_port, prefix))
        print(f"{time.strftime('%H:%M:%S')} {'PASS' if r['passed'] else 'FAIL'} {case.id} {r['failures']}", flush=True)
        return r

    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        results = list(pool.map(job, cases))

    work = Path(args.work)
    (work / "results.json").write_text(json.dumps(results, indent=2))
    (work / "results.md").write_text(markdown(results, platform.name))
    failed = [r["id"] for r in results if not r["passed"]]
    print(f"{len(results) - len(failed)}/{len(results)} passed" + (f", failed: {', '.join(failed)}" if failed else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
