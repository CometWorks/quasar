import { state } from "./state.js";
import { log } from "./logging.js";

const DB_NAME = "quasar-viewer";
const STORE_NAME = "handles";
const HANDLE_KEY = "space-engineers-content";
const LOOKUP_CONCURRENCY = 32;
const METADATA_CONCURRENCY = 16;
const KNOWN_FILE_EXTENSION = /\.(mwm|dds|png|jpe?g|webp|sbc|xml)$/i;

const resolvedPathCache = new Map();
const inFlightPathCache = new Map();
const fileMetadataByCanonicalPath = new Map();
const inFlightMetadataByCanonicalPath = new Map();
let directoryNodesByLowerPath = new Map();
let contentCacheGeneration = 0;
const lookupQueue = createAsyncQueue(LOOKUP_CONCURRENCY);
const metadataQueue = createAsyncQueue(METADATA_CONCURRENCY);

export async function restoreContentFolder() {
    if (!window.indexedDB || !window.showDirectoryPicker) return null;
    const handle = await readHandle();
    if (!handle) return null;
    if (await ensurePermission(handle, false)) {
        state.contentFolder = handle;
        state.contentFolderName = handle.name || "Content";
        state.textureCache.clear();
        state.textureLoadPromises.clear();
        clearContentFolderCaches();
        return handle;
    }
    return null;
}

export async function pickContentFolder() {
    if (!window.showDirectoryPicker) return await pickContentFolderFromFileList();
    const handle = await window.showDirectoryPicker({ id: "space-engineers-content", mode: "read" });
    if (!(await looksLikeContentFolder(handle))) {
        throw new Error("Selected folder does not look like a Space Engineers Content folder. Pick the folder containing Data, Models, and Textures.");
    }
    activateContentFolder(handle, handle.name || "Content");
    await writeHandle(handle);
    log(`Selected local Content folder: ${state.contentFolderName}`);
    return handle;
}

async function pickContentFolderFromFileList() {
    const files = await selectContentFolderFiles();
    if (!files) throw createAbortError("Content folder selection cancelled.");
    if (!files.length) throw new Error("Selected folder does not contain any files.");

    const handle = createFileListDirectoryHandle(files);
    if (!(await looksLikeContentFolder(handle))) {
        throw new Error("Selected folder does not look like a Space Engineers Content folder. Pick the folder containing Data, Models, and Textures.");
    }

    activateContentFolder(handle, handle.name || "Content");
    log(`Selected local Content folder: ${state.contentFolderName} (${files.length.toLocaleString()} files).`);
    return handle;
}

function activateContentFolder(handle, name) {
    state.contentFolder = handle;
    state.contentFolderName = name;
    state.textureCache.clear();
    state.textureLoadPromises.clear();
    clearContentFolderCaches();
}

export async function looksLikeContentFolder(handle) {
    const root = createDirectoryNode("", handle);
    return !!(await getChildDirectory(root, "Data")) &&
        !!(await getChildDirectory(root, "Models")) &&
        !!(await getChildDirectory(root, "Textures"));
}

function selectContentFolderFiles() {
    return new Promise(resolve => {
        const input = document.getElementById("folderPicker") || createFolderPickerInput();
        input.value = "";
        input.type = "file";
        input.multiple = true;
        input.webkitdirectory = true;
        input.setAttribute("webkitdirectory", "");
        input.setAttribute("directory", "");

        let done = false;
        const finish = files => {
            if (done) return;
            done = true;
            input.removeEventListener("change", handleChange);
            input.removeEventListener("cancel", handleCancel);
            if (!input.id) input.remove();
            resolve(files);
        };
        const handleChange = () => finish(Array.from(input.files || []));
        const handleCancel = () => finish(null);

        input.addEventListener("change", handleChange);
        input.addEventListener("cancel", handleCancel);
        if (!input.isConnected) document.body.appendChild(input);
        input.click();
    });
}

function createFolderPickerInput() {
    const input = document.createElement("input");
    input.hidden = true;
    return input;
}

function createFileListDirectoryHandle(files) {
    const root = createVirtualDirectoryHandle(fileListRootName(files));
    for (const file of files) {
        const parts = fileListRelativeParts(file);
        if (parts.length < 2) continue;

        let directory = root;
        for (let i = 1; i < parts.length - 1; i++) {
            directory = getOrCreateVirtualDirectory(directory, parts[i]);
        }
        directory.children.set(parts[parts.length - 1], createVirtualFileHandle(parts[parts.length - 1], file));
    }
    return root;
}

function fileListRootName(files) {
    const firstPath = fileListRelativePath(files[0] || null);
    const firstPart = firstPath.split("/").filter(Boolean)[0];
    return firstPart || "Content";
}

function fileListRelativeParts(file) {
    return fileListRelativePath(file).split("/").filter(Boolean);
}

function fileListRelativePath(file) {
    return String(file && (file.webkitRelativePath || file.relativePath || file.name) || "").replaceAll("\\", "/");
}

function getOrCreateVirtualDirectory(parent, name) {
    let child = parent.children.get(name);
    if (!child || child.kind !== "directory") {
        child = createVirtualDirectoryHandle(name);
        parent.children.set(name, child);
    }
    return child;
}

function createVirtualDirectoryHandle(name) {
    const handle = {
        kind: "directory",
        name,
        children: new Map(),
        async getDirectoryHandle(childName) {
            const child = handle.children.get(childName);
            if (!child || child.kind !== "directory") throw new Error(`Directory not found: ${childName}`);
            return child;
        },
        async getFileHandle(childName) {
            const child = handle.children.get(childName);
            if (!child || child.kind !== "file") throw new Error(`File not found: ${childName}`);
            return child;
        },
        async *entries() {
            for (const entry of handle.children) yield entry;
        },
    };
    return handle;
}

function createVirtualFileHandle(name, file) {
    return {
        kind: "file",
        name,
        async getFile() {
            return file;
        },
    };
}

function createAbortError(message) {
    if (typeof DOMException === "function") return new DOMException(message, "AbortError");
    const error = new Error(message);
    error.name = "AbortError";
    return error;
}

export async function resolveContentFile(logicalPath) {
    if (!state.contentFolder || !logicalPath) return null;
    const normalized = normalizeLogicalPath(logicalPath);
    if (!normalized) return null;
    const cacheKey = normalized.toLowerCase();
    if (resolvedPathCache.has(cacheKey)) {
        addCacheCounter("pathCacheHit");
        return resolvedPathCache.get(cacheKey);
    }
    if (inFlightPathCache.has(cacheKey)) return await inFlightPathCache.get(cacheKey);
    addCacheCounter("pathCacheMiss");

    const generation = contentCacheGeneration;
    const promise = resolveContentFileUncached(normalized, generation);
    inFlightPathCache.set(cacheKey, promise);
    try {
        const resolved = await promise;
        if (generation === contentCacheGeneration) resolvedPathCache.set(cacheKey, resolved);
        return resolved;
    } finally {
        inFlightPathCache.delete(cacheKey);
    }
}

export function clearContentFolderCaches() {
    resolvedPathCache.clear();
    inFlightPathCache.clear();
    fileMetadataByCanonicalPath.clear();
    inFlightMetadataByCanonicalPath.clear();
    directoryNodesByLowerPath = new Map();
    contentCacheGeneration++;
}

export function getContentFolderCacheGeneration() {
    return contentCacheGeneration;
}

async function resolveContentFileUncached(normalized, generation) {
    const hasKnownExtension = KNOWN_FILE_EXTENSION.test(normalized);
    const candidates = hasKnownExtension
        ? [normalized]
        : [`${normalized}.mwm`, normalized];
    for (const candidate of candidates) {
        const candidateKey = candidate.toLowerCase();
        if (resolvedPathCache.has(candidateKey)) {
            const cached = resolvedPathCache.get(candidateKey);
            if (cached) return cached;
            continue;
        }

        const resolved = await getFileByPath(candidate);
        if (generation === contentCacheGeneration) resolvedPathCache.set(candidateKey, resolved);
        if (resolved) return resolved;
    }
    return null;
}

function normalizeLogicalPath(path) {
    let value = String(path || "").trim().replaceAll("\\", "/");
    while (value.startsWith("./")) value = value.slice(2);
    value = value.replace(/^Content\//i, "");
    value = value.replace(/\/+/g, "/");
    return value;
}

async function getFileByPath(path) {
    const parts = path.split("/").filter(Boolean);
    let current = getRootDirectoryNode();
    for (let i = 0; i < parts.length; i++) {
        const last = i === parts.length - 1;
        if (last) return await getChildFile(current, parts[i], path);
        const entry = await getChildDirectory(current, parts[i]);
        if (!entry) return null;
        current = entry.node;
    }
    return null;
}

async function getChildDirectory(parent, name) {
    return await getTypedChild(parent, name, "directory");
}

async function getChildFile(parent, name, logicalPath) {
    const child = await getTypedChild(parent, name, "file");
    if (!child) return null;
    const generation = contentCacheGeneration;
    return {
        logicalPath,
        canonicalPath: child.canonicalPath,
        fileHandle: child.handle,
        getFile: () => getFileSnapshot(child.canonicalPath, child.handle, generation),
    };
}

async function getTypedChild(parent, name, kind) {
    const wanted = name.toLowerCase();
    const misses = kind === "file" ? parent.fileMisses : parent.directoryMisses;
    if (misses.has(wanted)) {
        addCacheCounter("negativeCacheHit");
        return null;
    }

    if (parent.childrenByLowerName) {
        const entry = parent.childrenByLowerName.get(wanted) || null;
        if (entry && entry.kind === kind) {
            addCacheCounter("directoryCacheHit");
            return entry;
        }
        misses.add(wanted);
        addCacheCounter("negativeCacheHit");
        return null;
    }

    const promiseKey = `${kind}:${wanted}`;
    if (parent.childPromises.has(promiseKey)) return await parent.childPromises.get(promiseKey);

    const promise = getTypedChildUncached(parent, name, wanted, kind);
    parent.childPromises.set(promiseKey, promise);
    try {
        const child = await promise;
        if (!child) misses.add(wanted);
        return child;
    } catch (error) {
        throw error;
    } finally {
        parent.childPromises.delete(promiseKey);
    }
}

async function getTypedChildUncached(parent, name, wanted, kind) {
    try {
        addCacheCounter(kind === "file" ? "exactFileProbe" : "exactDirectoryProbe");
        const handle = await lookupQueue(() => kind === "file"
            ? parent.handle.getFileHandle(name)
            : parent.handle.getDirectoryHandle(name));
        return createChildEntry(parent, name, kind, handle);
    } catch {
    }

    const childMap = await getLowercaseChildMap(parent);
    const entry = childMap.get(wanted) || null;
    if (entry && entry.kind === kind) {
        if (entry.name !== name) addCacheCounter("caseFallbackHit");
        return entry;
    }
    return null;
}

async function getLowercaseChildMap(parent) {
    if (parent.childrenByLowerName) return parent.childrenByLowerName;
    if (parent.enumerationPromise) return await parent.enumerationPromise;

    const promise = buildLowercaseChildMap(parent);
    parent.enumerationPromise = promise;
    try {
        const map = await promise;
        parent.childrenByLowerName = map;
        return map;
    } catch (error) {
        throw error;
    } finally {
        parent.enumerationPromise = null;
    }
}

async function buildLowercaseChildMap(parent) {
    addCacheCounter("directoryEnumeration");
    return await lookupQueue(async () => {
        const map = new Map();
        for await (const [entryName, entryHandle] of parent.handle.entries()) {
            const key = entryName.toLowerCase();
            map.set(key, createChildEntry(parent, entryName, entryHandle.kind, entryHandle));
        }
        return map;
    });
}

function getRootDirectoryNode() {
    const key = directoryNodeKey("", contentCacheGeneration);
    let node = directoryNodesByLowerPath.get(key);
    if (!node) {
        node = createDirectoryNode("", state.contentFolder, contentCacheGeneration);
        directoryNodesByLowerPath.set(key, node);
    }
    return node;
}

function getDirectoryNode(canonicalPath, handle, generation) {
    const key = directoryNodeKey(canonicalPath, generation);
    let node = directoryNodesByLowerPath.get(key);
    if (!node) {
        node = createDirectoryNode(canonicalPath, handle, generation);
        directoryNodesByLowerPath.set(key, node);
    }
    return node;
}

function createDirectoryNode(canonicalPath, handle, generation = contentCacheGeneration) {
    return {
        canonicalPath,
        generation,
        handle,
        childrenByLowerName: null,
        childPromises: new Map(),
        fileMisses: new Set(),
        directoryMisses: new Set(),
        enumerationPromise: null,
    };
}

function createChildEntry(parent, name, kind, handle) {
    const canonicalPath = joinPath(parent.canonicalPath, name);
    const entry = {
        name,
        lowerName: name.toLowerCase(),
        kind,
        handle,
        canonicalPath,
    };
    if (kind === "directory") entry.node = getDirectoryNode(canonicalPath, handle, parent.generation);
    return entry;
}

function directoryNodeKey(canonicalPath, generation) {
    return `${generation}:${canonicalPath.toLowerCase()}`;
}

async function getFileSnapshot(canonicalPath, fileHandle, generation) {
    const cacheKey = canonicalPath.toLowerCase();
    if (generation === contentCacheGeneration && fileMetadataByCanonicalPath.has(cacheKey)) {
        addCacheCounter("metadataCacheHit");
        return fileMetadataByCanonicalPath.get(cacheKey);
    }
    if (generation === contentCacheGeneration && inFlightMetadataByCanonicalPath.has(cacheKey)) {
        return await inFlightMetadataByCanonicalPath.get(cacheKey);
    }

    const promise = timedQueue(metadataQueue, "localFileMetadataRead", () => fileHandle.getFile());
    if (generation === contentCacheGeneration) inFlightMetadataByCanonicalPath.set(cacheKey, promise);
    try {
        const file = await promise;
        if (generation === contentCacheGeneration) fileMetadataByCanonicalPath.set(cacheKey, file);
        return file;
    } finally {
        inFlightMetadataByCanonicalPath.delete(cacheKey);
    }
}

function joinPath(parentPath, name) {
    return parentPath ? `${parentPath}/${name}` : name;
}

async function timedQueue(queue, timingKey, operation) {
    const start = performance.now();
    try {
        return await queue(operation);
    } finally {
        addTiming(timingKey, performance.now() - start);
    }
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

function addTiming(key, durationMs) {
    const metric = state.timings[key] || { count: 0, totalMs: 0, maxMs: 0 };
    metric.count++;
    metric.totalMs += durationMs;
    metric.maxMs = Math.max(metric.maxMs, durationMs);
    state.timings[key] = metric;
}

function addCacheCounter(key) {
    const label = key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, value => value.toUpperCase());
    state.stats[label] = (state.stats[label] || 0) + 1;
}

async function ensurePermission(handle, request) {
    const options = { mode: "read" };
    if ((await handle.queryPermission(options)) === "granted") return true;
    return request && (await handle.requestPermission(options)) === "granted";
}

async function readHandle() {
    try {
        return await withStore("readonly", store => store.get(HANDLE_KEY));
    } catch {
        return null;
    }
}

async function writeHandle(handle) {
    try {
        await withStore("readwrite", store => store.put(handle, HANDLE_KEY));
    } catch {
    }
}

function withStore(mode, callback) {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(STORE_NAME);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            const db = request.result;
            const tx = db.transaction(STORE_NAME, mode);
            const storeRequest = callback(tx.objectStore(STORE_NAME));
            storeRequest.onsuccess = () => resolve(storeRequest.result);
            storeRequest.onerror = () => reject(storeRequest.error);
            tx.oncomplete = () => db.close();
            tx.onerror = () => reject(tx.error);
        };
    });
}
