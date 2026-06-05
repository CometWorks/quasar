# Description-file authoring contract

You are generating one Markdown **description file** per source file for the Quasar
project handbook (Quasar = a Blazor Server supervisor that manages Space Engineers
dedicated server instances; Quasar.Agent = an in-DS plugin; Magnetar.Protocol =
shared WebSocket contracts; Quasar.Bootstrap = an ensure-running helper).

For each source file `<path>` write the description to `Docs/Reference/files/<path>.md`
(mirror the source tree exactly, appending `.md`). Create parent directories as needed.

## Format (use exactly this structure)

```
# <relative/source/path>

**Module:** <module key>  **Kind:** <class | interface | enum | record | struct | Blazor component | CSS | JS | JSON config | project file | other>  **Tier:** <1|2|3>

## Summary
<1-4 sentences: what this file is and its role in the system. Be concrete and accurate.>

## Structure
<For C#: namespace; the primary type(s) with base class / implemented interfaces; whether abstract/static/sealed. Then a compact bullet or table of the notable public members (methods, properties, fields, events) with a few words each. For Blazor .razor: the @page route if any, [Inject]ed services, [Parameter]s, key UI sections/dialogs, and JS interop used. For CSS/JS/JSON/csproj: the key sections/keys/targets. Keep it tight — do not paste large code blocks.>

## Dependencies
<Bullet list of the most important other project files this one references (by source path), and notable external packages (MudBlazor, Discord.Net, NLog, SharpCompress, Newtonsoft.Json, Steamworks, VRage/Sandbox game assemblies, etc.). Use source paths like `Quasar/Services/AgentRegistry.cs` so they can be cross-linked later. Only list intra-project references you can actually see (usings, types referenced, DI). If none, write "None".>

## Notes
<Optional: gotchas, threading/concurrency, persistence/atomic writes, platform (Linux/Windows) specifics, security considerations. Omit the section if nothing notable.>
```

## Rules
- Read each file fully before describing it. Be accurate; do not invent members.
- Keep each description focused and token-efficient — this is reference material, not a tutorial.
- For trivial DTO/enum/CSS/JSON files, a short Summary + Structure is enough.
- Skip nothing in your assigned list. Every assigned file gets a description file.
- Do not modify any source files. Only write under `Docs/Reference/files/`.
- Return ONLY a compact report: count of files written, and a 1-line-per-module note of
  the main types/responsibilities you found (this feeds later module synthesis).
