import { resolveContentFile } from "./content-folder.js";
import { state } from "./state.js";

const modelCache = new Map();
const textDecoder = new TextDecoder();

const TECHNIQUE_ORDER = new Map([
    ["MESH", 0],
    ["VOXELS_DEBRIS", 1],
    ["VOXEL_MAP", 2],
    ["ALPHA_MASKED", 3],
    ["ALPHA_MASKED_SINGLE_SIDED", 4],
    ["FOLIAGE", 5],
    ["DECAL", 6],
    ["DECAL_NOPREMULT", 7],
    ["DECAL_CUTOUT", 8],
    ["HOLO", 9],
    ["VOXEL_MAP_SINGLE", 10],
    ["VOXEL_MAP_MULTI", 11],
    ["SKINNED", 12],
    ["MESH_INSTANCED", 13],
    ["MESH_INSTANCED_SKINNED", 14],
    ["GLASS", 15],
    ["MESH_INSTANCED_GENERIC", 16],
    ["MESH_INSTANCED_GENERIC_MASKED", 17],
    ["ATMOSPHERE", 18],
    ["CLOUD_LAYER", 19],
    ["SHIELD", 20],
    ["SHIELD_LIT", 21],
]);

export async function resolveModelAsset(asset) {
    if (!asset || !asset.logicalPath) return { status: "missing", message: "Model asset has no logical path." };
    const resolved = await timed("modelFileResolution", () => resolveContentFile(asset.logicalPath));
    if (!resolved) return { status: "missing", message: `Missing local model: ${asset.logicalPath}` };

    try {
        const model = await parseResolvedModel(resolved, new Set());
        return {
            status: "parsed",
            logicalPath: resolved.logicalPath,
            byteLength: resolved.file.size,
            model,
            message: `Parsed ${asset.logicalPath} locally (${model.vertexCount.toLocaleString()} vertices, ${model.triangleCount.toLocaleString()} triangles).`,
        };
    } catch (error) {
        return {
            status: "proxy",
            logicalPath: resolved.logicalPath,
            byteLength: resolved.file.size,
            message: `Resolved ${asset.logicalPath} locally (${resolved.file.size} bytes), but MWM parsing failed: ${error.message}. Rendering proxy geometry.`,
        };
    }
}

async function parseResolvedModel(resolved, stack) {
    const cacheKey = `${resolved.logicalPath.toLowerCase()}|${resolved.file.size}|${resolved.file.lastModified || 0}`;
    if (modelCache.has(cacheKey)) return modelCache.get(cacheKey);
    if (stack.has(resolved.logicalPath.toLowerCase())) throw new Error(`recursive GeometryDataAsset reference: ${resolved.logicalPath}`);

    const promise = parseResolvedModelUncached(resolved, stack);
    modelCache.set(cacheKey, promise);
    try {
        return await promise;
    } catch (error) {
        modelCache.delete(cacheKey);
        throw error;
    }
}

async function parseResolvedModelUncached(resolved, stack) {
    stack.add(resolved.logicalPath.toLowerCase());
    try {
        const buffer = await timed("mwmFileRead", () => resolved.file.arrayBuffer());
        const parseStart = performance.now();
        const reader = new MwmReader(buffer);
        const tags = reader.readTagIndex();
        const geometryAsset = tags.get("GeometryDataAsset") ? reader.readStringTag(tags.get("GeometryDataAsset")) : "";
        if (!tags.has("Vertices") && geometryAsset) {
            const geometryResolved = await resolveContentFile(geometryAsset);
            if (!geometryResolved) throw new Error(`missing geometry asset ${geometryAsset}`);
            const model = await parseResolvedModel(geometryResolved, stack);
            return { ...model, logicalPath: resolved.logicalPath, geometryLogicalPath: geometryResolved.logicalPath };
        }

        const patternScale = tags.has("PatternScale") ? validPatternScale(reader.readFloatTag(tags.get("PatternScale"))) : 1;
        const positions = tags.has("Vertices") ? reader.readPositions(tags.get("Vertices")) : null;
        if (!positions || positions.length === 0) throw new Error("missing Vertices tag");

        const normals = tags.has("Normals") ? reader.readNormals(tags.get("Normals"), positions.length / 3) : null;
        const uvs = tags.has("TexCoords0") ? reader.readTexCoords(tags.get("TexCoords0"), positions.length / 3) : null;
        if (uvs && patternScale !== 1) scaleTexCoords(uvs, patternScale);
        const parts = tags.has("MeshParts") ? reader.readMeshParts(tags.get("MeshParts")) : [];
        if (!parts.length) throw new Error("missing MeshParts tag");

        parts.sort((a, b) => techniqueOrder(a.technique) - techniqueOrder(b.technique));
        const indexCount = parts.reduce((sum, part) => sum + part.indices.length, 0);
        const IndexArray = positions.length / 3 > 65535 ? Uint32Array : Uint16Array;
        const indices = new IndexArray(indexCount);
        const groups = [];
        let offset = 0;
        for (let i = 0; i < parts.length; i++) {
            const part = parts[i];
            indices.set(part.indices, offset);
            groups.push({
                start: offset,
                count: part.indices.length,
                materialIndex: i,
                materialName: part.materialName || `part-${i + 1}`,
                technique: part.technique || "MESH",
                textures: part.textures,
            });
            offset += part.indices.length;
        }

        const model = {
            logicalPath: resolved.logicalPath,
            geometryLogicalPath: resolved.logicalPath,
            positions,
            normals,
            uvs,
            patternScale,
            indices,
            groups,
            vertexCount: positions.length / 3,
            triangleCount: Math.floor(indexCount / 3),
        };
        addTiming("mwmParse", performance.now() - parseStart);
        return model;
    } finally {
        stack.delete(resolved.logicalPath.toLowerCase());
    }
}

async function timed(key, operation) {
    const start = performance.now();
    try {
        return await operation();
    } finally {
        addTiming(key, performance.now() - start);
    }
}

function addTiming(key, durationMs) {
    const metric = state.timings[key] || { count: 0, totalMs: 0, maxMs: 0 };
    metric.count++;
    metric.totalMs += durationMs;
    metric.maxMs = Math.max(metric.maxMs, durationMs);
    state.timings[key] = metric;
}

function techniqueOrder(technique) {
    return TECHNIQUE_ORDER.get(String(technique || "").toUpperCase()) ?? 999;
}

function validPatternScale(value) {
    return Number.isFinite(value) && value > 0 ? value : 1;
}

function scaleTexCoords(uvs, patternScale) {
    for (let i = 0; i < uvs.length; i++) uvs[i] /= patternScale;
}

class MwmReader {
    constructor(buffer) {
        this.view = new DataView(buffer);
        this.offset = 0;
        this.version = 0;
    }

    readTagIndex() {
        this.readString();
        const debugValues = this.readStringArray();
        const versionPrefix = "Version:";
        if (debugValues.length && debugValues[0].startsWith(versionPrefix)) {
            this.version = Number.parseInt(debugValues[0].slice(versionPrefix.length), 10) || 0;
        }

        if (this.version < 1066002) throw new Error(`unsupported old MWM version ${this.version || "unknown"}`);

        const tags = new Map();
        const count = this.readInt32();
        for (let i = 0; i < count; i++) {
            const name = this.readString();
            const offset = this.readInt32();
            tags.set(name, offset);
        }
        return tags;
    }

    readStringTag(offset) {
        this.seekTag(offset);
        return this.readString();
    }

    readFloatTag(offset) {
        this.seekTag(offset);
        return this.readFloat32();
    }

    readPositions(offset) {
        this.seekTag(offset);
        const count = this.readInt32();
        const positions = new Float32Array(count * 3);
        for (let i = 0; i < count; i++) {
            const x = this.readHalf();
            const y = this.readHalf();
            const z = this.readHalf();
            const scale = this.readHalf();
            const target = i * 3;
            positions[target] = x * scale;
            positions[target + 1] = y * scale;
            positions[target + 2] = z * scale;
        }
        return positions;
    }

    readNormals(offset, expectedCount) {
        this.seekTag(offset);
        const count = this.readInt32();
        if (count !== expectedCount) return null;
        const normals = new Float32Array(count * 3);
        for (let i = 0; i < count; i++) {
            const normal = unpackNormal(this.readUint32());
            const target = i * 3;
            normals[target] = normal.x;
            normals[target + 1] = normal.y;
            normals[target + 2] = normal.z;
        }
        return normals;
    }

    readTexCoords(offset, expectedCount) {
        this.seekTag(offset);
        const count = this.readInt32();
        if (count !== expectedCount) return null;
        const uvs = new Float32Array(count * 2);
        for (let i = 0; i < count; i++) {
            const target = i * 2;
            uvs[target] = this.readHalf();
            uvs[target + 1] = 1 - this.readHalf();
        }
        return uvs;
    }

    readMeshParts(offset) {
        this.seekTag(offset);
        const count = this.readInt32();
        const parts = [];
        for (let i = 0; i < count; i++) {
            this.readInt32();
            if (this.version < 1052001) this.readInt32();
            const indexCount = this.readInt32();
            const indices = new Uint32Array(indexCount);
            for (let j = 0; j < indexCount; j++) indices[j] = this.readInt32();
            const material = this.readBoolean() ? this.readMaterial() : null;
            parts.push({
                indices,
                materialName: material?.materialName || "",
                technique: material?.technique || "MESH",
                textures: material?.textures || {},
            });
        }
        return parts;
    }

    readMaterial() {
        const materialName = this.readString() || "";
        const textures = {};
        if (this.version < 1052002) {
            const diffuse = this.readString();
            const normal = this.readString();
            if (diffuse) textures.DiffuseTexture = diffuse;
            if (normal) textures.NormalTexture = normal;
        } else {
            const textureCount = this.readInt32();
            for (let i = 0; i < textureCount; i++) textures[this.readString()] = this.readString();
        }

        if (this.version >= 1068001) {
            const userDataCount = this.readInt32();
            for (let i = 0; i < userDataCount; i++) {
                this.readString();
                this.readString();
            }
        }

        if (this.version < 1157001) {
            for (let i = 0; i < 7; i++) this.readFloat32();
        }

        const technique = this.version < 1052001 ? `OLD_${this.readInt32()}` : this.readString();
        if (technique === "GLASS") {
            if (this.version >= 1043001) {
                this.readString();
                this.readString();
                this.readBoolean();
            } else {
                for (let i = 0; i < 4; i++) this.readFloat32();
            }
        }

        return { materialName, technique, textures };
    }

    readStringArray() {
        const count = this.readInt32();
        const values = [];
        for (let i = 0; i < count; i++) values.push(this.readString());
        return values;
    }

    seekTag(offset) {
        this.offset = offset;
        this.readString();
    }

    readString() {
        const length = this.read7BitEncodedInt();
        this.ensure(length);
        const bytes = new Uint8Array(this.view.buffer, this.view.byteOffset + this.offset, length);
        this.offset += length;
        return textDecoder.decode(bytes);
    }

    read7BitEncodedInt() {
        let count = 0;
        let shift = 0;
        while (shift !== 35) {
            this.ensure(1);
            const byte = this.view.getUint8(this.offset++);
            count |= (byte & 0x7f) << shift;
            if ((byte & 0x80) === 0) return count;
            shift += 7;
        }
        throw new Error("invalid string length encoding");
    }

    readBoolean() {
        this.ensure(1);
        return this.view.getUint8(this.offset++) !== 0;
    }

    readInt32() {
        this.ensure(4);
        const value = this.view.getInt32(this.offset, true);
        this.offset += 4;
        return value;
    }

    readUint32() {
        this.ensure(4);
        const value = this.view.getUint32(this.offset, true);
        this.offset += 4;
        return value;
    }

    readFloat32() {
        this.ensure(4);
        const value = this.view.getFloat32(this.offset, true);
        this.offset += 4;
        return value;
    }

    readHalf() {
        this.ensure(2);
        const value = unpackHalf(this.view.getUint16(this.offset, true));
        this.offset += 2;
        return value;
    }

    ensure(byteCount) {
        if (this.offset + byteCount > this.view.byteLength) throw new Error("unexpected end of MWM data");
    }
}

function unpackHalf(value) {
    const sign = (value & 0x8000) ? -1 : 1;
    const exponent = (value >> 10) & 0x1f;
    const fraction = value & 0x03ff;
    if (exponent === 0) return sign * Math.pow(2, -14) * (fraction / 1024);
    if (exponent === 31) return fraction ? Number.NaN : sign * Number.POSITIVE_INFINITY;
    return sign * Math.pow(2, exponent - 15) * (1 + fraction / 1024);
}

function unpackNormal(packed) {
    const xByte = packed & 0xff;
    let yByte = (packed >>> 8) & 0xff;
    const zByte = (packed >>> 16) & 0xff;
    const wByte = (packed >>> 24) & 0xff;
    const sign = yByte > 127.5 ? 1 : -1;
    if (sign > 0) yByte -= 128;
    const packedX = (xByte + 256 * yByte) / 32767;
    const packedY = (zByte + 256 * wByte) / 32767;
    const x = 2 * packedX - 1;
    const y = 2 * packedY - 1;
    const z = sign * Math.sqrt(Math.max(0, 1 - x * x - y * y));
    return { x, y, z };
}
