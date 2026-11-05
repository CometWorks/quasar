import { connect } from "./rtc.js";
import { QuasarSdkError } from "./client.js";

// SignalR's public ITransport contract. The standard framework retains its
// negotiation, hub protocol, rendering, JS interop and circuit reconnect logic.
export function circuitTransport(root, tokens) {
    let lifetime, peer, pipe, reader, receiving, preparing, closing = false;
    const transport = {
        onreceive: null, onclose: null,
        async prepare() {
            if (peer && !peer.closed) return peer;
            if (!preparing) {
                closing = false; lifetime = new AbortController();
                const pending = connect(root, tokens, lifetime.signal).then(value => peer = value);
                preparing = pending;
                pending.finally(() => { if (preparing === pending) preparing = null; }).catch(() => {});
            }
            return preparing;
        },
        async connect(url, transferFormat) {
            if (transferFormat !== 2) throw new QuasarSdkError(426, "Blazor requires binary circuit transport.");
            const address = new URL(url);
            const expected = new URL("circuit", root);
            const ticket = address.searchParams.get("id");
            if (address.origin !== expected.origin || address.pathname !== expected.pathname || !/^[a-zA-Z0-9_-]{16,128}$/.test(ticket || ""))
                throw new QuasarSdkError(400, "Invalid framework circuit negotiation.");
            closing = false;
            try {
                peer = await transport.prepare();
                pipe = await peer.circuit(ticket);
                reader = pipe.body.getReader();
                const chunks = [], boundaries = [];
                let received = 0, delivered = 0, offset = 0;
                function drain() {
                    while (boundaries.length && boundaries[0].bytes <= received) {
                        const boundary = boundaries.shift(), size = boundary.bytes - delivered;
                        const message = new Uint8Array(size);
                        let copied = 0;
                        while (copied < size) {
                            const chunk = chunks[0], count = Math.min(size - copied, chunk.length - offset);
                            message.set(chunk.subarray(offset, offset + count), copied); copied += count; offset += count;
                            if (offset === chunk.length) { chunks.shift(); offset = 0; }
                        }
                        delivered = boundary.bytes;
                        transport.onreceive?.(boundary.kind === "text" ? new TextDecoder().decode(message) : message.buffer);
                    }
                    if (received - delivered > 8 * 1024 * 1024) throw new QuasarSdkError(413, "Circuit message exceeds 8 MiB.");
                }
                pipe.onmessage = boundary => { if (boundaries.length >= 64) throw new Error("Too many circuit messages."); boundaries.push(boundary); drain(); };
                receiving = (async () => {
                    let error;
                    try {
                        while (true) {
                            const { value, done } = await reader.read();
                            if (done) break;
                            received += value.byteLength; chunks.push(value); drain();
                        }
                    } catch (failure) { error = failure; }
                    finally { if (!closing) { lifetime.abort(error); peer.close(); transport.onclose?.(error); } }
                })();
            } catch (error) { lifetime.abort(error); throw error; }
        },
        send(data) {
            if (!pipe || closing) return Promise.reject(new QuasarSdkError(503, "The peer circuit is closed."));
            return pipe.send(typeof data === "string" ? new TextEncoder().encode(data) : new Uint8Array(data));
        },
        async stop() {
            if (closing) return;
            closing = true; lifetime?.abort(); pipe?.close(); peer?.close();
            await reader?.cancel().catch(() => {}); await receiving;
            pipe = null; reader = null;
            transport.onclose?.();
        },
    };
    return transport;
}

export async function startBlazor({ hosted, baseUrl = document.baseURI } = {}) {
    if (!hosted) { await Blazor.start(); return; }
    const base = new URL(baseUrl, location.href);
    if (base.origin !== location.origin || !base.pathname.endsWith("/") || base.search || base.hash || base.username || base.password) throw new TypeError("Invalid panel base URL.");
    const root = new URL("_quasar/sdk/v1/", base);
    const response = await fetch(new URL("session", root), { credentials: "same-origin", redirect: "error", cache: "no-store", signal: AbortSignal.timeout(30000) });
    if (!response.ok) throw new QuasarSdkError(response.status, "The authenticated hosted session is unavailable.");
    const tokens = await response.json();
    if (!tokens.directEnabled) throw new QuasarSdkError(426, "This hosted session requires peer circuit support.");
    const transport = circuitTransport(root, tokens);
    await Blazor.start({ circuit: { configureSignalR(builder) {
        builder.withUrl(new URL("circuit", root).href, {
            transport, httpClient: negotiationHttpClient(root, transport), headers: { [tokens.headerName]: tokens.requestToken }, withCredentials: true,
        });
    } } });
}

// Complete ICE before allocating the framework's short-lived negotiation token.
// This applies to initial startup and framework reconnects without replacing its
// negotiation protocol or depending on an internal HttpConnectionManager timeout.
function negotiationHttpClient(root, transport) {
    const client = {
        getCookieString() { return ""; },
        get(url, options) { return client.send({ ...options, url, method: "GET" }); },
        post(url, options) { return client.send({ ...options, url, method: "POST" }); },
        delete(url, options) { return client.send({ ...options, url, method: "DELETE" }); },
        async send(request) {
            const url = new URL(request.url), expected = new URL("circuit/negotiate", root);
            if (request.method !== "POST" || url.origin !== expected.origin || url.pathname !== expected.pathname)
                throw new QuasarSdkError(400, "Unsupported framework transport request.");
            const abort = new AbortController();
            const cancel = () => { abort.abort(); void transport.stop(); };
            const timeout = setTimeout(cancel, request.timeout || 100000);
            if (request.abortSignal) { request.abortSignal.onabort = cancel; if (request.abortSignal.aborted) cancel(); }
            try {
                await transport.prepare(); abort.signal.throwIfAborted();
                const response = await fetch(url, { method: "POST", body: request.content, headers: request.headers,
                    credentials: "same-origin", redirect: "error", cache: "no-store", signal: abort.signal });
                const result = { statusCode: response.status, statusText: response.statusText, content: await response.text() };
                if (!response.ok) await transport.stop();
                return result;
            } catch (error) { await transport.stop(); throw error; }
            finally { clearTimeout(timeout); if (request.abortSignal) request.abortSignal.onabort = null; }
        },
    };
    return client;
}
