import * as THREE from "three";
import { state } from "./state.js";
import { getContentFolderCacheGeneration, resolveContentFile } from "./content-folder.js";
import { loadTexture } from "./texture-loader.js";

const GAME_GUI_TEXT_SCALE = 144 / 185;
const FONT_PATHS = new Map([
    ["debug", "Fonts/white_shadow/FontDataPA.xml"],
    ["monospace", "Fonts/monospace/FontDataPA.xml"],
]);

let fontCacheGeneration = -1;
const loadedFonts = new Map();
const fontPromises = new Map();

export function supportedLcdFontId(font) {
    const key = String(font || "").trim().toLowerCase();
    return key.includes("monospace") ? "monospace" : "debug";
}

export function getLoadedLcdBitmapFont(font) {
    ensureFontCacheGeneration();
    return loadedFonts.get(supportedLcdFontId(font)) || null;
}

export async function loadLcdBitmapFont(font) {
    ensureFontCacheGeneration();
    const generation = fontCacheGeneration;
    const id = supportedLcdFontId(font);
    if (loadedFonts.has(id)) return loadedFonts.get(id);
    if (fontPromises.has(id)) return await fontPromises.get(id);

    const promise = loadLcdBitmapFontUncached(id);
    fontPromises.set(id, promise);
    try {
        const loaded = await promise;
        if (generation === fontCacheGeneration) loadedFonts.set(id, loaded);
        return loaded;
    } finally {
        fontPromises.delete(id);
    }
}

export function lcdBitmapTextScale(surfaceScale, canvas, useSurfaceFontScale) {
    const scale = Math.max(0, Number(surfaceScale) || 0);
    const spriteScale = useSurfaceFontScale ? scale * Math.min(canvas.width, canvas.height) / 512 : scale;
    return spriteScale * GAME_GUI_TEXT_SCALE;
}

export function measureLcdBitmapLine(font, text, renderScale) {
    const chars = Array.from(String(text || ""));
    let width = 0;
    let previous = "\0";
    for (let i = 0; i < chars.length; i++) {
        const glyph = glyphForChar(font, chars[i]);
        if (!glyph) continue;
        width += kern(font, previous, chars[i]) * renderScale;
        previous = chars[i];
        width += glyph.advanceWidth * renderScale;
        if (i < chars.length - 1) width += font.spacing * renderScale;
    }
    return width;
}

export function drawLcdBitmapText(ctx, font, text, color, renderScale, x, y, width, alignment = "LEFT") {
    if (!font || renderScale <= 0) return false;
    const rgba = normalizeColor(color || { r: 255, g: 255, b: 255, a: 255 });
    const align = String(alignment || "LEFT").toUpperCase();
    const lines = String(text || "").replace(/\r\n/g, "\n").split("\n");

    ctx.save();
    ctx.imageSmoothingEnabled = true;
    for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
        const line = lines[lineIndex];
        const chars = Array.from(line);
        const lineWidth = measureLcdBitmapLine(font, line, renderScale);
        let atX = x;
        if (align === "RIGHT") atX += width - lineWidth;
        else if (align === "CENTER") atX += (width - lineWidth) / 2;

        let previous = "\0";
        for (let i = 0; i < chars.length; i++) {
            const char = chars[i];
            const glyph = glyphForChar(font, char);
            if (!glyph) continue;

            atX += kern(font, previous, char) * renderScale;
            previous = char;
            const bitmap = font.bitmaps.get(glyph.bitmapId);
            if (bitmap && bitmap.canvas) {
                const tinted = tintedBitmapCanvas(bitmap, rgba);
                const drawX = atX + glyph.leftSideBearing * renderScale;
                const drawY = y + (glyph.leftSideBearing + glyph.heightOffset + 3.8333333 + lineIndex * font.lineHeight) * renderScale;
                ctx.drawImage(
                    tinted,
                    glyph.x,
                    glyph.y,
                    glyph.width,
                    glyph.height,
                    drawX,
                    drawY,
                    glyph.width * renderScale,
                    glyph.height * renderScale);
            }

            atX += (glyph.advanceWidth - glyph.leftSideBearing) * renderScale;
            if (i < chars.length - 1) atX += font.spacing * renderScale;
        }
    }
    ctx.restore();
    return true;
}

async function loadLcdBitmapFontUncached(id) {
    const xmlPath = FONT_PATHS.get(id);
    const resolved = await resolveContentFile(xmlPath);
    if (!resolved) throw new Error(`Missing local LCD font: ${xmlPath}`);

    const file = await resolved.getFile();
    const definition = parseBitmapFontXml(await file.text(), xmlPath, id);
    await Promise.all(Array.from(definition.bitmaps.values()).map(bitmap => loadBitmapCanvas(definition, bitmap)));
    state.stats["LCD fonts loaded"] = loadedFonts.size + 1;
    return definition;
}

function parseBitmapFontXml(text, xmlPath, id) {
    const document = new DOMParser().parseFromString(text, "application/xml");
    const parseError = document.querySelector("parsererror");
    if (parseError) throw new Error(parseError.textContent || `invalid font XML: ${xmlPath}`);

    const root = firstElementByLocalName(document, "font");
    if (!root) throw new Error(`Font XML has no font root: ${xmlPath}`);

    const font = {
        id,
        xmlPath,
        directory: parentPath(xmlPath),
        spacing: 1,
        baseline: parseInteger(root.getAttribute("base"), 0),
        lineHeight: parseInteger(root.getAttribute("height"), 0),
        bitmaps: new Map(),
        glyphs: new Map(),
        kernPairs: new Map(),
        replacementGlyph: null,
    };

    for (const node of elementsByLocalName(document, "bitmap")) {
        const size = parseSize(node.getAttribute("size"));
        font.bitmaps.set(parseInteger(node.getAttribute("id"), 0), {
            id: parseInteger(node.getAttribute("id"), 0),
            name: node.getAttribute("name") || "",
            width: size.width,
            height: size.height,
            canvas: null,
            imageData: null,
            tintedCanvases: new Map(),
        });
    }

    for (const node of elementsByLocalName(document, "glyph")) {
        const code = parseGlyphCode(node);
        if (!Number.isFinite(code)) continue;
        const origin = parsePair(node.getAttribute("loc") || node.getAttribute("origin"));
        const size = parseSize(node.getAttribute("size"));
        const glyph = {
            char: String.fromCodePoint(code),
            bitmapId: parseInteger(node.getAttribute("bm"), 0),
            x: origin.x,
            y: origin.y,
            width: size.width,
            height: size.height,
            advanceWidth: parseInteger(node.getAttribute("aw"), 0),
            leftSideBearing: parseInteger(node.getAttribute("lsb"), 0),
            heightOffset: parseInteger(node.getAttribute("ho"), 0),
        };
        font.glyphs.set(glyph.char, glyph);
        if (glyph.char === "□") font.replacementGlyph = glyph;
    }

    for (const node of elementsByLocalName(document, "kernpair")) {
        const left = firstCodePointChar(node.getAttribute("left"));
        const right = firstCodePointChar(node.getAttribute("right"));
        if (left && right) font.kernPairs.set(`${left}|${right}`, parseInteger(node.getAttribute("adjust"), 0));
    }

    return font;
}

async function loadBitmapCanvas(font, bitmap) {
    if (!bitmap.name) throw new Error(`LCD font ${font.id} has a bitmap with no file name.`);
    const texture = await loadTexture(joinPath(font.directory, bitmap.name), "FontAtlas");
    const canvas = textureToCanvas(texture, bitmap.width, bitmap.height);
    bitmap.canvas = canvas;
    bitmap.imageData = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
}

function textureToCanvas(texture, width, height) {
    if (!state.renderer) throw new Error("LCD font atlas decode requires an active WebGL renderer.");
    const renderer = state.renderer;
    const renderTarget = new THREE.WebGLRenderTarget(width, height, {
        depthBuffer: false,
        stencilBuffer: false,
        format: THREE.RGBAFormat,
        type: THREE.UnsignedByteType,
        colorSpace: THREE.SRGBColorSpace,
    });
    renderTarget.texture.name = `decoded:${texture.name || "lcd-font-atlas"}`;

    const scene = new THREE.Scene();
    const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    const geometry = new THREE.PlaneGeometry(2, 2);
    const material = new THREE.MeshBasicMaterial({ map: texture, transparent: true, blending: THREE.NoBlending });
    scene.add(new THREE.Mesh(geometry, material));

    const previousTarget = renderer.getRenderTarget();
    const previousClearAlpha = renderer.getClearAlpha();
    const previousClearColor = renderer.getClearColor(new THREE.Color());
    const pixels = new Uint8Array(width * height * 4);
    try {
        renderer.setRenderTarget(renderTarget);
        renderer.setClearColor(0x000000, 0);
        renderer.clear(true, false, false);
        renderer.render(scene, camera);
        renderer.readRenderTargetPixels(renderTarget, 0, 0, width, height, pixels);
    } finally {
        renderer.setRenderTarget(previousTarget);
        renderer.setClearColor(previousClearColor, previousClearAlpha);
        geometry.dispose();
        material.dispose();
        renderTarget.dispose();
    }

    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext("2d");
    const imageData = ctx.createImageData(width, height);
    for (let y = 0; y < height; y++) {
        const sourceOffset = y * width * 4;
        const targetOffset = y * width * 4;
        imageData.data.set(pixels.subarray(sourceOffset, sourceOffset + width * 4), targetOffset);
    }
    ctx.putImageData(imageData, 0, 0);
    return canvas;
}

function tintedBitmapCanvas(bitmap, color) {
    const key = `${color.r},${color.g},${color.b},${color.a}`;
    if (bitmap.tintedCanvases.has(key)) return bitmap.tintedCanvases.get(key);

    const canvas = document.createElement("canvas");
    canvas.width = bitmap.canvas.width;
    canvas.height = bitmap.canvas.height;
    const ctx = canvas.getContext("2d");
    const imageData = ctx.createImageData(canvas.width, canvas.height);
    const source = bitmap.imageData;
    const target = imageData.data;
    for (let i = 0; i < source.length; i += 4) {
        target[i] = source[i] * color.r / 255;
        target[i + 1] = source[i + 1] * color.g / 255;
        target[i + 2] = source[i + 2] * color.b / 255;
        target[i + 3] = source[i + 3] * color.a / 255;
    }
    ctx.putImageData(imageData, 0, 0);
    bitmap.tintedCanvases.set(key, canvas);
    return canvas;
}

function glyphForChar(font, char) {
    const glyph = font.glyphs.get(char);
    if (glyph) return glyph;
    if (font.replacementGlyph && canUseReplacementGlyph(char)) return font.replacementGlyph;
    return null;
}

function canUseReplacementGlyph(char) {
    return !/\s/.test(char) && !/[\u0000-\u001f\u007f-\u009f]/.test(char);
}

function kern(font, left, right) {
    return font.kernPairs.get(`${left}|${right}`) || 0;
}

function normalizeColor(color) {
    color = color || {};
    return {
        r: clampByte(color.r ?? color.R ?? 255),
        g: clampByte(color.g ?? color.G ?? 255),
        b: clampByte(color.b ?? color.B ?? 255),
        a: clampByte(color.a ?? color.A ?? 255),
    };
}

function ensureFontCacheGeneration() {
    const generation = getContentFolderCacheGeneration();
    if (generation === fontCacheGeneration) return;
    loadedFonts.clear();
    fontPromises.clear();
    fontCacheGeneration = generation;
}

function elementsByLocalName(document, name) {
    return Array.from(document.getElementsByTagName("*")).filter(node => node.localName === name);
}

function firstElementByLocalName(document, name) {
    return elementsByLocalName(document, name)[0] || null;
}

function parseGlyphCode(node) {
    const code = node.getAttribute("code");
    if (code) return parseInt(code, 16);
    const char = firstCodePointChar(node.getAttribute("ch"));
    return char ? char.codePointAt(0) : NaN;
}

function firstCodePointChar(value) {
    return Array.from(String(value || ""))[0] || "";
}

function parsePair(value) {
    const [x, y] = String(value || "0,0").split(",");
    return { x: parseInteger(x, 0), y: parseInteger(y, 0) };
}

function parseSize(value) {
    const [width, height] = String(value || "0x0").split("x");
    return { width: parseInteger(width, 0), height: parseInteger(height, 0) };
}

function parseInteger(value, fallback) {
    const parsed = Number.parseInt(String(value || ""), 10);
    return Number.isFinite(parsed) ? parsed : fallback;
}

function parentPath(path) {
    const value = String(path || "").replaceAll("\\", "/");
    const index = value.lastIndexOf("/");
    return index >= 0 ? value.slice(0, index) : "";
}

function joinPath(parent, child) {
    return parent ? `${parent}/${child}` : child;
}

function clampByte(value) {
    return Math.max(0, Math.min(255, Math.round(Number(value) || 0)));
}
