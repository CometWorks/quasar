import { state } from "./state.js";
import { log } from "./logging.js";

const DB_NAME = "quasar-viewer";
const STORE_NAME = "handles";
const HANDLE_KEY = "space-engineers-content";

export async function restoreContentFolder() {
    if (!window.indexedDB) return null;
    const handle = await readHandle();
    if (!handle) return null;
    if (await ensurePermission(handle, false)) {
        state.contentFolder = handle;
        state.contentFolderName = handle.name || "Content";
        return handle;
    }
    return null;
}

export async function pickContentFolder() {
    if (!window.showDirectoryPicker) {
        throw new Error("This browser does not support folder selection. Use a Chromium-based browser for local Content loading.");
    }
    const handle = await window.showDirectoryPicker({ id: "space-engineers-content", mode: "read" });
    if (!(await looksLikeContentFolder(handle))) {
        throw new Error("Selected folder does not look like a Space Engineers Content folder. Pick the folder containing Data, Models, and Textures.");
    }
    state.contentFolder = handle;
    state.contentFolderName = handle.name || "Content";
    await writeHandle(handle);
    log(`Selected local Content folder: ${state.contentFolderName}`);
    return handle;
}

export async function looksLikeContentFolder(handle) {
    return !!(await getChildDirectory(handle, "Data")) &&
        !!(await getChildDirectory(handle, "Models")) &&
        !!(await getChildDirectory(handle, "Textures"));
}

export async function resolveContentFile(logicalPath) {
    if (!state.contentFolder || !logicalPath) return null;
    const normalized = normalizeLogicalPath(logicalPath);
    const candidates = normalized.toLowerCase().endsWith(".mwm") || normalized.toLowerCase().endsWith(".dds")
        ? [normalized]
        : [`${normalized}.mwm`, normalized];
    for (const candidate of candidates) {
        const file = await getFileByPath(state.contentFolder, candidate);
        if (file) return { logicalPath: candidate, file };
    }
    return null;
}

function normalizeLogicalPath(path) {
    let value = String(path || "").trim().replaceAll("\\", "/");
    while (value.startsWith("./")) value = value.slice(2);
    value = value.replace(/^Content\//i, "");
    return value;
}

async function getFileByPath(root, path) {
    const parts = path.split("/").filter(Boolean);
    let current = root;
    for (let i = 0; i < parts.length; i++) {
        const last = i === parts.length - 1;
        if (last) return await getChildFile(current, parts[i]);
        current = await getChildDirectory(current, parts[i]);
        if (!current) return null;
    }
    return null;
}

async function getChildDirectory(handle, name) {
    const child = await getChild(handle, name);
    return child && child.kind === "directory" ? child : null;
}

async function getChildFile(handle, name) {
    const child = await getChild(handle, name);
    if (!child || child.kind !== "file") return null;
    return await child.getFile();
}

async function getChild(handle, name) {
    try {
        return await handle.getDirectoryHandle(name);
    } catch {
    }
    try {
        return await handle.getFileHandle(name);
    } catch {
    }
    const wanted = name.toLowerCase();
    for await (const [entryName, entryHandle] of handle.entries()) {
        if (entryName.toLowerCase() === wanted) return entryHandle;
    }
    return null;
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
