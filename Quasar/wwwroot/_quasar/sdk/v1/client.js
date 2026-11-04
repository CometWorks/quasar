export class QuasarSdkError extends Error {
    constructor(status, detail) { super(detail); this.name = "QuasarSdkError"; this.status = status; }
}

export function createClient({ baseUrl }) {
    const base = new URL(baseUrl, globalThis.location?.href);
    if (base.origin !== globalThis.location?.origin || !base.pathname.endsWith("/") || base.search || base.hash || base.username || base.password)
        throw new TypeError("An explicit same-origin panel base URL ending in / is required.");
    const root = new URL("_quasar/sdk/v1/", base);
    const identifier = /^[a-z0-9]+(?:[._-][a-z0-9]+)*$/;
    const active = new Set();
    let disposed = false;

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
        const result = await (await checked(await fetch(new URL("session", root), {
            credentials: "same-origin", redirect: "error", cache: "no-store", signal: entry.controller.signal,
        }))).json();
        if (result.version !== 1 || !result.headerName || !result.requestToken)
            throw new QuasarSdkError(426, "Incompatible Quasar SDK session.");
        return result;
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
        try {
            const tokens = await session(entry);
            const result = await (await post(`resources/${plugin}/${operation}`, body, entry, tokens)).json();
            const url = new URL(result.url, base);
            if (result.kind !== kind || !/^[a-f0-9]{32}$/.test(result.id) || url.href !== new URL(`resources/${result.id}`, root).href)
                throw new QuasarSdkError(426, "Incompatible SDK resource response.");
            let released = false;
            entry.dispose = async () => {
                if (released) return;
                released = true;
                entry.controller.abort(); entry.finish();
                await fetch(url, { method: "DELETE", credentials: "same-origin", redirect: "error", cache: "no-store", keepalive: true,
                    headers: { [tokens.headerName]: tokens.requestToken } }).catch(() => {});
            };
            return { entry, url, tokens };
        } catch (error) { entry.finish(); throw error; }
    }
    async function readResource(kind, plugin, operation, payload, { signal, range } = {}) {
        const handle = await resource(kind, plugin, operation, payload, signal);
        try {
            const response = await checked(await fetch(handle.url, { credentials: "same-origin", redirect: "error", cache: "no-store",
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
            return { body, contentType: response.headers.get("Content-Type"), length: response.headers.has("Content-Length") ? Number(response.headers.get("Content-Length")) : null,
                contentRange: response.headers.get("Content-Range"), status: response.status,
                blob: () => new Response(body, { headers: response.headers }).blob(),
                async dispose() { await handle.entry.dispose(); await reader.cancel().catch(() => {}); } };
        } catch (error) { await handle.entry.dispose(); throw error; }
    }
    const client = {
        version: 1,
        async request(plugin, operation, payload = {}, { signal } = {}) {
            const body = input(plugin, operation, payload);
            const entry = begin(signal);
            try { return await (await post(`operations/${plugin}/${operation}`, body, entry, await session(entry))).json(); }
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
            const anchor = document.createElement("a");
            anchor.href = handle.url.href; anchor.download = fileName; anchor.hidden = true;
            document.body.append(anchor); anchor.click(); anchor.remove();
            const timeout = setTimeout(() => { handle.entry.finish(); }, 10 * 60 * 1000);
            const dispose = handle.entry.dispose;
            handle.entry.dispose = async () => { clearTimeout(timeout); await dispose(); };
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            globalThis.removeEventListener?.("pagehide", client.dispose);
            for (const entry of [...active]) { if (entry.dispose) void entry.dispose(); else { entry.controller.abort(); entry.finish(); } }
        },
    };
    globalThis.addEventListener?.("pagehide", client.dispose);
    return Object.freeze(client);
}
