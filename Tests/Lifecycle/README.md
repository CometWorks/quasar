# Lifecycle test suite under Quasar

Runs one Quasar worker per case, each with an install root, web port and DS
port of its own, and sends each stop and restart command to its one server:
in-game commands, plugin `ServerControl` calls, the DS's own exit and timed
auto-restart, signals, crashes and hangs, and Quasar's own Start, Stop,
Restart and Kill from its UI. Then it checks what Quasar made of it: its
process state and goal, the restart cause and crash counter in
`supervisor-state.json`, how many Magnetar processes it started and whether
two ran at once, the world save, and agent socket transport errors in its log.

The Magnetar side, the probe plugin and the rig come from the magnetar
repository's `Tests/Lifecycle` (`--magnetar-src`); its README explains the
probe commands. `cases.py` here is the Quasar matrix.

## Setup

- **Quasar.** Build `Quasar/Quasar.csproj -c Release` with `MagnetarBin`
  pointing at the Magnetar build's `Libraries/MagnetarInterim`, so the bundled
  agent matches it.
- **Template.** `--template` is a Quasar install root to copy per case:
  `ConfigProfiles/rt-config/profile.json` (any profile; the runner sets its
  plugins to Essentials and the probe and its timed auto-restart, and
  `autoSaveInMinutes` should be 0), `Magnetars/rt1/server.json` with
  `configProfileId` `rt-config` and `worldSaveName` `rt1`,
  `Magnetars/rt1/DedicatedServer/Saves/rt1` (a small world),
  `Magnetars/rt1/Magnetar/GitHub` and `Sources` from a run that has loaded
  Essentials, `ManagedMagnetar` (a Magnetar release, so Quasar doesn't fetch
  one) and `Caches/plugin-catalog.json`.
- **Tools.** `--ds-root` is a DS install (with `DedicatedServer64`);
  `--steamcmd` holds `SteamCMD` and `SteamCmdHome`. Launch updates are off
  (`QUASAR_DS_LAUNCH_UPDATE_MODE=Skip`).
- **Browser.** `pip install playwright` into the Python that runs the suite.
  Either set `LIFECYCLE_CHROMIUM` to a Chromium binary or run `playwright
  install chromium-headless-shell`. If the machine lacks its libraries
  (`libnss3`, `libnspr4`), extract them from the distribution's packages and
  name their folders in `LIFECYCLE_CHROMIUM_LIBS`; only the browser gets them.
  Quasar's UI needs no login from loopback.

```bash
Tests/Lifecycle/run.py --magnetar-src <magnetar checkout> --install <Magnetar install> \
    --quasar Quasar/bin/Release/net10.0 --template <template> \
    --ds-root <DS install> --steamcmd <tools> --work <work> --jobs 4
```

`--essentials-src <Essentials working copy>` compiles Essentials from there as a
dev folder instead of MagnetarHub's; `q-reload` and `q-fixshipmodid` need it and
are skipped without it. `q-chatpage-save` types a line into the Chat page, so it
needs the browser too.

Job `i` uses DS port `--port-base + i` (Quasar sets the Steam port to that
plus 1000) and web port `--web-port-base + i`. Each Quasar runs in Production
mode, so health monitoring is on. A case takes 1.5 to 4 minutes.

## Under Proton

`--platform proton-interim` or `proton-legacy` runs the Windows `Quasar.exe`
(`dotnet publish -r win-x64 --self-contained false`) under Proton, with
`--install` pointing at a Windows Magnetar package and `ManagedMagnetar` in
the template holding one too. Each job needs a prefix of its own (`--prefix`,
comma separated) with the .NET 10 runtime and ASP.NET Core 10 in `C:\Program
Files\dotnet`, and .NET Framework 4.8 for MagnetarLegacy (see the magnetar
suite's README).

Quasar's UI does not work under Wine: ASP.NET Core data protection fails in
Wine's BCrypt, so every page answers 500. The supervisor and the agent work,
so the in-game, plugin, crash and hang cases run there; the UI cases and the
signal cases run on Linux only.
