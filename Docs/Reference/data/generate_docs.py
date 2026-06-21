#!/usr/bin/env python3
"""Generate the module description files, Index.md and TOC.md.

Reads data/module_index.json and data/reference_graph.json. Module overview prose
is human-synthesized and embedded here so the whole tree can be regenerated cheaply
on a re-run (descriptions change -> rerun build_graph.py then this).
"""
import json
import os

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))          # Docs/Reference
REPO = os.path.abspath(os.path.join(ROOT, "..", ".."))
modidx = json.load(open(os.path.join(HERE, "module_index.json")))
graph = json.load(open(os.path.join(HERE, "reference_graph.json")))
pathmap = json.load(open(os.path.join(HERE, "path_map.json")))

# path -> module
path_mod = {f["path"]: m for m, fs in modidx.items() for f in fs}


def doc_link(src_path, prefix):
    """Relative link to a file's description doc, honoring path_map disambiguation.

    path_map stores repo-relative targets under Docs/Reference/; `prefix` makes
    them relative to the file being written ("" from Index/TOC, "../" from Modules).
    """
    target = pathmap.get(src_path, f"Docs/Reference/files/{src_path}.md")
    rel = target[len("Docs/Reference/"):]  # -> files/<...>.md
    return prefix + rel

# Ordered modules: (key, title, overview)
MODULES = [
    ("Magnetar.Protocol", "Magnetar.Protocol — Shared Wire Contracts",
     "Shared `netstandard2.0` contract library referenced by the Quasar supervisor, the "
     "in-DS [Quasar.Agent](Quasar.Agent.md), and [Quasar.Bootstrap](Quasar.Bootstrap.md). "
     "It defines the entire agent↔supervisor wire protocol: the tagged-union `AgentWireMessage` "
     "envelope with `WireMessageKind` discriminators, the server-command request/response triple "
     "(`ServerCommandEnvelope` / `ServerCommandResult` / `ServerCommandType`), the handshake "
     "(`AgentHello`) and periodic telemetry (`AgentSnapshot` carrying `ServerMetrics`, `PlayerSnapshot`, "
     "chat/death events and plugin info), the entity-browser and plugin-config DTOs, the "
     "`WebServiceDiscoveryManifest` used to locate a running supervisor, the `IQuasarConfigProvider` "
     "bridge, and runtime helpers (`MagnetarPaths`, `QuasarActiveReleasePointer`, "
     "`QuasarReleaseVersion`, `QuasarWebReleaseLayout`). It has zero external "
     "dependencies by design so it can also load inside the .NET-Framework game process."),

    ("Quasar.Host", "Quasar.Host — Application Host & Wiring",
     "The Blazor Server application host and composition root. `Program.cs` builds the dependency-injection "
     "graph (the singletons and hosted services that make up the supervisor), configures Steam OpenID "
     "authentication with role-based authorization policies and a trusted-network bypass, registers the "
     "Razor components and MudBlazor, and maps the HTTP/WebSocket endpoints — `/ws/agent` for agents, "
     "`/api/health` and `/api/discovery` for discovery/health, `/api/internal/drain` for graceful handoff, "
     "and the login/logout flow. This module also holds the project file, `appsettings`, launch profile, and "
     "the `wwwroot` static assets (global CSS and the JS-interop helpers)."),

    ("Quasar.Models", "Quasar.Models — Domain Models",
     "Plain domain-model layer with no behaviour or DI. It centres on the managed DS instance: the persisted "
     "`DedicatedServerDefinition`, the volatile `DedicatedServerRuntimeSnapshot`, and the "
     "lifecycle enums (goal / process / health state and process priority). It also defines the config-profile "
     "model (`QuasarConfigProfile`, covering world-root and ~90 session settings plus plugins/mods), world "
     "templates, branding settings and palette, and known-player records. These types are produced and consumed "
     "throughout [Quasar.Services.Core](Quasar.Services.Core.md) and the [UI](Quasar.Components.md)."),

    ("Quasar.Services.Core", "Quasar.Services.Core — Supervisor Engine & Support Services",
     "The heart of the supervisor and its supporting services. `DedicatedServerSupervisor` is an `IHostedService` "
     "whose 2-second reconcile loop starts/stops/restarts DS processes to match goal state, evaluates instance "
     "health (agent heartbeat, simulation-frame progress, uptime thresholds), and persists/adopts process state "
     "across Quasar restarts. `AgentRegistry` and `AgentSocketHandler` own the WebSocket agents and route "
     "fire-and-forget and request/response commands. `DedicatedServerRuntimePreparer`, "
     "`ManagedDedicatedServerRuntimeResolver` and the warmup service check/download managed SteamCMD, Magnetar and "
     "Dedicated Server prerequisites, gate managed launches until ready, stage the managed runtime, and write every "
     "config artifact before launch. A family of file-backed, live-reloading catalogs persist instance "
     "definitions, config profiles, world templates, known players, plugins, dev folders and Steam credentials, "
     "all on top of `AtomicFileWriter`. The module also provides branding, theming, NLog configuration, the "
     "server-side file browser, the entity service, and the web-service discovery/options/state plumbing."),

    ("Quasar.Services.Analytics", "Quasar.Services.Analytics — Metrics Storage",
     "A lightweight round-robin-database (RRD) style metrics pipeline. `MetricsStoreService` (hosted) ingests "
     "`MetricSample`s derived from agent snapshots through a bounded, drop-oldest channel and periodically "
     "persists them. Per server, `ServerMetricsStore` keeps three consolidation tiers — raw (1 hour), "
     "1-minute (1 week) and 1-hour (90 days) — built on `RrdCircularBuffer` and `RrdRollupBuffer`. "
     "`AnalyticsViewConfig` captures the dashboard's per-panel layout preferences."),

    ("Quasar.Services.Auth", "Quasar.Services.Auth — Authentication & RBAC",
     "Authentication and role-based access control. Steam OpenID, plus an optional trusted-network bypass, map "
     "users onto three roles (viewer / editor / admin) and a set of named authorization policies. "
     "`QuasarRoleMapper` builds claims principals by consulting the file-backed `RbacConfigCatalog` (`rbac.json`), "
     "`QuasarAuthOptions` reads provider configuration from the `Quasar:Auth` section, "
     "`QuasarAuthSettingsService` persists Security-page trusted-network edits into `appsettings.json`, and "
     "`TrustedNetworkEvaluator` recognises loopback and same-subnet clients after rejecting unaccepted proxy "
     "forwarding headers. Consumed by "
     "[Program.cs](../files/Quasar/Program.cs.md) and the [Security page](../files/Quasar/Components/Pages/Security.razor.md)."),

    ("Quasar.Services.Discord", "Quasar.Services.Discord — Discord Integration",
     "Optional Discord.Net integration. `DiscordBotService` (hosted) owns the `DiscordSocketClient` lifecycle and "
     "orchestrates sub-services: bidirectional chat relay, templated death-event relay, log-file tailing relay, "
     "simspeed degradation alerts, and periodic analytics export. A command router and dispatcher expose server-admin verbs (chat, save, "
     "start/stop/restart, kick/ban/unban, promote/demote, status) from Discord, throttled by a per-channel rate "
     "limiter. Bot configuration and death-message templates are file-backed catalogs following the same "
     "watch-and-debounce pattern as the rest of the project."),

    ("Quasar.Services.PluginSdk", "Quasar.Services.PluginSdk — Plugin Config & Log Bridge",
     "Bridge to the Magnetar PluginSdk. `PluginConfigService` (hosted) caches per-agent plugin-config snapshots "
     "and routes update requests back to the originating agent over the wire, evicting stale entries when agents "
     "disconnect. Its DTOs mirror the PluginSdk config envelope and schema. `PluginLogStream` parses structured "
     "plugin log lines and keeps a bounded ring buffer per instance for the UI's log panels."),

    ("Quasar.Components", "Quasar.Components — Blazor UI",
     "The Blazor Server user interface, built with MudBlazor. Routable pages cover the home dashboard and "
     "first-run setup wizard, the servers control surface, the config-template editor, world templates, players, "
     "the entity browser, plugins, analytics, Discord settings, security/RBAC, nodes, and appearance/branding — "
     "each backed by supporting dialogs. Shared pieces include the application shell "
     "(`App` / `Routes` / `_Imports` / `MainLayout` / `NavMenu`), the dashboard `ServerCard` and "
     "`ServerDetailPanel`, the reconnect modal (with its JS module), and the reusable `PluginConfigEditor` and "
     "`PluginLogPanel`. Pages bind directly to the singleton services in "
     "[Quasar.Services.Core](Quasar.Services.Core.md) and subscribe to their `Changed` events for live updates."),

    ("Quasar.Agent", "Quasar.Agent — In-DS Plugin",
     "The plugin loaded inside each Space Engineers Dedicated Server (`netstandard2.0`, referencing the "
     "VRage/Sandbox game assemblies and the Magnetar PluginSdk provided at runtime). `AdminPlugin` is the "
     "`IPlugin` entry point; `AgentConnection` runs the WebSocket reconnect loop, sending hello/snapshot/"
     "plugin-config messages and executing inbound commands; `GameBridge` and `EntityInspector` marshal telemetry "
     "collection and command actions onto the game thread; and `WebServiceLocator` finds an existing supervisor or "
     "triggers [Quasar.Bootstrap](Quasar.Bootstrap.md). If the supervisor stays offline beyond a configurable "
     "window, the agent autonomously saves and stops the server. It speaks the "
     "[Magnetar.Protocol](Magnetar.Protocol.md) contracts."),

    ("Quasar.Bootstrap", "Quasar.Bootstrap — Ensure-Running Helper",
     "A small `net10.0` helper whose job is to make sure the Quasar web service is running. It exposes "
     "`ensure-running`, `serve` and `activate-release` commands, guards startup with a named mutex, and (in "
     "`serve` mode) manages the Quasar worker process lifecycle: start, health-wait, graceful drain via "
     "`/api/internal/drain`, hot reload when the active-release pointer changes, and auto-restart on unexpected "
     "exit. It can be invoked manually or by [Quasar.Agent](Quasar.Agent.md) when the supervisor is missing, and "
     "shares the [Magnetar.Protocol](Magnetar.Protocol.md) discovery/release contracts."),
]

ORDER = [m[0] for m in MODULES]


def cross_module_targets(modkey):
    """Modules referenced by files in this module (excluding itself)."""
    targets = {}
    for f in modidx[modkey]:
        for dep in graph.get(f["path"], []):
            tm = path_mod.get(dep)
            if tm and tm != modkey:
                targets.setdefault(tm, set()).add(f["name"])
    return targets


def write_module(modkey, title, overview):
    files = modidx[modkey]
    lines = [f"# {title}", ""]
    lines.append(f"*Module `{modkey}` — {len(files)} files.* "
                 f"See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).")
    lines.append("")
    lines.append(overview)
    lines.append("")
    lines.append("## Files")
    lines.append("")
    lines.append("| File | Kind | Summary |")
    lines.append("| --- | --- | --- |")
    for f in files:
        rel = doc_link(f['path'], "../")
        summ = f["summary"].replace("|", "\\|")
        kind = (f["kind"] or "").replace("|", "\\|")
        lines.append(f"| [{f['path']}]({rel}) | {kind} | {summ} |")
    lines.append("")
    targets = cross_module_targets(modkey)
    if targets:
        lines.append("## Depends on")
        lines.append("")
        for tm in sorted(targets):
            lines.append(f"- [{tm}]({tm}.md)")
        lines.append("")
    out = os.path.join(ROOT, "Modules", f"{modkey}.md")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, "w", encoding="utf-8").write("\n".join(lines))


for modkey, title, overview in MODULES:
    write_module(modkey, title, overview)

# ---- Index.md (every file, alphabetical by path) ----
allfiles = sorted((f for fs in modidx.values() for f in fs), key=lambda x: x["path"])
idx = ["# Quasar Handbook — File Index", "",
       f"Every documented source file ({len(allfiles)} total), alphabetical by path. "
       "See the [TOC](TOC.md) for the module-oriented view.", "",
       "| File | Module | Kind | Summary |", "| --- | --- | --- | --- |"]
for f in allfiles:
    rel = doc_link(f['path'], "")
    summ = f["summary"].replace("|", "\\|")
    kind = (f["kind"] or "").replace("|", "\\|")
    mod = path_mod[f["path"]]
    idx.append(f"| [{f['path']}]({rel}) | [{mod}](Modules/{mod}.md) | {kind} | {summ} |")
idx.append("")
open(os.path.join(ROOT, "Index.md"), "w", encoding="utf-8").write("\n".join(idx))

# ---- TOC.md ----
total = len(allfiles)
toc = [
    "# Quasar Handbook", "",
    "Generated reference handbook for the **Quasar** stack — a supervisor and management "
    "system for Space Engineers (v1) dedicated server instances. Quasar is a Blazor Server "
    "supervisor that manages DS processes; an in-process plugin (Quasar.Agent) attaches to each "
    "server over raw WebSockets to report telemetry and execute commands; Quasar.Bootstrap keeps "
    "the supervisor running; and Magnetar.Protocol defines the shared contracts between them.", "",
    "For the hand-written architecture narrative and design rationale, see "
    "[QuasarArchitecture.md](../QuasarArchitecture.md).", "",
    f"This handbook covers **{total} source files** across **{len(MODULES)} modules**. "
    "Browse by module below, or jump to the flat [file Index](Index.md).", "",
    "## Runtime topology", "",
    "```",
    "  Quasar.Bootstrap  ──ensure-running──>  Quasar (Blazor Server supervisor)",
    "                                              │  /ws/agent (WebSocket)",
    "                                              ▼",
    "   each DS process  ◀── commands ──  Quasar.Agent  ── snapshots ──▶  Quasar",
    "                                              ▲",
    "                          Magnetar.Protocol (shared wire contracts)",
    "```", "",
    "## Modules", "",
    "| Module | Files | Summary |",
    "| --- | --- | --- |",
]
short = {
    "Magnetar.Protocol": "Shared wire/discovery contracts and release/runtime helpers between agent and supervisor.",
    "Quasar.Host": "Blazor Server host: DI graph, auth, endpoints, static assets.",
    "Quasar.Models": "Domain models for instances, config profiles, templates, branding.",
    "Quasar.Services.Core": "Supervisor engine, agent registry, runtime preparation, catalogs.",
    "Quasar.Services.Analytics": "RRD-style per-instance metrics storage and persistence.",
    "Quasar.Services.Auth": "Steam OpenID auth, RBAC, trusted-network bypass.",
    "Quasar.Services.Discord": "Discord bot: chat/death/simspeed/log relay, commands, analytics export.",
    "Quasar.Services.PluginSdk": "Plugin config snapshot/update bridge and log streaming.",
    "Quasar.Components": "Blazor/MudBlazor UI: pages, dialogs, shell, shared components.",
    "Quasar.Agent": "In-DS plugin: telemetry, command execution, supervisor attach.",
    "Quasar.Bootstrap": "Ensure-running helper and worker-process lifecycle manager.",
}
for modkey, _, _ in MODULES:
    toc.append(f"| [{modkey}](Modules/{modkey}.md) | {len(modidx[modkey])} | {short[modkey]} |")
toc.append("")

# Module interaction graph (cross-module edges)
toc.append("## Module interactions")
toc.append("")
toc.append("Cross-module references (source module → modules it depends on):")
toc.append("")
for modkey, _, _ in MODULES:
    tg = sorted(cross_module_targets(modkey))
    if tg:
        links = ", ".join(f"[{t}](Modules/{t}.md)" for t in tg)
        toc.append(f"- **[{modkey}](Modules/{modkey}.md)** → {links}")
toc.append("")
toc.append("## Index")
toc.append("")
toc.append("- [Flat file index (every documented file)](Index.md)")
toc.append("- [Architecture narrative](../QuasarArchitecture.md)")
toc.append("")
toc.append("---")
toc.append("")
toc.append("*This handbook was generated by the `structured-documentation` skill. "
           "Per-file descriptions live under `files/`, module overviews under `Modules/`, "
           "and the regeneration scripts and committed cache data under `data/`. "
           "To refresh, re-run `data/build_manifest.py` → `data/make_groups.py` (only if files "
           "were added/removed) → re-describe changed files → `data/build_graph.py` → "
           "`data/generate_docs.py` → `data/linkify_deps.py` → `data/verify_links.py`. "
           "The SHA-256 manifest hashes are the cache key and are platform-independent "
           "(newline-normalized CRLF/CR → LF), so a Windows or Linux checkout yields identical "
           "hashes and only files whose content actually changed are re-described.*")
open(os.path.join(ROOT, "TOC.md"), "w", encoding="utf-8").write("\n".join(toc))

print("Wrote", len(MODULES), "module files, Index.md, TOC.md")
