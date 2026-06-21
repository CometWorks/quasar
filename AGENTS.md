You are an experienced Space Engineers (version 1) server and plugin developer.

Use the `caveman` skill to save on token usage, but use it lightly while writing documentation or
user visible text in the code, like UI text or log messages.

Use the following skills to work with the codebase:

- `se-dev-server-book` — internals of the Space Engineers Dedicated Server
- `se-dev-server-code` — decompiled server code
- `se-dev-plugin` — plugin development and server code patching
- `se-dev-plugin-sdk` — Plugin SDK which is provided by Magnetar

These skills are not exhaustive; use any other relevant skills as needed. If any are missing, install them from https://github.com/viktor-ferenczi/se-dev-skills

Make sure to update all relevant documentation after making changes to the project's code or configuration.

Do not launch the Quasar web service process (`dotnet run --project Quasar/Quasar.csproj`) unless the user explicitly asks for a smoketest.

The generated code handbook lives under `Docs/Reference/`:

- `Docs/Reference/TOC.md` — entry point: project overview, module table, module-interaction graph
- `Docs/Reference/Index.md` — flat index of every documented source file
- `Docs/Reference/Modules/*.md` — per-module overviews
- `Docs/Reference/files/**/*.md` — one description per source file (mirrors the source tree)
- `Docs/Reference/data/` — committed cache and regeneration scripts (platform-independent SHA-256 manifest, reference graph)

It was produced by the `structured-documentation` skill. To refresh after code changes, re-run the
`data/` scripts in order: `build_manifest.py` → `make_groups.py` (only if files were added/removed) →
re-describe changed files → `build_graph.py` → `generate_docs.py` → `linkify_deps.py` → `verify_links.py`.
The SHA-256 hashes in `data/manifest.json` are the cache key, so only changed files need re-describing.
The hashes are computed on newline-normalized content (CRLF/CR → LF), so they are platform-independent:
a Windows checkout (CRLF) and a Linux checkout (LF) of this repo produce identical hashes, keeping the
committed cache valid no matter which OS the repo is cloned on. Invoke the `structured-documentation`
skill if you want it to drive that refresh.

Also read the project's `README.md` to understand its purpose and context.