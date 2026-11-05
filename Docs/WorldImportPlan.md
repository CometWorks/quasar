# Direct world import and Worlds page rework

## Objective

Import a Space Engineers save directly into an existing standalone Quasar server
from a folder on the Quasar machine or a browser-uploaded ZIP. Create a new save
and select it without creating a reusable world template. Rework Worlds so this
operation is available there as well as in the server menu. Fix the alignment,
field presentation and wording in the current world import forms.

## Required behavior and boundaries

- Primary entry: **Import World...** in an existing server's menu. Require an
  inactive server with goal Off. Explain why import is unavailable while live.
- Worlds has **Server worlds** and **Templates** tabs. Server worlds lists
  standalone servers with selected save, profile, status and an import action.
  A page import action selects a target server and opens the same import dialog.
- Templates retains the existing library, clone/delete/profile actions and
  predefined worlds. Move template creation into **Add Template...**, using the
  existing quick import flow and the shared folder/ZIP source controls.
- Direct import dialog identifies the target server and current save, accepts
  folder or ZIP input, asks for a new save folder name and offers configuration
  choices. Commit through **Import and Select** and leave the server stopped.
- Keep the current profile with an explicit warning that its settings/mods apply
  at startup, or create a separate profile using imported world settings/mods.
  The latter starts with the current profile so plugins and operational settings
  survive. Preserve Online Mode safety. Never silently change a shared profile.
- Preserve source files and the previous world. Never overwrite an existing save.
  Clear stale world-template provenance on direct import.
- Copy the complete world including voxel files and mod storage, excluding only
  historical Backup content. Require readable, valid Sandbox.sbc. Missing or
  unreadable Sandbox_config.sbc prevents importing its settings, not use of an
  explicitly selected current profile.
- Support ZIP world files at archive root or in an enclosing directory. Reject
  multiple candidate worlds, traversal/absolute paths, links, duplicate targets,
  unsafe/colliding filenames, excessive entry counts and excessive sizes.
- Stream uploaded bytes into private temporary disk storage. Bound uploaded and
  extracted sizes; support progress and cancellation. Clean up owned temporary
  data and profiles on failure. Never delete someone else's files during cleanup.
- Reject unsafe folder links and source/destination overlap. Use the existing
  path resolver so custom saves paths work on Windows and Linux.
- Coordinate import with starts/restores. Guard catalog activation against
  concurrent edit, rename, deletion and goal-state changes. Stage a complete save
  on the destination filesystem, rename it into place, then publish selection.
  A crash may leave unselected files, never an active partial world.
- Enforce server editing permission and config editing permission where needed,
  including service/API boundaries. Viewers retain read-only page access.
- Keep clusters on their existing deployment-aware world workflow.
- Do not launch the Quasar service. Use focused automated tests and build checks.

## UI details

- Use visible field labels, consistent heights/padding, clear validation and
  consistent vertical rhythm. Keep forms at a readable maximum width.
- Align Browse with the path input, with helper/error text below the row. Remove
  the fixed Browse margin. Stack controls on narrow screens.
- Use a modest, vertically resizable optional description field. Group actions
  consistently. Distinguish Save folder name from Template name, Source folder
  from ZIP file, and Import and Select from Create Template.
- Share source selection, validation and progress between server import and
  template creation without coupling their destination-specific behavior.
- Preserve existing routes, setup-guide entry points and template workflows.

## Action points

Read the next action from this file after completing each previous action.
Update the checklist and record verification or material deviations here.

- [x] 1. Trace ownership and invariants. Read repository instructions, relevant
  skills and current server catalog/supervisor, profile import, template import,
  backup exclusion, upload and page/menu flows. Identify reusable implementation
  points and appropriate test fixtures before editing production code.
- [x] 2. Implement safe shared world staging. Add folder and streamed ZIP input,
  root detection, validation, limits, cancellation/progress and owned cleanup.
  Share the applicable copy path with template creation and add focused tests
  for correct content and hostile/failing input.
- [x] 3. Implement guarded direct server import. Integrate maintenance exclusion,
  inactive/Off checks, checked catalog activation, optional isolated profile
  creation and rollback. Preserve identity/network/plugins/previous saves and
  avoid world templates. Test conflict, lifecycle and failure behavior.
- [x] 4. Implement shared source controls and direct import dialog. Add folder
  browsing and streamed ZIP upload, preview/validation/config choice, progress,
  cancellation and explicit commit. Expose service operations through existing
  authorization conventions and an appropriate thin API entry where needed.
- [x] 5. Add the server-menu entry and rework Worlds. Include server selection
  and direct import on Worlds; retain template management and move creation to
  Add Template. Preserve existing callers/routes and refresh affected views.
- [x] 6. Complete form layout and field cleanup in both flows. Replace fixed
  offsets with aligned responsive rows; use visible labels, bounded form widths,
  consistent spacing, inline errors and clear operation-specific wording.
- [x] 7. Update developer documentation and the admin handbook. Cover both input
  types, page/menu paths, profile behavior, safety/limits/cancellation and retained
  saves. Check stale pages, links and source tracking using quasar-book tools.
- [x] 8. Run the smallest sufficient proof and review the complete diff. Build,
  run focused import/security/lifecycle/RBAC checks and relevant existing tests.
  Fix failures; record results, limitations and remaining manual checks. Do not
  start the web service unless the user explicitly requests a smoketest.

## Implementation notes and verification

- Planning baseline: clean working tree before this file was created.
- Existing template save copying lives in ServerEditorDialog, while template
  imports live in QuasarWorldTemplateCatalog. ServerRestoreCoordinator provides
  synchronized start/restore exclusion. DedicatedServerCatalog serializes writes.
- Current Worlds page mixes template creation and the library. Both it and the
  quick import dialog use a fixed one-rem Browse button offset in app.css.
- Existing backup upload uses a 10 GiB bound and streams to disk; reuse the
  established conventions where compatible with raw world ZIP import.

- Action 1 complete: reuse existing catalog write gate, restore/start exclusion,
  config reader and streaming upload conventions. No game/plugin contract changes
  are required; validation stays in Quasar and does not invoke the game runtime.

- Action 2: shared staging implemented; 17 isolated staging checks pass. Full
  project tests initially hit an installed PluginSdk missing clustering types;
  checking the sibling Magnetar SDK build before full validation.

- Action 3 complete: production worker builds against the available compatible
  clustering SDK. A focused test harness compiling the checked-in staging/import
  tests and existing exact-server tests against that worker passes 27 checks.
  The first main-project attempt needed a newer SDK command-alias contract.
  Action 8 resolves that local dependency reference without production changes.

- Action 4 complete: shared source picker, explicit profile selection dialog and
  authorized streaming/folder API compile successfully. Both import destinations
  use the picker; staged template input survives the profile-review step.

- Action 5 complete: Worlds exposes direct import with target selection and row
  actions alongside the retained template library. Both server menus use the
  same dialog. Existing /world-templates URLs remain valid; /worlds also works.

- Action 6 complete: both import forms use visible labels and aligned native
  controls with responsive Browse rows. The hard-coded margin is removed.
  Focused proof now passes 30 checks, including the real dialog event flow and
  in-memory HTTP folder/ZIP imports, viewer denial and stale revisions.

- Action 7 complete: WorldImport.md, README and affected handbook pages now cover
  direct import and the Worlds rework; handbook check passes. Source tracking
  updates are scoped to refreshed pages. Existing shared/unrelated stale source
  hashes are preserved so this pass does not certify unread handbook content.

- Action 8 complete: the normal Quasar.Tests project builds and passes all 168
  selected tests (0 failed, 0 skipped). This includes all new staging/import/API/UI
  tests and relevant existing catalog, profile, path, launch, lifecycle, RBAC,
  viewer, native UI and backup-security regressions. Handbook check and
  `git diff --check` pass.
- Final review tightened source-copy maintenance exclusion, serialized destination
  publication across shared custom saves paths, preserved empty ZIP directories,
  added XML cancellation/bounds, rejected malformed API configuration choices,
  and verified explicit profile permissions and corrupt-archive errors.
- Build reference used for normal-project verification:
  `MagnetarBin=../sdk-rollout/.validation/deps/magnetar/Magnetar/Libraries/MagnetarInterim`.
  It supplies the matching SDK already present locally. The installed SDK is older;
  no installed runtime files or repository dependency settings were changed.
- All action points above are complete. No Quasar service or game process was
  launched. A live browser layout check and real-world game-load smoketest remain
  manual verification, outside this pass's explicit no-service-launch constraint.
- Delivery requested after implementation: commit the feature, push its branch and open a pull request.

- Delivery rebase: feature rebased onto current remote main. The pre-existing
  local version-check commit 72e2f9e6 is patch-equivalent to main's b3cf257c
  (PR #335), so its changes remain present without a duplicate commit.
- Rebase preserves the current runtime discovery, per-file template progress,
  cancellation cleanup, and manual world-backup exclusion. Import and backup
  reservations are mutually exclusive; setup and backup regression tests pass.
- Post-rebase validation: normal project build and 239 selected tests pass,
  including the newly upstream setup, new-server dialog and world-backup tests.
