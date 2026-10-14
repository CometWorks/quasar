"""The lifecycle matrix under Quasar: one case per command arriving in one state.

Steps, besides the ones of the Magnetar suite (magnetar Tests/Lifecycle/cases.py):
  ("ui", "Restart server")   click that server action in Quasar's UI, confirming its dialog
  ("quasar", "TERM")         send a signal to the Quasar worker
  ("state", n)               wait until Quasar reports process state n (Running = 2, ...)
  ("init",)                  wait until the probe's plugin init has started
  ("chatpage", "!save")      run a line from Quasar's Chat page

Expectations, besides saved:
  launches   Magnetar processes Quasar started after the first one
  state      Quasar process state at the end (Stopped 0, Running 2, Crashed 5, Faulted 6)
  cause      lastRestart.cause of the last restart (InGame 6, Manual 5, HealthPolicy 1, CrashRecovery 7)
  attempts   restartAttempts, the crash counter
  goal       goalState in server.json (Off 0, On 1)
  replies    text each of which some reply to the probe's chat commands contains
  essentials_src  the case needs an Essentials newer than MagnetarHub's (run.py --essentials-src)
"""

from dataclasses import dataclass, field

READY = ("ready", 1)
SAVING = [("probe", "slowsave 10", "worker sdk SaveWorld"), ("sleep", 2)]
RESTARTING = [("probe", "slowsave 10", "worker sdk SaveAndRestart"), ("sleep", 2)]

STOPPED, RUNNING, CRASHED = 0, 2, 5
MANUAL, INGAME, CRASH, HEALTH = 5, 6, 7, 1


@dataclass
class QuasarCase:
    id: str
    state: str
    command: str
    steps: list
    launches: int = 0
    end_state: int = RUNNING
    cause: object = None
    attempts: int = 0
    goal: int = 1
    saved: object = None
    essentials: str = "none"
    auto_restart: bool = False
    settle: int = 20
    notes: str = ""
    hold_init: int = 0  # seconds the probe holds plugin init, so Quasar shows the server starting
    windows: bool = True  # under Proton: no signals, and the UI fails there (Wine's BCrypt)
    replies: list = field(default_factory=list)
    essentials_src: bool = False


def _ingame():
    return [
        QuasarCase("q-run-save-chat", "running", "Magnetar `!save`", [READY, ("probe", "chat !save")], saved=True),
        QuasarCase(
            "q-run-restart-chat",
            "running",
            "Quasar.Agent `!restart` (10 s countdown)",
            [READY, ("probe", "chat !restart")],
            launches=1,
            cause=INGAME,
            saved=True,
        ),
        QuasarCase(
            "q-run-restart-seconds",
            "running",
            "Quasar.Agent `!restart 30` (30 s countdown, SE1-0187)",
            [READY, ("probe", "chat !restart 30")],
            launches=1,
            cause=INGAME,
            saved=True,
            replies=["Server restart scheduled in 30 seconds."],
        ),
        QuasarCase(
            "q-restart-help",
            "running",
            "Quasar.Agent `!restart help`",
            [READY, ("probe", "chat !restart help")],
            replies=["Usage: !restart [seconds]"],
        ),
        QuasarCase(
            "q-chatpage-save",
            "running",
            "`!save` from Quasar's Chat page (RunChatCommand, DIS-0017)",
            [READY, ("chatpage", "!save")],
            saved=True,
            windows=False,
        ),
        QuasarCase(
            "q-reload",
            "running",
            "Essentials `!reload` (saves, then reloads the DS config)",
            [READY, ("probe", "chat !reload")],
            saved=True,
            replies=["Configuration reloaded."],
            essentials_src=True,
        ),
        QuasarCase(
            "q-fixshipmodid",
            "running",
            "Essentials `!fixshipmodid 123` (an argument to a short form)",
            [READY, ("probe", "chat !fixshipmodid 123")],
            replies=["Grid not found"],
            essentials_src=True,
        ),
        QuasarCase(
            "q-run-restart-ess",
            "running",
            "Essentials `!ess restart`",
            [READY, ("probe", "chat !ess restart")],
            launches=1,
            cause=INGAME,
            saved=True,
        ),
        QuasarCase(
            "q-run-restart-ess-auto",
            "running",
            "Essentials timed auto command `Restart` step",
            [READY],
            launches=2,
            cause=INGAME,
            saved=True,
            essentials="restart",
        ),
        QuasarCase(
            "q-run-restart-ds-auto",
            "running",
            "DS timed auto-restart (`autoRestartTimeInMin` 1)",
            [READY],
            launches=2,
            cause=INGAME,
            saved=True,
            auto_restart=True,
        ),
        QuasarCase(
            "q-run-restart-sdk",
            "running",
            "PluginSdk `SaveAndRestart`",
            [READY, ("probe", "worker sdk SaveAndRestart")],
            launches=1,
            cause=INGAME,
            saved=True,
        ),
        QuasarCase(
            "q-run-restart-nosave",
            "running",
            "PluginSdk `RestartWithoutSaving`",
            [READY, ("probe", "worker sdk RestartWithoutSaving")],
            launches=1,
            cause=INGAME,
            saved=False,
        ),
        QuasarCase(
            "q-run-stop-chat",
            "running",
            "Quasar.Agent `!stop`",
            [READY, ("probe", "chat !stop")],
            end_state=STOPPED,
            goal=0,
            saved=True,
        ),
        QuasarCase(
            "q-run-quit-chat",
            "running",
            "Quasar.Agent `!quit`",
            [READY, ("probe", "chat !quit")],
            end_state=STOPPED,
            goal=0,
            saved=False,
        ),
        QuasarCase(
            "q-run-stop-ess",
            "running",
            "Essentials `!ess stop`",
            [READY, ("probe", "chat !ess stop")],
            launches=1,
            cause=CRASH,
            attempts=1,
            saved=True,
            notes="SaveAndQuit tells Quasar nothing, so the exit counts as a crash",
        ),
        QuasarCase(
            "q-run-stop-sdk",
            "running",
            "PluginSdk `SaveAndQuit`",
            [READY, ("probe", "worker sdk SaveAndQuit")],
            launches=1,
            cause=CRASH,
            attempts=1,
            saved=True,
            notes="SaveAndQuit tells Quasar nothing, so the exit counts as a crash",
        ),
        QuasarCase(
            "q-run-quit-sdk",
            "running",
            "PluginSdk `QuitWithoutSaving`",
            [READY, ("probe", "worker sdk QuitWithoutSaving")],
            end_state=STOPPED,
            goal=0,
            saved=False,
            notes="Terminating(Shutdown) makes the agent report an admin stop",
        ),
        QuasarCase(
            "q-run-exit-ds",
            "running",
            "DS's own exit (`ExitThreadSafe`)",
            [READY, ("probe", "exit")],
            launches=1,
            cause=CRASH,
            attempts=1,
            saved=True,
        ),
        QuasarCase(
            "q-run-sigterm",
            "running",
            "SIGTERM to Magnetar from outside",
            [READY, ("signal", "TERM")],
            launches=1,
            cause=CRASH,
            attempts=1,
            saved=True,
            windows=False,
        ),
        QuasarCase(
            "q-run-kill",
            "running",
            "SIGKILL to Magnetar from outside",
            [READY, ("signal", "KILL")],
            launches=1,
            cause=CRASH,
            attempts=1,
            saved=False,
        ),
        QuasarCase(
            "q-run-crash",
            "running",
            "Magnetar crashes (unhandled exception)",
            [READY, ("probe", "crash")],
            launches=1,
            cause=CRASH,
            attempts=1,
            saved=False,
        ),
        QuasarCase(
            "q-run-hang",
            "hung",
            "Game thread hangs for 120 s",
            [READY, ("probe", "hang 120"), ("sleep", 60)],
            launches=1,
            cause=HEALTH,
            settle=60,
            notes="Health policy: simulation progress",
        ),
    ]


def _ui():
    return [
        QuasarCase(
            "q-ui-restart",
            "running",
            "UI Restart",
            [READY, ("ui", "Restart server")],
            launches=1,
            cause=MANUAL,
            saved=True,
            windows=False,
        ),
        QuasarCase(
            "q-ui-stop",
            "running",
            "UI Stop",
            [READY, ("ui", "Stop server")],
            end_state=STOPPED,
            goal=0,
            saved=True,
            windows=False,
        ),
        QuasarCase(
            "q-ui-start",
            "stopped",
            "UI Start after UI Stop",
            [READY, ("ui", "Stop server"), ("state", STOPPED), ("ui", "Start server"), ("ready", 2)],
            launches=1,
            windows=False,
        ),
        QuasarCase(
            "q-ui-kill-starting",
            "starting",
            "UI Kill while the server starts (plugin init held 120 s)",
            [("init",), ("ui", "Kill starting server")],
            end_state=STOPPED,
            goal=0,
            hold_init=120,
            windows=False,
        ),
        QuasarCase(
            "q-ui-restart-saving",
            "saving",
            "UI Restart while a save runs",
            [READY, *SAVING, ("ui", "Restart server")],
            launches=1,
            cause=MANUAL,
            saved=True,
            windows=False,
        ),
        QuasarCase(
            "q-ui-stop-ingame-restart",
            "restarting",
            "UI Stop while an in-game restart saves",
            [READY, *RESTARTING, ("ui", "Stop server")],
            launches=1,
            end_state=STOPPED,
            goal=0,
            saved=True,
            notes="The UI offers no Stop while Quasar restarts the server; it applies after the relaunch",
            windows=False,
        ),
        QuasarCase(
            "q-ingame-restart-ui-stop",
            "stopping",
            "`!ess restart` while a UI Stop saves",
            [READY, ("probe", "slowsave 10"), ("ui", "Stop server"), ("sleep", 2), ("probe", "chat !ess restart")],
            end_state=STOPPED,
            goal=0,
            saved=True,
            windows=False,
        ),
        QuasarCase(
            "q-ingame-restart-ui-restart",
            "restarting",
            "UI Restart while an in-game restart saves",
            [READY, *RESTARTING, ("ui", "Restart server")],
            launches=2,
            cause=MANUAL,
            saved=True,
            notes="The UI offers no Restart while Quasar restarts the server; it restarts again after the relaunch",
            windows=False,
        ),
        QuasarCase(
            "q-double-restart",
            "running",
            "`!ess restart` and `SaveAndRestart` at once",
            [READY, ("probe", "chat !ess restart", "worker sdk SaveAndRestart")],
            launches=1,
            cause=INGAME,
            saved=True,
        ),
        QuasarCase(
            "q-restart-saving",
            "saving",
            "`!ess restart` while a save runs",
            [READY, *SAVING, ("probe", "chat !ess restart")],
            launches=1,
            cause=INGAME,
            saved=True,
        ),
        QuasarCase(
            "q-quasar-stop",
            "running",
            "Quasar worker stops (SIGTERM)",
            [READY, ("quasar", "TERM")],
            end_state=None,
            saved=None,
            notes="What happens to the server when Quasar itself stops",
            windows=False,
        ),
    ]


CASES = _ingame() + _ui()
