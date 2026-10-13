---
name: quasar-book
description: Handbook for admins of Quasar, the Space Engineers 1 dedicated server manager (web UI, supervisor, Quasar.Agent, Quasar.Host, clusters). Read it to answer how to install, configure, update, back up and troubleshoot Quasar and the Magnetar servers it runs. It is written from the Quasar repo's code and docs.
---

# Quasar handbook

Quasar runs and manages Space Engineers 1 dedicated servers through Magnetar, with a web UI. This handbook answers admin and player questions about it: installing it, setting up servers, plugins, updates, backups, clusters, diagnostics and common failures.

Start at [TOC.md](TOC.md), which lists the pages by topic. [Index.md](Index.md) lists terms, settings, files and commands alphabetically, each with the page that explains it. Search the pages with `grep -ri <term> *.md` from this folder.

Each page ends with a Sources list of files in the Quasar repository, https://github.com/CometWorks/quasar, which the page was written from. Point people to the repository's `Docs/` pages or the release page (https://github.com/CometWorks/quasar/releases) when they need more than the handbook says.

[OpenQuestions.md](OpenQuestions.md) lists what the code leaves unclear or what the docs describe but the code doesn't do yet. Don't answer those from the handbook. Say it isn't documented, or check the repository.

## Version

The pages describe Quasar at the commit recorded in `data/sources.json`. Quasar changes often. If an answer depends on a detail that may have changed, check the source files listed on the page.

## Keeping it current

Maintainers only, readers don't need this. After a Quasar release:

1. `python3 data/book.py stale` lists the pages whose source files changed since the last refresh, and new docs no page covers yet.
2. Reread the changed sources and update only those pages and their Sources lists. Update `TOC.md`, `Index.md` and `OpenQuestions.md` when topics or terms change.
3. `python3 data/book.py check` verifies links, anchors, Sources lists and dashes.
4. `python3 data/book.py record` stores the new source hashes and the Quasar commit.

Write in plain language, following the humanizer rules: no em dashes, plain verbs, present tense, no filler.
