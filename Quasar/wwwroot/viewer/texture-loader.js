import * as THREE from "three";
import { getContentFolderCacheGeneration, resolveContentFile } from "./content-folder.js";
import { state } from "./state.js";

const MAX_CONCURRENT_TEXTURE_RESOLVES = 24;
const MAX_CONCURRENT_TEXTURE_READS = 6;
const MAX_CONCURRENT_TEXTURE_UPLOADS = 4;
const resolveQueue = createAsyncQueue(MAX_CONCURRENT_TEXTURE_RESOLVES);
const readQueue = createAsyncQueue(MAX_CONCURRENT_TEXTURE_READS);
const uploadQueue = createAsyncQueue(MAX_CONCURRENT_TEXTURE_UPLOADS);
const resolvedTexturePathCache = new Map();
const inFlightTexturePathCache = new Map();
let texturePathCacheGeneration = -1;

export async function resolveTextureAsset(asset) {
    if (!asset || !asset.logicalPath) return null;
    return await resolveTextureFile(asset.logicalPath);
}

export async function loadTexture(logicalPath, slot = "") {
    if (!logicalPath) return null;
    const colorSpaceKey = isNonColorTexture(logicalPath, slot) ? "data" : "color";
    const logicalKey = `${normalizeTextureKey(logicalPath)}|${colorSpaceKey}`;
    if (state.textureLoadPromises.has(logicalKey)) return await state.textureLoadPromises.get(logicalKey);

    const promise = loadTextureUncoalesced(logicalPath, slot, colorSpaceKey);
    state.textureLoadPromises.set(logicalKey, promise);
    try {
        return await promise;
    } finally {
        state.textureLoadPromises.delete(logicalKey);
    }
}

async function loadTextureUncoalesced(logicalPath, slot, colorSpaceKey) {
    const resolved = await resolveQueue(() => resolveTextureFile(logicalPath));
    if (!resolved) {
        const error = new Error(`Missing local texture: ${logicalPath}`);
        error.isMissingLocalTexture = true;
        throw error;
    }

    const file = await resolved.getFile();
    const cacheKey = `${resolved.logicalPath.toLowerCase()}|${file.size}|${file.lastModified || 0}|${colorSpaceKey}`;
    if (state.textureCache.has(cacheKey)) return await state.textureCache.get(cacheKey);

    const promise = loadResolvedTexture(resolved, file, slot);
    state.textureCache.set(cacheKey, promise);
    try {
        const texture = await promise;
        state.textureCache.set(cacheKey, texture);
        return texture;
    } catch (error) {
        state.textureCache.delete(cacheKey);
        throw error;
    }
}

export async function resolveTextureFile(logicalPath) {
    const path = String(logicalPath || "").trim();
    if (!path) return null;
    const generation = ensureTexturePathCacheGeneration();
    const cacheKey = normalizeTextureKey(path);
    if (resolvedTexturePathCache.has(cacheKey)) return resolvedTexturePathCache.get(cacheKey);
    if (inFlightTexturePathCache.has(cacheKey)) return await inFlightTexturePathCache.get(cacheKey);

    const promise = resolveTextureFileUncached(path, generation);
    inFlightTexturePathCache.set(cacheKey, promise);
    try {
        const resolved = await promise;
        if (generation === texturePathCacheGeneration) resolvedTexturePathCache.set(cacheKey, resolved);
        return resolved;
    } finally {
        inFlightTexturePathCache.delete(cacheKey);
    }
}

async function resolveTextureFileUncached(path, generation) {
    const hasExtension = /\.[a-z0-9]+$/i.test(path);
    const candidates = hasExtension ? [path] : [`${path}.dds`, `${path}.png`, `${path}.jpg`, `${path}.jpeg`, `${path}.webp`, path];
    for (const candidate of candidates) {
        const candidateKey = normalizeTextureKey(candidate);
        if (resolvedTexturePathCache.has(candidateKey)) {
            const cached = resolvedTexturePathCache.get(candidateKey);
            if (cached) return cached;
            continue;
        }

        const resolved = await resolveContentFile(candidate);
        if (generation === texturePathCacheGeneration) resolvedTexturePathCache.set(candidateKey, resolved);
        if (resolved) return resolved;
    }
    return null;
}

async function loadResolvedTexture(resolved, file, slot) {
    const lower = resolved.logicalPath.toLowerCase();
    const texture = lower.endsWith(".dds")
        ? await loadDdsTexture(resolved, file, slot)
        : await loadBrowserImageTexture(file);
    configureTexture(texture, resolved.logicalPath, slot);
    return texture;
}

async function loadBrowserImageTexture(file) {
    const objectUrl = URL.createObjectURL(file);
    try {
        return await new Promise((resolve, reject) => {
            new THREE.TextureLoader().load(objectUrl, resolve, undefined, reject);
        });
    } finally {
        URL.revokeObjectURL(objectUrl);
    }
}

async function loadDdsTexture(resolved, file, slot) {
    const buffer = await readQueue(() => file.arrayBuffer());
    const info = readDdsInfo(buffer, resolved.logicalPath);
    const texture = createCompressedDdsTexture(parseDdsMipmaps(buffer, info), resolved.logicalPath, info);
    configureTexture(texture, resolved.logicalPath, slot);
    await uploadQueue(() => validateCompressedTextureUpload(texture, info));
    console.debug(`DDS texture loaded locally: ${ddsLogLabel(info)}.`);
    return texture;
}

function readDdsInfo(buffer, logicalPath) {
    if (buffer.byteLength < 128) throw new Error("DDS file is too small for a header.");
    const header = new Int32Array(buffer, 0, 31);
    if (header[0] !== 0x20534444 || header[1] !== 124) throw new Error("Invalid DDS header.");

    const fourCC = header[21];
    const info = {
        logicalPath,
        fourCC,
        fourCCText: fourCCToString(fourCC),
        dxgiFormat: null,
        width: header[4],
        height: header[3],
        mipmapCount: Math.max(1, header[7]),
        dataOffset: 128,
        blockBytes: 0,
        format: null,
        formatName: "",
        extensionName: "",
        isSrgb: false,
    };

    if (fourCC === fourCCCode("DXT1")) applyBlockInfo(info, 8, THREE.RGB_S3TC_DXT1_Format, "DXT1", "WEBGL_compressed_texture_s3tc");
    else if (fourCC === fourCCCode("DXT3")) applyBlockInfo(info, 16, THREE.RGBA_S3TC_DXT3_Format, "DXT3", "WEBGL_compressed_texture_s3tc");
    else if (fourCC === fourCCCode("DXT5")) applyBlockInfo(info, 16, THREE.RGBA_S3TC_DXT5_Format, "DXT5", "WEBGL_compressed_texture_s3tc");
    else if (fourCC === fourCCCode("BC4U") || fourCC === fourCCCode("ATI1")) applyBlockInfo(info, 8, THREE.RED_RGTC1_Format, "BC4_UNORM", "EXT_texture_compression_rgtc", "r");
    else if (fourCC === fourCCCode("BC4S")) applyBlockInfo(info, 8, THREE.SIGNED_RED_RGTC1_Format, "BC4_SNORM", "EXT_texture_compression_rgtc", "r");
    else if (fourCC === fourCCCode("BC5U") || fourCC === fourCCCode("ATI2")) applyBlockInfo(info, 16, THREE.RED_GREEN_RGTC2_Format, "BC5_UNORM", "EXT_texture_compression_rgtc");
    else if (fourCC === fourCCCode("BC5S")) applyBlockInfo(info, 16, THREE.SIGNED_RED_GREEN_RGTC2_Format, "BC5_SNORM", "EXT_texture_compression_rgtc");
    else if (fourCC === fourCCCode("DX10")) readDx10DdsInfo(buffer, info);
    else throw new Error(`Unsupported DDS FourCC ${info.fourCCText}.`);

    if (info.width <= 0 || info.height <= 0) throw new Error(`Invalid DDS dimensions ${info.width}x${info.height}.`);
    if (!info.format) throw new Error(`Three.js does not expose a WebGL format for ${info.formatName}.`);
    return info;
}

function readDx10DdsInfo(buffer, info) {
    if (buffer.byteLength < 148) throw new Error("DX10 DDS file is too small for a DX10 header.");
    const dx10 = new Int32Array(buffer, 128, 5);
    info.dxgiFormat = dx10[0];
    info.dataOffset = 148;

    if (info.dxgiFormat === 71 || info.dxgiFormat === 72) applyBlockInfo(info, 8, THREE.RGB_S3TC_DXT1_Format, info.dxgiFormat === 72 ? "BC1_UNORM_SRGB" : "BC1_UNORM", "WEBGL_compressed_texture_s3tc");
    else if (info.dxgiFormat === 74 || info.dxgiFormat === 75) applyBlockInfo(info, 16, THREE.RGBA_S3TC_DXT3_Format, info.dxgiFormat === 75 ? "BC2_UNORM_SRGB" : "BC2_UNORM", "WEBGL_compressed_texture_s3tc");
    else if (info.dxgiFormat === 77 || info.dxgiFormat === 78) applyBlockInfo(info, 16, THREE.RGBA_S3TC_DXT5_Format, info.dxgiFormat === 78 ? "BC3_UNORM_SRGB" : "BC3_UNORM", "WEBGL_compressed_texture_s3tc");
    else if (info.dxgiFormat === 80) applyBlockInfo(info, 8, THREE.RED_RGTC1_Format, "BC4_UNORM", "EXT_texture_compression_rgtc", "r");
    else if (info.dxgiFormat === 81) applyBlockInfo(info, 8, THREE.SIGNED_RED_RGTC1_Format, "BC4_SNORM", "EXT_texture_compression_rgtc", "r");
    else if (info.dxgiFormat === 83) applyBlockInfo(info, 16, THREE.RED_GREEN_RGTC2_Format, "BC5_UNORM", "EXT_texture_compression_rgtc");
    else if (info.dxgiFormat === 84) applyBlockInfo(info, 16, THREE.SIGNED_RED_GREEN_RGTC2_Format, "BC5_SNORM", "EXT_texture_compression_rgtc");
    else if (info.dxgiFormat === 98 || info.dxgiFormat === 99) applyBlockInfo(info, 16, THREE.RGBA_BPTC_Format, info.dxgiFormat === 99 ? "BC7_UNORM_SRGB" : "BC7_UNORM", "EXT_texture_compression_bptc");
    else throw new Error(`Unsupported DX10 DDS format ${info.dxgiFormat}.`);
}

function applyBlockInfo(info, blockBytes, format, formatName, extensionName, colorMaskChannel = "a") {
    info.blockBytes = blockBytes;
    info.format = format;
    info.formatName = formatName;
    info.extensionName = extensionName;
    info.colorMaskChannel = colorMaskChannel;
    info.isSrgb = /_SRGB$/i.test(formatName);
}

function parseDdsMipmaps(buffer, info) {
    const mipmaps = [];
    let width = info.width;
    let height = info.height;
    let dataOffset = info.dataOffset;
    for (let i = 0; i < info.mipmapCount; i++) {
        const dataLength = Math.max(1, Math.ceil(width / 4)) * Math.max(1, Math.ceil(height / 4)) * info.blockBytes;
        if (dataOffset + dataLength > buffer.byteLength) throw new Error(`DDS mip ${i} exceeds file length.`);
        mipmaps.push({ data: new Uint8Array(buffer, dataOffset, dataLength), width, height });
        dataOffset += dataLength;
        width = Math.max(1, width >> 1);
        height = Math.max(1, height >> 1);
    }
    if (!mipmaps.length) throw new Error("DDS has no readable mipmaps.");
    return mipmaps;
}

function createCompressedDdsTexture(mipmaps, logicalPath, info) {
    const texture = new THREE.CompressedTexture(mipmaps, info.width, info.height, info.format);
    texture.name = logicalPath;
    texture.userData.seColorMaskChannel = info.colorMaskChannel || "a";
    texture.userData.seDdsIsSrgb = !!info.isSrgb;
    texture.generateMipmaps = false;
    if (mipmaps.length === 1) texture.minFilter = THREE.LinearFilter;
    texture.needsUpdate = true;
    return texture;
}

function configureTexture(texture, logicalPath, slot) {
    texture.wrapS = THREE.RepeatWrapping;
    texture.wrapT = THREE.RepeatWrapping;
    texture.anisotropy = Math.min(8, state.renderer.capabilities.getMaxAnisotropy());
    texture.colorSpace = (!isNonColorTexture(logicalPath, slot) || texture.userData.seDdsIsSrgb)
        ? THREE.SRGBColorSpace
        : THREE.NoColorSpace;
}

function validateCompressedTextureUpload(texture, info) {
    if (!compressedTextureExtension(info.extensionName)) throw new Error(`${info.extensionName} is not available in this browser/GPU.`);
    if (typeof state.renderer.initTexture !== "function") throw new Error("three.js renderer cannot preflight texture uploads.");

    const gl = state.renderer.getContext();
    collectGlErrors(gl);
    state.renderer.initTexture(texture);
    const errors = collectGlErrors(gl);
    if (errors.length) throw new Error(`WebGL upload failed with ${errors.join(", ")}.`);
}

function collectGlErrors(gl) {
    const errors = [];
    let error = gl.getError();
    while (error !== gl.NO_ERROR) {
        errors.push(glErrorName(gl, error));
        error = gl.getError();
    }
    return errors;
}

function glErrorName(gl, error) {
    if (error === gl.INVALID_ENUM) return "INVALID_ENUM";
    if (error === gl.INVALID_VALUE) return "INVALID_VALUE";
    if (error === gl.INVALID_OPERATION) return "INVALID_OPERATION";
    if (error === gl.INVALID_FRAMEBUFFER_OPERATION) return "INVALID_FRAMEBUFFER_OPERATION";
    if (error === gl.OUT_OF_MEMORY) return "OUT_OF_MEMORY";
    if (error === gl.CONTEXT_LOST_WEBGL) return "CONTEXT_LOST_WEBGL";
    return `0x${error.toString(16)}`;
}

function compressedTextureExtension(extensionName) {
    const gl = state.renderer.getContext();
    if (extensionName === "WEBGL_compressed_texture_s3tc") {
        return gl.getExtension("WEBGL_compressed_texture_s3tc") ||
            gl.getExtension("MOZ_WEBGL_compressed_texture_s3tc") ||
            gl.getExtension("WEBKIT_WEBGL_compressed_texture_s3tc");
    }
    return gl.getExtension(extensionName);
}

function isNonColorTexture(logicalPath, slot) {
    const slotText = String(slot || "").toLowerCase();
    if (slotText) {
        if (slotText.includes("normal") || slotText.includes("alpha") || slotText.includes("orm") ||
            slotText.includes("addmaps") || slotText.includes("extension")) return true;
        if (slotText.includes("color") || slotText.includes("diffuse") || slotText.includes("glass") ||
            slotText.includes("transparent")) return false;
    }

    return hasTextureMapToken(textureFileStem(logicalPath), [
        "add",
        "addmaps",
        "alpha",
        "alphamask",
        "extension",
        "extensions",
        "ext",
        "ng",
        "normal",
        "normalgloss",
        "orm",
    ]);
}

function textureFileStem(logicalPath) {
    const path = normalizeTextureKey(logicalPath);
    const name = path.split("/").pop() || path;
    return name.replace(/\.[a-z0-9]+$/i, "");
}

function hasTextureMapToken(text, tokens) {
    return tokens.some(token => new RegExp(`(^|[^a-z0-9])${token}([^a-z0-9]|$)`, "i").test(text));
}

function normalizeTextureKey(logicalPath) {
    return String(logicalPath || "").trim().replaceAll("\\", "/").toLowerCase();
}

function ensureTexturePathCacheGeneration() {
    const generation = getContentFolderCacheGeneration();
    if (generation !== texturePathCacheGeneration) {
        resolvedTexturePathCache.clear();
        inFlightTexturePathCache.clear();
        texturePathCacheGeneration = generation;
    }
    return generation;
}

function createAsyncQueue(limit) {
    let active = 0;
    const queued = [];

    function runNext() {
        while (active < limit && queued.length) {
            const item = queued.shift();
            active++;
            Promise.resolve()
                .then(item.operation)
                .then(item.resolve, item.reject)
                .finally(() => {
                    active--;
                    runNext();
                });
        }
    }

    return operation => new Promise((resolve, reject) => {
        queued.push({ operation, resolve, reject });
        runNext();
    });
}

function ddsLogLabel(info) {
    const dxgi = info.dxgiFormat == null ? "" : `, DXGI ${info.dxgiFormat}`;
    return `${info.logicalPath} (${info.formatName || info.fourCCText}, FourCC ${info.fourCCText}${dxgi}, ${info.width}x${info.height}, ${info.mipmapCount} mip(s), ${info.extensionName})`;
}

function fourCCCode(value) {
    return value.charCodeAt(0) + (value.charCodeAt(1) << 8) + (value.charCodeAt(2) << 16) + (value.charCodeAt(3) << 24);
}

function fourCCToString(value) {
    return String.fromCharCode(value & 255, (value >> 8) & 255, (value >> 16) & 255, (value >> 24) & 255);
}
