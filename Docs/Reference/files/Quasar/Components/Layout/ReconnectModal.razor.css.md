# Quasar/Components/Layout/ReconnectModal.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Styles and animations for the `ReconnectModal` dialog. Controls per-state visibility of message paragraphs, the slide-up/fade-in/fade-out dialog animations, and the ripple reconnect animation.

## Structure
**Visibility rules:**
- All state-specific paragraphs and the animation are `display: none` by default.
- Shown selectively when the `<dialog>` carries the matching Blazor-assigned state class (`components-reconnect-show`, `components-reconnect-retrying`, `components-reconnect-failed`, `components-reconnect-paused`, `components-reconnect-resume-failed`).

**Dialog chrome (`#components-reconnect-modal`):**
- Uses MudBlazor CSS custom properties (`--mud-palette-surface`, `--mud-palette-text-primary`, `--mud-palette-lines-default`, `--mud-palette-primary`) for theme compatibility.
- 20 rem wide, centered at 20 vh, slide-up + fade-in on `[open]` via CSS animations.
- Backdrop: semi-transparent black with fade-in animation.

**Keyframes:**
- `components-reconnect-modal-slideUp` — translateY 30 px → 0, scale 0.95 → 1.
- `components-reconnect-modal-fadeInOpacity` / `fadeOutOpacity` — opacity transitions.
- `components-rejoining-animation` — dual-ring ripple expanding from center outward.

**Button styling:** MudBlazor primary palette, brightness hover/active states.

## Dependencies
- MudBlazor CSS custom properties (runtime theming).
