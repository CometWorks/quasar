# Quasar/wwwroot/viewer/lcd-font-loader.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser-side Space Engineers LCD bitmap font loader for the standalone grid viewer. It supports the game `Debug` and `Monospace` LCD font IDs, resolves their XML font descriptors from the selected local `Content` folder, parses bitmap page, glyph, advance, bearing, line-height, and kerning metadata, loads the referenced DDS atlas pages through the existing texture loader, decodes those GPU textures into canvases through a temporary WebGL render target/readback, and exposes canvas text drawing helpers that preserve the game's glyph metrics and `144/185` GUI text scale.

## Structure

| Export | Purpose |
|---|---|
| `supportedLcdFontId(font)` | Normalizes LCD font names to the first-pass supported set: `debug` or `monospace`. |
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
The loader intentionally does not implement every `Fonts.sbc` subtype. For this pass, unsupported LCD font names normalize to `Debug`, while `Monospace` uses its separate fixed-width atlas. DDS atlas pixels are read back in the same row order used by the viewer's `flipY = false` Space Engineers texture path, then tinted per LCD color into cached canvases so repeated glyph draws avoid per-character pixel manipulation.
