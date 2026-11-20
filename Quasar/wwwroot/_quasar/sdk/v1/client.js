// Copyright (c) 2026 CometWorks. SPDX-License-Identifier: MIT
// See LICENSE.MIT in the Quasar source or distribution.
// Share panel capacity across same-origin plugin frames. Feature plugins never schedule network work.
const panelsKey = Symbol.for("Quasar.SDK.v1.panels");
function panelState(base) {
    let owner = globalThis;
    try { if (globalThis.top?.location.origin === base.origin) owner = globalThis.top; } catch { /* standalone/cross-origin */ }
    const panels = owner[panelsKey] ||= new Map();
    if (!panels.has(base.href)) panels.set(base.href, { running: 0, waiting: [], session: null });
    return panels.get(base.href);
}
function acquire(state, signal) {
    return new Promise((resolve, reject) => {
        if (signal.aborted) { reject(signal.reason); return; }
        if (state.waiting.length >= 128) { reject(new QuasarSdkError(429, "Too many pending SDK transfers.")); return; }
        const start = () => {
            signal.removeEventListener("abort", cancel);
            state.running++;
            let released = false;
            resolve(() => {
                if (released) return;
                released = true;
                state.running--;
                state.waiting.shift()?.();
            });
        };
        const cancel = () => { state.waiting.splice(state.waiting.indexOf(start), 1); reject(signal.reason); };
        if (state.running < 4) start();
        else { state.waiting.push(start); signal.addEventListener("abort", cancel, { once: true }); }
    });
}
function untilAborted(promise, signal) {
    return new Promise((resolve, reject) => {
        const cancel = () => reject(signal.reason);
        if (signal.aborted) { cancel(); return; }
        signal.addEventListener("abort", cancel, { once: true });
        promise.then(resolve, reject).finally(() => signal.removeEventListener("abort", cancel));
    });
}

export class QuasarSdkError extends Error {
    constructor(status, detail) { super(detail); this.name = "QuasarSdkError"; this.status = status; }
}

export function createClient({ baseUrl }) {
    const base = new URL(baseUrl, globalThis.location?.href);
    if (base.origin !== globalThis.location?.origin || !base.pathname.endsWith("/") || base.search || base.hash || base.username || base.password)
        throw new TypeError("An explicit same-origin panel base URL ending in / is required.");
    const root = new URL("_quasar/sdk/v1/", base);
    const identifier = /^[a-z0-9]+(?:[._-][a-z0-9]+)*$/;
    const state = panelState(base);
    const active = new Set();
    let disposed = false;
    let direct, directConnection, directModule, directRequired = false;
    let connectionLifetime = new AbortController();
    async function transport(tokens, entry) {
        if (!tokens.directEnabled) return null;
        directRequired = true;
        if (directConnection?.closed) { direct = null; directConnection = null; }
        if (!direct) {
            directModule ||= import("./rtc.js");
            const lifetime = connectionLifetime.signal;
            direct = directModule.then(module => module.connect(root, tokens, lifetime)).then(connection => directConnection = connection);
            direct.catch(() => { direct = null; });
        }
        return untilAborted(direct, entry.controller.signal);
    }

    function begin(signal) {
        if (disposed) throw new Error("The Quasar SDK client is disposed.");
        const controller = new AbortController();
        const abort = () => controller.abort(signal.reason);
        if (signal?.aborted) abort();
        else signal?.addEventListener("abort", abort, { once: true });
        const entry = { controller, finish() { active.delete(entry); signal?.removeEventListener("abort", abort); } };
        active.add(entry);
        return entry;
    }
    function input(plugin, operation, payload) {
        if (![plugin, operation].every(value => typeof value === "string" && value.length <= 128 && identifier.test(value)))
            throw new TypeError("Invalid plugin or operation identifier.");
        const body = JSON.stringify(payload);
        if (body === undefined || new TextEncoder().encode(body).length > 64 * 1024)
            throw new TypeError("SDK control requests must contain at most 64 KiB of JSON.");
        return body;
    }
    async function checked(response) {
        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            throw new QuasarSdkError(response.status, error.detail || error.title || "Quasar SDK request failed.");
        }
        return response;
    }
    async function session(entry) {
        // Coalesce initial cookie setup so simultaneous views cannot reserve under different bindings.
        const pending = state.session ||= (async () => {
            const result = await (await checked(await fetch(new URL("session", root), {
                credentials: "same-origin", redirect: "error", cache: "no-store", signal: AbortSignal.timeout(30000),
            }))).json();
            if (result.version !== 1 || !result.headerName || !result.requestToken)
                throw new QuasarSdkError(426, "Incompatible Quasar SDK session.");
            return result;
        })().finally(() => { state.session = null; });
        return untilAborted(pending, entry.controller.signal);
    }

    async function post(path, body, entry, tokens) {
        return checked(await fetch(new URL(path, root), {
            method: "POST", credentials: "same-origin", redirect: "error", cache: "no-store", signal: entry.controller.signal,
            headers: { "Content-Type": "application/json", [tokens.headerName]: tokens.requestToken }, body,
        }));
    }
    async function resource(kind, plugin, operation, payload, signal) {
        const body = input(plugin, operation, payload);
        const entry = begin(signal);
        let release;
        try {
            release = await acquire(state, entry.controller.signal);
            entry.releaseCapacity = release;
            const finish = entry.finish;
            entry.finish = () => { release(); finish(); };
            const tokens = await session(entry);
            const native = await transport(tokens, entry);
            let response, result;
            // Only explicit capacity rejection may retry reservation. No feature handler has run yet.
            for (let attempt = 0; ; attempt++) {
                try {
                    if (native) result = await native.reserve(kind, plugin, operation, JSON.parse(body), entry.controller.signal);
                    else response = await post(`resources/${plugin}/${operation}`, body, entry, tokens);
                    break;
                }
                catch (error) {
                    if (error.status !== 429 || attempt >= 40) throw error;
                    await untilAborted(new Promise(resolve => setTimeout(resolve, 250)), entry.controller.signal);
                }
            }
            result ||= await response.json();
            const id = native ? result.resource : result.id;
            const url = new URL(native ? `resources/${id}` : result.url, native ? root : base);
            if (result.kind !== kind || !/^[a-f0-9]{32}$/.test(id) || url.href !== new URL(`resources/${id}`, root).href)
                throw new QuasarSdkError(426, "Incompatible SDK resource response.");
            let released = false;
            entry.dispose = async () => {
                if (released) return;
                released = true;
                entry.controller.abort(); entry.finish();
                if (native) { await native.release(id); return; }
                await fetch(url, { method: "DELETE", credentials: "same-origin", redirect: "error", cache: "no-store", keepalive: true,
                    headers: { [tokens.headerName]: tokens.requestToken } }).catch(() => {});
            };
            entry.controller.signal.addEventListener("abort", () => { void entry.dispose(); }, { once: true });
            entry.controller.signal.throwIfAborted();
            return { entry, url, tokens, native, id };
        } catch (error) { release?.(); entry.finish(); throw error; }
    }
    async function readResource(kind, plugin, operation, payload, { signal, range } = {}) {
        const handle = await resource(kind, plugin, operation, payload, signal);
        try {
            const transferred = handle.native ? await handle.native.read(handle.id, range, handle.entry.controller.signal) : null;
            const response = transferred ? new Response(transferred.body, { status: transferred.status, headers: {
                "Content-Type": transferred.contentType, ...(transferred.length != null ? { "Content-Length": String(transferred.length) } : {}),
                ...(transferred.contentRange ? { "Content-Range": transferred.contentRange } : {}),
            } }) : await checked(await fetch(handle.url, { credentials: "same-origin", redirect: "error", cache: "no-store",
                signal: handle.entry.controller.signal, headers: range ? { Range: range } : {} }));
            const reader = response.body.getReader();
            const body = new ReadableStream({
                async pull(controller) {
                    try {
                        const result = await reader.read();
                        if (result.done) { handle.entry.finish(); controller.close(); }
                        else controller.enqueue(result.value);
                    } catch (error) { handle.entry.finish(); controller.error(error); }
                },
                async cancel(reason) { await reader.cancel(reason).catch(() => {}); await handle.entry.dispose(); },
            }, { highWaterMark: 0 });
            return { body, native: handle.native, fileName: transferred?.fileName, contentType: response.headers.get("Content-Type"), length: response.headers.has("Content-Length") ? Number(response.headers.get("Content-Length")) : null,
                contentRange: response.headers.get("Content-Range"), status: response.status,
                blob: () => new Response(body, { headers: response.headers }).blob(),
                async dispose() { await handle.entry.dispose(); await reader.cancel().catch(() => {}); } };
        } catch (error) { await handle.entry.dispose(); throw error; }
    }
    function cancelOwned() {
        for (const entry of [...active]) { if (entry.dispose) void entry.dispose(); else { entry.controller.abort(); entry.finish(); } }
    }
    function pagehide(event) {
        // A restored back/forward-cache page must remain usable, without replaying its old work.
        if (event.persisted) { cancelOwned(); connectionLifetime.abort(); connectionLifetime = new AbortController(); direct = null; directConnection = null; }
        else client.dispose();
    }
    const client = {
        version: 1,
        get transport() { return Object.freeze(directConnection ? { ...directConnection.stats } : { path: directRequired ? "unavailable" : "http", receivedBytes: 0, sentBytes: 0, maximumQueuedBytes: 0 }); },
        async request(plugin, operation, payload = {}, { signal } = {}) {
            const body = input(plugin, operation, payload);
            const entry = begin(signal);
            try {
                const tokens = await session(entry);
                const native = await transport(tokens, entry);
                return native ? await native.request(plugin, operation, JSON.parse(body), entry.controller.signal)
                    : await (await post(`operations/${plugin}/${operation}`, body, entry, tokens)).json();
            }
            finally { entry.finish(); }
        },
        openRead(plugin, operation, payload = {}, options = {}) { return readResource("read", plugin, operation, payload, options); },
        async openWrite(plugin, operation, payload = {}, { signal } = {}) {
            const handle = await resource("upload", plugin, operation, payload, signal);
            let used = false;
            return {
                async write(body) {
                    if (used) throw new Error("SDK uploads are single-use; never replay an unknown write outcome.");
                    used = true;
                    try {
                        if (handle.native) return await handle.native.upload(handle.id, body, handle.entry.controller.signal);
                        const options = { method: "PUT", credentials: "same-origin", redirect: "error", cache: "no-store", signal: handle.entry.controller.signal,
                            headers: { [handle.tokens.headerName]: handle.tokens.requestToken }, body };
                        if (body instanceof ReadableStream) options.duplex = "half";
                        return await (await checked(await fetch(handle.url, options))).json();
                    } catch (error) { await handle.entry.dispose(); throw error; }
                    finally { handle.entry.finish(); }
                },
                dispose: handle.entry.dispose,
            };
        },
        async *subscribe(plugin, operation, payload = {}, options = {}) {
            const stream = await readResource("subscription", plugin, operation, payload, options);
            const reader = stream.body.getReader();
            const decoder = new TextDecoder();
            let pending = "";
            try {
                while (true) {
                    const { done, value } = await reader.read();
                    const parts = (pending + (done ? decoder.decode() : decoder.decode(value, { stream: true }))).split("\n");
                    pending = parts.pop();
                    for (const line of parts) {
                        if (line.length > 65536) throw new QuasarSdkError(413, "Subscription item exceeds its limit.");
                        if (line) yield JSON.parse(line);
                    }
                    if (pending.length > 65536) throw new QuasarSdkError(413, "Subscription item exceeds its limit.");
                    if (done) { if (pending) throw new Error("Incomplete SDK subscription item."); break; }
                }
            } finally { await reader.cancel().catch(() => {}); await stream.dispose(); }
        },
        async download(plugin, operation, payload = {}, { signal, fileName = "" } = {}) {
            const handle = await resource("read", plugin, operation, payload, signal);
            if (handle.native) {
                try {
                    const stream = await handle.native.read(handle.id, undefined, handle.entry.controller.signal);
                    const module = await directModule;
                    await module.download(root, stream, fileName, handle.entry.controller.signal);
                } finally { await handle.entry.dispose(); }
                return;
            }
            const anchor = document.createElement("a");
            anchor.href = handle.url.href; anchor.download = fileName; anchor.hidden = true;
            document.body.append(anchor); anchor.click(); anchor.remove();
            // Browser-native downloads have no completion callback. Server capacity remains authoritative.
            handle.entry.releaseCapacity();
            const timeout = setTimeout(() => { handle.entry.finish(); }, 10 * 60 * 1000);
            const dispose = handle.entry.dispose;
            handle.entry.dispose = async () => { clearTimeout(timeout); await dispose(); };
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            globalThis.removeEventListener?.("pagehide", pagehide);
            cancelOwned(); connectionLifetime.abort(); direct = null;
        },
    };
    globalThis.addEventListener?.("pagehide", pagehide);
    return Object.freeze(client);
}
