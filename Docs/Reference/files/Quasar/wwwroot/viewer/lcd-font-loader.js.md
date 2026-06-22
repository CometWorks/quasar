# Quasar/wwwroot/viewer/lcd-font-loader.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser-side Space Engineers LCD bitmap font loader for the standalone grid viewer. It supports every built-in font exposed by the LCD font menu, resolves each definition to its `Fonts.sbc` XML descriptor and color mask, parses bitmap page, glyph, advance, bearing, line-height, and kerning metadata, loads the referenced DDS atlas pages through the existing texture loader, decodes those GPU textures into canvases through a temporary WebGL render target/readback, and exposes canvas text drawing helpers that preserve the game's glyph metrics and `144/185` GUI text scale.

## Structure

| Export | Purpose |
|---|---|
| `supportedLcdFontId(font)` | Normalizes LCD font names to supported built-in `Fonts.sbc` definition IDs, falling back to `debug`. |
| `getLoadedLcdBitmapFont(font)` | Returns a cached parsed/decoded bitmap font for the active Content folder when already loaded. |
| `loadLcdBitmapFont(font)` | Loads and caches the normalized font XML plus all bitmap atlas pages for the active Content folder. |
| `lcdBitmapTextScale(surfaceScale, canvas, useSurfaceFontScale)` | Converts Space Engineers LCD surface or sprite text scale into atlas-pixel render scale. |
| `measureLcdBitmapLine(font, text, renderScale)` | Measures one line using parsed glyph advances, spacing, and kerning. |
| `drawLcdBitmapText(ctx, font, text, color, renderScale, x, y, width, alignment)` | Draws tinted bitmap glyphs into a 2D canvas using game-like alignment and line stepping. |

## Dependencies
- `three` for temporary atlas readback render targets.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for the active WebGL renderer and stats.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local font XML resolution and cache-generation invalidation.
- [`Quasar/wwwroot/viewer/texture-loader.js`](texture-loader.js.md) for DDS atlas loading and compressed-texture upload validation.

## Notes
The loader supports the built-in `Fonts.sbc` subtypes exposed by the LCD font menu. `Debug`, `Red`, and `LoadingScreen` use the `white_shadow` atlas, white-family definitions use the non-shadow `white` atlas, `Monospace` uses its separate fixed-width atlas, and color-mask definitions multiply their `Fonts.sbc` mask into the requested LCD text color before glyph tinting. Unknown names fall back to `Debug`. DDS atlas pixels are read back in the same row order used by the viewer's `flipY = false` Space Engineers texture path, unpremultiplied for `FontDataPA` pages before Canvas2D tinting, then cached per effective LCD color so repeated glyph draws avoid per-character pixel manipulation.
