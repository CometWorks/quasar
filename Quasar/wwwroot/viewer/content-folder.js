import * as zip from "zip.js";
import { state } from "./state.js";
import { log } from "./logging.js";

const DB_NAME = "quasar-viewer";
const STORE_NAME = "handles";
const CONTENT_HANDLE_KEY = "space-engineers-content";
const MODS_HANDLE_KEY = "space-engineers-mods";
const LOOKUP_CONCURRENCY = 32;
const METADATA_CONCURRENCY = 16;
const KNOWN_FILE_EXTENSION = /\.(mwm|dds|png|jpe?g|webp|sbc|xml)$/i;

const resolvedPathCache = new Map();
const inFlightPathCache = new Map();
const fileMetadataByCanonicalPath = new Map();
const inFlightMetadataByCanonicalPath = new Map();
let directoryNodesByKey = new Map();
let assetCacheGeneration = 0;
const lookupQueue = createAsyncQueue(LOOKUP_CONCURRENCY);
const metadataQueue = createAsyncQueue(METADATA_CONCURRENCY);

export async function restoreContentFolder() {
    if (!window.indexedDB || !window.showDirectoryPicker) return null;
    const handle = await readHandle(CONTENT_HANDLE_KEY);
    if (!handle) return null;
    if (await ensurePermission(handle, false)) {
        activateContentFolder(handle, handle.name || "Content");
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
    await writeHandle(CONTENT_HANDLE_KEY, handle);
    log(`Selected local Content folder: ${state.contentFolderName}`);
    return handle;
}

export async function restoreModsFolder() {
    if (!window.indexedDB || !window.showDirectoryPicker) return null;
    const handle = await readHandle(MODS_HANDLE_KEY);
    if (!handle) return null;
    if (await ensurePermission(handle, false)) {
        await activateModsFolder(handle, handle.name || "Mods");
        return handle;
    }
    return null;
}

export async function pickModsFolder() {
    if (!window.showDirectoryPicker) return await pickModsFolderFromFileList();
    const handle = await window.showDirectoryPicker({ id: "space-engineers-mods", mode: "read" });
    await validateModsFolder(handle);
    await activateModsFolder(handle, handle.name || "Mods");
    await writeHandle(MODS_HANDLE_KEY, handle);
    log(`Selected local Mods folder: ${state.modsFolderName}`);
    return handle;
}

async function pickContentFolderFromFileList() {
    const files = await selectFolderFiles("folderPicker");
    if (!files) throw createAbortError("Content folder selection cancelled.");
    if (!files.length) throw new Error("Selected folder does not contain any files.");

    const handle = createFileListDirectoryHandle(files, "Content");
    if (!(await looksLikeContentFolder(handle))) {
        throw new Error("Selected folder does not look like a Space Engineers Content folder. Pick the folder containing Data, Models, and Textures.");
    }

    activateContentFolder(handle, handle.name || "Content");
    log(`Selected local Content folder: ${state.contentFolderName} (${files.length.toLocaleString()} files).`);
    return handle;
}

async function pickModsFolderFromFileList() {
    const files = await selectFolderFiles("modsFolderPicker");
    if (!files) throw createAbortError("Mods folder selection cancelled.");
    const handle = createFileListDirectoryHandle(files, "Mods");
    await validateModsFolder(handle);
    await activateModsFolder(handle, handle.name || "Mods");
    log(`Selected local Mods folder: ${state.modsFolderName} (${files.length.toLocaleString()} files).`);
    return handle;
}

function activateContentFolder(handle, name) {
    state.contentFolder = handle;
    state.contentFolderName = name;
    clearAssetFolderCaches();
}

async function activateModsFolder(handle, name) {
    state.modsFolder = handle;
    state.modsFolderName = name;
    clearAssetFolderCaches();
    await refreshSceneModRoots();
}

export async function looksLikeContentFolder(handle) {
    const root = createDirectoryNode("content", "", handle);
    return !!(await getChildDirectory(root, "Data")) &&
        !!(await getChildDirectory(root, "Models")) &&
        !!(await getChildDirectory(root, "Textures"));
}

async function validateModsFolder(handle) {
    const status = await looksLikeModsFolder(handle);
    if (!status.valid) throw new Error("Selected folder does not look like a Space Engineers Mods folder. Pick the folder containing mod folders or .sbm files.");
    if (status.empty) log("Mods folder selected, but no mods were found.", true);
}

export async function looksLikeModsFolder(handle) {
    if (!handle || handle.kind !== "directory") return { valid: false, empty: true };
    try {
        for await (const [name, child] of handle.entries()) {
            if (child.kind === "directory" || (child.kind === "file" && name.toLowerCase().endsWith(".sbm"))) {
                return { valid: true, empty: false };
            }
        }
        return { valid: true, empty: true };
    } catch {
        return { valid: true, empty: false };
    }
}

function selectFolderFiles(inputId) {
    return new Promise(resolve => {
        const input = document.getElementById(inputId) || createFolderPickerInput(inputId);
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

function createFolderPickerInput(id = "") {
    const input = document.createElement("input");
    input.hidden = true;
    input.id = id;
    return input;
}

function createFileListDirectoryHandle(files, fallbackName) {
    const root = createVirtualDirectoryHandle(fileListRootName(files) || fallbackName);
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
    return firstPart || "";
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

export async function setSceneModRoots(mods) {
    state.sceneMods = Array.isArray(mods) ? mods : [];
    await refreshSceneModRoots();
}

async function refreshSceneModRoots() {
    state.modRoots = new Map();
    for (const mod of state.sceneMods || []) {
        const rootId = String(mod.rootId || mod.RootId || "");
        if (!rootId) continue;
        state.modRoots.set(rootId, {
            id: rootId,
            kind: "mod-pending",
            name: String(mod.name || mod.Name || ""),
            handle: null,
            archive: null,
            metadata: mod,
            resolved: false,
            missing: false,
            warningLogged: false,
        });
    }
}

export async function resolveContentFile(logicalPath) {
    return await resolveAssetFile(logicalPath, { rootId: "" });
}

export async function resolveAssetFile(logicalPath, options = {}) {
    if (!logicalPath) return null;
    const normalized = normalizeLogicalPath(logicalPath);
    if (!normalized.path) return null;
    const rootId = String(options.rootId || options.RootId || "");
    const sourceKind = String(options.sourceKind || options.SourceKind || "");
    const cacheKey = assetCacheKey(normalized, rootId, sourceKind);
    if (resolvedPathCache.has(cacheKey)) {
        addCacheCounter("pathCacheHit");
        return resolvedPathCache.get(cacheKey);
    }
    if (inFlightPathCache.has(cacheKey)) return await inFlightPathCache.get(cacheKey);
    addCacheCounter("pathCacheMiss");

    const generation = assetCacheGeneration;
    const promise = resolveAssetFileUncached(normalized, rootId, sourceKind, generation);
    inFlightPathCache.set(cacheKey, promise);
    try {
        const resolved = await promise;
        if (generation === assetCacheGeneration) resolvedPathCache.set(cacheKey, resolved);
        return resolved;
    } finally {
        inFlightPathCache.delete(cacheKey);
    }
}

async function resolveAssetFileUncached(normalized, rootId, sourceKind, generation) {
    if (normalized.modName) {
        const direct = await resolveDirectModPath(normalized.modName, normalized.path, generation);
        if (direct) return direct;
    }

    if (rootId) {
        const root = await getSceneModRoot(rootId);
        if (root) {
            const resolved = await resolveInRoot(root, normalized.path, generation);
            if (resolved) return resolved;
        }
    }

    const contentRoot = getContentRoot();
    if (contentRoot) {
        const resolved = await resolveInRoot(contentRoot, normalized.path, generation);
        if (resolved) return resolved;
    }

    if (!rootId && sourceKind.toLowerCase() === "mod") {
        for (const id of state.modRoots.keys()) {
            const root = await getSceneModRoot(id);
            if (!root) continue;
            const resolved = await resolveInRoot(root, normalized.path, generation);
            if (resolved) return resolved;
        }
    }

    return null;
}

function normalizeLogicalPath(path) {
    let value = String(path || "").trim().replaceAll("\\", "/");
    while (value.startsWith("./")) value = value.slice(2);
    value = value.replace(/\/+/g, "/");
    value = value.replace(/^Content\//i, "");
    if (/^Mods\//i.test(value)) {
        const parts = value.replace(/^Mods\//i, "").split("/").filter(Boolean);
        const modName = parts.shift() || "";
        return { path: parts.join("/"), modName };
    }
    return { path: value, modName: "" };
}

function assetCacheKey(normalized, rootId, sourceKind) {
    return `${assetCacheGeneration}|${rootId || "content"}|${normalized.modName || ""}|${sourceKind || ""}|${normalized.path.toLowerCase()}`;
}

function candidatePaths(path) {
    const hasKnownExtension = KNOWN_FILE_EXTENSION.test(path);
    return hasKnownExtension ? [path] : [`${path}.mwm`, path];
}

function getContentRoot() {
    if (!state.contentFolder) return null;
    return {
        id: "content",
        kind: "content",
        name: state.contentFolderName || "Content",
        handle: state.contentFolder,
        rootId: "",
    };
}

async function resolveDirectModPath(modName, logicalPath, generation) {
    if (!state.modsFolder || !modName) return null;
    const handle = await getTopLevelChild(state.modsFolder, modName, "directory") || await getTopLevelChild(state.modsFolder, modName, "file");
    if (!handle) return null;
    const root = createModRoot(`mods-direct:${modName}`, modName, handle, null);
    return await resolveInRoot(root, logicalPath, generation);
}

async function getSceneModRoot(rootId) {
    if (!state.modsFolder) return null;
    const root = state.modRoots.get(rootId);
    if (!root) return null;
    if (root.resolved) return root.missing ? null : root;

    root.resolved = true;
    const name = root.name || root.metadata?.name || root.metadata?.Name || "";
    const handle = await getTopLevelChild(state.modsFolder, name, "directory") ||
        await getTopLevelChild(state.modsFolder, name, "file") ||
        (!name.toLowerCase().endsWith(".sbm") ? await getTopLevelChild(state.modsFolder, `${name}.sbm`, "file") : null);
    if (!handle) {
        root.missing = true;
        const friendly = root.metadata?.friendlyName || root.metadata?.FriendlyName || name || rootId;
        if (!root.warningLogged) log(`Scene references mod ${friendly}, but it was not found in the selected Mods folder.`, true);
        root.warningLogged = true;
        return null;
    }

    Object.assign(root, createModRoot(rootId, name, handle, root.metadata));
    return root;
}

function createModRoot(rootId, name, handle, metadata) {
    const isArchive = handle.kind === "file" || String(handle.name || name).toLowerCase().endsWith(".sbm");
    return {
        id: rootId,
        rootId,
        kind: isArchive ? "mod-archive" : "mod-directory",
        name: handle.name || name,
        handle,
        metadata,
        archive: null,
        resolved: true,
        missing: false,
    };
}

async function getTopLevelChild(parentHandle, name, kind) {
    if (!parentHandle || !name) return null;
    try {
        return kind === "file" ? await parentHandle.getFileHandle(name) : await parentHandle.getDirectoryHandle(name);
    } catch {
    }

    try {
        const wanted = name.toLowerCase();
        for await (const [entryName, entryHandle] of parentHandle.entries()) {
            if (entryHandle.kind === kind && entryName.toLowerCase() === wanted) return entryHandle;
        }
    } catch {
    }
    return null;
}

async function resolveInRoot(root, logicalPath, generation) {
    for (const candidate of candidatePaths(logicalPath)) {
        const resolved = root.kind === "mod-archive"
            ? await getArchiveFileByPath(root, candidate, generation)
            : await getDirectoryFileByPath(root, candidate, generation);
        if (resolved) return resolved;
    }
    return null;
}

async function getDirectoryFileByPath(root, path, generation) {
    const parts = path.split("/").filter(Boolean);
    let current = getRootDirectoryNode(root);
    for (let i = 0; i < parts.length; i++) {
        const last = i === parts.length - 1;
        if (last) return await getChildFile(root, current, parts[i], path, generation);
        const entry = await getChildDirectory(current, parts[i]);
        if (!entry) return null;
        current = entry.node;
    }
    return null;
}

async function getArchiveFileByPath(root, path, generation) {
    const archive = await getArchiveIndex(root, generation);
    if (!archive) return null;
    const key = archivePathKey(path);
    const entry = archive.entries.get(key) || archive.entries.get(archivePathKey(withoutSingleTopLevel(path, archive.singleTopLevel)));
    if (!entry) return null;
    return {
        logicalPath: path,
        canonicalPath: `${root.name}/${entry.filename}`,
        rootId: root.rootId || "",
        rootKind: root.kind,
        getFile: async () => {
            const blob = await entry.getData(new zip.BlobWriter());
            const fileName = entry.filename.split("/").pop() || path.split("/").pop() || "asset";
            if (typeof File === "function") return new File([blob], fileName, { lastModified: archive.file.lastModified || 0 });
            blob.name = fileName;
            blob.lastModified = archive.file.lastModified || 0;
            return blob;
        },
    };
}

async function getArchiveIndex(root, generation) {
    if (root.archive && root.archive.generation === generation) return root.archive;
    try {
        const file = await root.handle.getFile();
        const reader = new zip.ZipReader(new zip.BlobReader(file));
        const entries = await reader.getEntries();
        const map = new Map();
        for (const entry of entries) {
            if (entry.directory) continue;
            map.set(archivePathKey(entry.filename), entry);
        }
        root.archive = { generation, file, reader, entries: map, singleTopLevel: detectSingleTopLevel(entries) };
        return root.archive;
    } catch (error) {
        log(`Could not read .sbm archive ${root.name}: ${error.message}`, true);
        return null;
    }
}

function archivePathKey(path) {
    return String(path || "").replaceAll("\\", "/").replace(/^\.\//, "").replace(/\/+/g, "/").toLowerCase();
}

function detectSingleTopLevel(entries) {
    const firstParts = new Set();
    let hasAssetDirectory = false;
    for (const entry of entries) {
        const parts = archivePathKey(entry.filename).split("/").filter(Boolean);
        if (!parts.length) continue;
        firstParts.add(parts[0]);
        if (["data", "models", "textures"].includes(parts[0])) hasAssetDirectory = true;
    }
    return !hasAssetDirectory && firstParts.size === 1 ? [...firstParts][0] : "";
}

function withoutSingleTopLevel(path, topLevel) {
    return topLevel ? `${topLevel}/${path}` : path;
}

async function getChildDirectory(parent, name) {
    return await getTypedChild(parent, name, "directory");
}

async function getChildFile(root, parent, name, logicalPath, generation) {
    const child = await getTypedChild(parent, name, "file");
    if (!child) return null;
    return {
        logicalPath,
        canonicalPath: child.canonicalPath,
        rootId: root.rootId || "",
        rootKind: root.kind,
        fileHandle: child.handle,
        getFile: () => getFileSnapshot(root, child.canonicalPath, child.handle, generation),
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

function getRootDirectoryNode(root) {
    const key = directoryNodeKey(root, "");
    let node = directoryNodesByKey.get(key);
    if (!node) {
        node = createDirectoryNode(root.id || root.rootId || root.kind, "", root.handle);
        directoryNodesByKey.set(key, node);
    }
    return node;
}

function getDirectoryNode(parent, canonicalPath, handle) {
    const key = directoryNodeKey({ id: parent.rootKey }, canonicalPath);
    let node = directoryNodesByKey.get(key);
    if (!node) {
        node = createDirectoryNode(parent.rootKey, canonicalPath, handle);
        directoryNodesByKey.set(key, node);
    }
    return node;
}

function createDirectoryNode(rootKey, canonicalPath, handle) {
    return {
        rootKey,
        canonicalPath,
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
    const entry = { name, lowerName: name.toLowerCase(), kind, handle, canonicalPath };
    if (kind === "directory") entry.node = getDirectoryNode(parent, canonicalPath, handle);
    return entry;
}

function directoryNodeKey(root, canonicalPath) {
    return `${assetCacheGeneration}:${root.id || root.rootId || root.kind}:${canonicalPath.toLowerCase()}`;
}

async function getFileSnapshot(root, canonicalPath, fileHandle, generation) {
    const cacheKey = `${root.rootId || root.id || root.kind}:${canonicalPath.toLowerCase()}`;
    if (generation === assetCacheGeneration && fileMetadataByCanonicalPath.has(cacheKey)) {
        addCacheCounter("metadataCacheHit");
        return fileMetadataByCanonicalPath.get(cacheKey);
    }
    if (generation === assetCacheGeneration && inFlightMetadataByCanonicalPath.has(cacheKey)) {
        return await inFlightMetadataByCanonicalPath.get(cacheKey);
    }

    const promise = timedQueue(metadataQueue, "localFileMetadataRead", () => fileHandle.getFile());
    if (generation === assetCacheGeneration) inFlightMetadataByCanonicalPath.set(cacheKey, promise);
    try {
        const file = await promise;
        if (generation === assetCacheGeneration) fileMetadataByCanonicalPath.set(cacheKey, file);
        return file;
    } finally {
        inFlightMetadataByCanonicalPath.delete(cacheKey);
    }
}

export function clearAssetFolderCaches() {
    for (const root of state.modRoots?.values?.() || []) {
        if (root.archive?.reader && typeof root.archive.reader.close === "function") root.archive.reader.close().catch(() => {});
        root.archive = null;
        root.resolved = false;
        root.missing = false;
        root.handle = null;
        root.kind = "mod-pending";
    }
    resolvedPathCache.clear();
    inFlightPathCache.clear();
    fileMetadataByCanonicalPath.clear();
    inFlightMetadataByCanonicalPath.clear();
    directoryNodesByKey = new Map();
    state.textureCache.clear();
    state.textureLoadPromises.clear();
    assetCacheGeneration++;
}

export const clearContentFolderCaches = clearAssetFolderCaches;

export function getAssetFolderCacheGeneration() {
    return assetCacheGeneration;
}

export const getContentFolderCacheGeneration = getAssetFolderCacheGeneration;

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

async function readHandle(key) {
    try {
        return await withStore("readonly", store => store.get(key));
    } catch {
        return null;
    }
}

async function writeHandle(key, handle) {
    try {
        await withStore("readwrite", store => store.put(handle, key));
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
