import { QuasarSdkError } from "./client.js";

const CHUNK = 16 * 1024, WINDOW = 256 * 1024, CONTROL = 66 * 1024;
const deferred = () => {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    promise.catch(() => {});
    return { promise, resolve, reject };
};
function wait(promise, signal) {
    return new Promise((resolve, reject) => {
        const abort = () => reject(signal.reason);
        if (signal.aborted) { abort(); return; }
        signal.addEventListener("abort", abort, { once: true });
        promise.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
    });
}
const integer = value => Number.isSafeInteger(value) && value >= 0;

export async function connect(root, tokens, lifetime) {
    if (!globalThis.RTCPeerConnection) throw new QuasarSdkError(503, "This browser cannot establish a peer connection.");
    try { return await pair(root, tokens, lifetime); }
    catch (error) {
        // A new transport attempt may fall back before any feature command runs.
        // Established streams/writes are never replayed across this boundary.
        if (tokens.relayTicket || !tokens.directEnabled || lifetime.aborted
            || !(error.status === 503 || error.name === "TimeoutError")) throw error;
        const response = await fetch(new URL("rtc/relay", root), { method: "POST", credentials: "same-origin", redirect: "error", cache: "no-store",
            signal: AbortSignal.any([lifetime, AbortSignal.timeout(10000)]), headers: { [tokens.headerName]: tokens.requestToken } });
        if (!response.ok) throw new QuasarSdkError(response.status, "Peer connectivity is unavailable or relay capacity is exhausted.");
        const relay = await response.json();
        if (!/^[a-f0-9]{32}$/.test(relay.ticket || "") || relay.validForSeconds !== 60) throw new QuasarSdkError(426, "Invalid relay setup.");
        return pair(root, { ...tokens, iceServers: relay.iceServers, relayTicket: relay.ticket }, lifetime);
    }
}

async function pair(root, tokens, lifetime) {
    if (!globalThis.RTCPeerConnection) throw new QuasarSdkError(503, "This browser cannot establish a direct connection.");
    const iceServers = tokens.iceServers || [];
    if (!Array.isArray(iceServers) || iceServers.length > 3 || iceServers.some(server => !Array.isArray(server.urls)
        || server.urls.length > 3 || server.urls.some(url => typeof url !== "string" || url.length > 256
            || !(url.startsWith("stun:") || tokens.relayTicket && (url.startsWith("turn:") || url.startsWith("turns:"))))
        || tokens.relayTicket && (typeof server.username !== "string" || server.username.length !== 32 || typeof server.credential !== "string" || server.credential.length !== 64)))
        throw new QuasarSdkError(426, "Incompatible peer NAT-discovery configuration.");
    const pc = new RTCPeerConnection({ iceServers, iceTransportPolicy: tokens.relayTicket ? "relay" : "all" });
    const control = pc.createDataChannel("quasar.control.v1", { ordered: true, protocol: "quasar-sdk.v1" });
    const bulk = pc.createDataChannel("quasar.bulk.v1", { ordered: true, protocol: "quasar-sdk.v1" });
    bulk.binaryType = "arraybuffer";
    const stop = new AbortController();
    const pending = new Map();
    let peerId, disconnected, next = 0, closed = false;
    const stats = { path: "unavailable", receivedBytes: 0, sentBytes: 0, maximumQueuedBytes: 0 };
    const post = async (path, body, signal = stop.signal) => {
        const response = await fetch(new URL(path, root), { method: "POST", credentials: "same-origin", redirect: "error", cache: "no-store", signal,
            headers: { "Content-Type": "application/json", [tokens.headerName]: tokens.requestToken }, body: JSON.stringify(body) });
        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            throw new QuasarSdkError(response.status, error.detail || "Peer connection setup failed.");
        }
        return response;
    };
    function remove(entry) {
        pending.delete(entry.id);
        entry.signal?.removeEventListener("abort", entry.abort);
    }
    function reject(entry, error) {
        remove(entry);
        entry.failed = error;
        entry.abortStream.abort(error);
        entry.result.reject(error); entry.ready.reject(error);
        entry.controller?.error(error);
        entry.pull?.resolve(); entry.credit.resolve();
        entry.queue.length = 0; entry.queued = 0;
    }
    function fail(error = new QuasarSdkError(503, "Peer connection lost; a write outcome may be unknown.")) {
        if (closed) return;
        closed = true; clearTimeout(disconnected); stats.path = "unavailable"; stop.abort(error);
        for (const entry of [...pending.values()]) reject(entry, error);
        pc.close();
        if (peerId) void fetch(new URL(`rtc/attempts/${peerId}`, root), { method: "DELETE", credentials: "same-origin", redirect: "error", cache: "no-store", keepalive: true,
            headers: { [tokens.headerName]: tokens.requestToken } }).catch(() => {});
    }
    const abort = () => fail(lifetime.reason);
    lifetime.addEventListener("abort", abort, { once: true });
    stop.signal.addEventListener("abort", () => lifetime.removeEventListener("abort", abort), { once: true });
    const buffers = new Map([[control, deferred()], [bulk, deferred()]]);
    for (const channel of [control, bulk]) {
        channel.bufferedAmountLowThreshold = WINDOW / 2;
        channel.onbufferedamountlow = () => { buffers.get(channel).resolve(); buffers.set(channel, deferred()); };
        channel.onerror = () => fail(); channel.onclose = () => fail();
    }
    pc.onconnectionstatechange = () => {
        clearTimeout(disconnected);
        if (["failed", "closed"].includes(pc.connectionState)) fail();
        // ICE's disconnected state is transient, including missed consent under
        // bounded TURN pacing. Keep the same reliable session briefly; no new
        // peer or feature replay occurs. Failed/closed remains terminal.
        else if (pc.connectionState === "disconnected") disconnected = setTimeout(() => fail(), 5000);
    };
    async function send(channel, data) {
        const bytes = typeof data === "string" ? new TextEncoder().encode(data).length : data.byteLength;
        while (channel.bufferedAmount + bytes > WINDOW) await wait(buffers.get(channel).promise, stop.signal);
        stop.signal.throwIfAborted();
        channel.send(data); stats.sentBytes += bytes;
    }
    let controlSending = Promise.resolve();
    function command(value) {
        const data = JSON.stringify({ v: 1, ...value });
        if (new TextEncoder().encode(data).length > CONTROL) throw new QuasarSdkError(413, "SDK request exceeds its control-frame limit.");
        const sending = controlSending.then(() => send(control, data));
        controlSending = sending.catch(() => {});
        return sending;
    }
    function credit(entry) {
        // Cumulative credit replenishes half a window at a time. TURN can batch
        // arrivals; acknowledging each chunk creates avoidable control bursts.
        if (entry.credited !== undefined && entry.consumed - entry.credited < WINDOW / 2) return;
        entry.credited = entry.consumed;
        void command({ type: "credit", id: entry.id, bytes: entry.consumed }).catch(fail);
    }
    function finishStream(entry) {
        if (entry.expected === entry.received && entry.queue.length === 0 && entry.controller) {
            entry.controller.close(); remove(entry); entry.pull?.resolve();
        }
    }
    function consume(entry) {
        if (entry.queue.length === 0) { finishStream(entry); return; }
        const chunk = entry.queue.shift(); entry.queued -= chunk.byteLength;
        entry.consumed += chunk.byteLength;
        entry.controller.enqueue(chunk); credit(entry);
        finishStream(entry);
    }
    function body(entry) {
        return new ReadableStream({
            start(controller) { entry.controller = controller; },
            pull() {
                if (!entry.started) { entry.started = true; credit(entry); }
                if (entry.queue.length || entry.expected === entry.received) { consume(entry); return; }
                entry.pull = deferred(); return entry.pull.promise;
            },
            cancel(reason) { void command({ type: "cancel", id: entry.id }).catch(() => {}); reject(entry, reason || new DOMException("Cancelled", "AbortError")); },
        }, { highWaterMark: 0 });
    }
    control.onmessage = event => {
        try {
            if (typeof event.data !== "string" || new TextEncoder().encode(event.data).length > CONTROL) throw new Error("Invalid direct control frame");
            stats.receivedBytes += new TextEncoder().encode(event.data).length;
            const message = JSON.parse(event.data);
            if (message.v !== 1 || !integer(message.id) || message.id < 1 || message.id > 0xffffffff) throw new Error("Invalid direct response");
            const entry = pending.get(message.id);
            if (!entry) { if (message.id > next) throw new Error("Unknown direct response"); return; }
            switch (message.type) {
            case "result": remove(entry); entry.result.resolve({ value: message.value }); break;
            case "reserved":
                if (!/^[a-f0-9]{32}$/.test(message.resource) || !["read", "upload", "subscription"].includes(message.kind)) throw new Error("Invalid direct resource");
                remove(entry); entry.result.resolve({ value: message }); break;
            case "released": remove(entry); entry.result.resolve({ value: null }); break;
            case "error":
                if (!Number.isInteger(message.status) || message.status < 400 || message.status > 599) throw new Error("Invalid direct error");
                reject(entry, new QuasarSdkError(message.status, typeof message.detail === "string" ? message.detail : "SDK operation failed.")); break;
            case "json": case "stream":
                if (entry.controller || message.length != null && !integer(message.length)) throw new Error("Invalid direct stream metadata");
                entry.length = message.length;
                entry.result.resolve({ body: body(entry), contentType: message.contentType || "application/octet-stream", fileName: message.fileName || "",
                    length: message.length ?? null, contentRange: message.contentRange ?? null, status: message.status || 200, streamed: true });
                break;
            case "end":
                if (!integer(message.bytes) || message.bytes < entry.received || entry.length != null && entry.length !== message.bytes) throw new Error("Invalid direct stream completion");
                entry.expected = message.bytes; finishStream(entry); break;
            case "circuit.message":
                if (!entry.circuit || !integer(message.bytes) || message.bytes < entry.messageEnd || message.bytes - entry.messageEnd > 8 * 1024 * 1024
                    || !["text", "binary"].includes(message.kind)) throw new Error("Invalid circuit message boundary");
                entry.messageEnd = message.bytes;
                if (entry.onmessage) entry.onmessage(message);
                else { if (entry.messages.length >= 64) throw new Error("Circuit message queue exceeded"); entry.messages.push(message); }
                break;
            case "upload":
                if (message.window !== 65536) throw new Error("Invalid upload window");
                entry.uploadWindow = message.window; entry.ready.resolve(); break;
            case "ack":
                if (!integer(message.bytes) || message.bytes < entry.acknowledged || message.bytes > entry.sent) throw new Error("Invalid upload acknowledgment");
                entry.acknowledged = message.bytes; entry.credit.resolve(); entry.credit = deferred(); break;
            default: throw new Error("Unknown direct response type");
            }
        } catch { fail(new QuasarSdkError(502, "Invalid direct SDK response.")); }
    };
    bulk.onmessage = event => {
        try {
            if (!(event.data instanceof ArrayBuffer) || event.data.byteLength <= 8 || event.data.byteLength > CHUNK + 8) throw new Error("Invalid direct binary frame");
            stats.receivedBytes += event.data.byteLength;
            const header = new DataView(event.data);
            const id = header.getUint32(0, true), sequence = header.getUint32(4, true);
            const entry = pending.get(id);
            if (!entry) { if (id > next) throw new Error("Unknown direct stream"); return; }
            if (!entry.controller || entry.sequence++ !== sequence) throw new Error("Invalid direct binary sequence");
            const data = new Uint8Array(event.data, 8);
            entry.received += data.byteLength; entry.queued += data.byteLength;
            if (entry.queued > WINDOW || entry.expected != null && entry.received > entry.expected || entry.length != null && entry.received > entry.length) throw new Error("Direct receive window exceeded");
            stats.maximumQueuedBytes = Math.max(stats.maximumQueuedBytes, entry.queued);
            entry.queue.push(data);
            if (entry.pull) { const wake = entry.pull; entry.pull = null; consume(entry); wake.resolve(); }
        } catch { fail(new QuasarSdkError(502, "Invalid direct SDK binary response.")); }
    };
    function invoke(type, fields, signal) {
        stop.signal.throwIfAborted(); signal?.throwIfAborted();
        if (pending.size >= (type === "release" ? 12 : 8)) throw new QuasarSdkError(429, "Too many concurrent direct SDK operations.");
        if (next === 0xffffffff) { fail(); throw new QuasarSdkError(503, "Reconnect before starting more SDK work."); }
        const entry = { id: ++next, result: deferred(), ready: deferred(), credit: deferred(), signal, queue: [], queued: 0, received: 0, consumed: 0,
            sequence: 0, expected: null, sent: 0, acknowledged: 0, failed: null, circuit: type === "circuit", messages: [], messageEnd: 0, abortStream: new AbortController() };
        entry.abort = () => { void command({ type: "cancel", id: entry.id }).catch(() => {}); reject(entry, signal.reason); };
        signal?.addEventListener("abort", entry.abort, { once: true });
        pending.set(entry.id, entry);
        void command({ type, id: entry.id, ...fields }).catch(error => reject(entry, error));
        return entry;
    }
    async function sendInput(entry, bytes, nextSequence, signal) {
        for (let offset = 0; offset < bytes.length; offset += CHUNK) {
            const chunk = bytes.subarray(offset, offset + CHUNK);
            while (entry.sent - entry.acknowledged + chunk.byteLength > entry.uploadWindow) {
                if (entry.failed) throw entry.failed;
                await wait(entry.credit.promise, signal);
            }
            signal.throwIfAborted();
            if (entry.failed) throw entry.failed;
            const frame = new Uint8Array(chunk.byteLength + 8);
            const header = new DataView(frame.buffer);
            header.setUint32(0, entry.id, true); header.setUint32(4, nextSequence(), true);
            frame.set(chunk, 8); entry.sent += chunk.byteLength;
            await send(bulk, frame);
        }
    }
    const value = async entry => {
        const result = await entry.result.promise;
        return result.streamed ? new Response(result.body, { headers: { "Content-Type": "application/json" } }).json() : result.value;
    };
    try {
        lifetime.throwIfAborted();
        const opened = Promise.all([new Promise(resolve => control.onopen = resolve), new Promise(resolve => bulk.onopen = resolve)]);
        await pc.setLocalDescription(await pc.createOffer());
        if (pc.iceGatheringState !== "complete") await wait(new Promise(resolve => pc.onicegatheringstatechange = () => {
            if (pc.iceGatheringState === "complete") resolve();
        }), AbortSignal.any([stop.signal, AbortSignal.timeout(10000)]));
        const nonce = [...crypto.getRandomValues(new Uint8Array(32))].map(byte => byte.toString(16).padStart(2, "0")).join("");
        const setup = await (await post("rtc/attempts", { version: 1, nonce, description: pc.localDescription.toJSON(), ...(tokens.relayTicket ? { relayTicket: tokens.relayTicket } : {}) })).json();
        if (setup.version !== 1 || !/^[a-f0-9]{32}$/.test(setup.id) || !/^[a-fA-F0-9]{64}$/.test(setup.grant) || setup.description?.type !== "answer")
            throw new QuasarSdkError(426, "Incompatible direct SDK setup.");
        peerId = setup.id;
        await pc.setRemoteDescription(setup.description);
        await wait(opened, AbortSignal.any([stop.signal, AbortSignal.timeout(15000)]));
        if (!pc.sctp || pc.sctp.maxMessageSize < CONTROL) throw new QuasarSdkError(426, "This browser cannot support the SDK message size.");
        const confirmed = await post(`rtc/attempts/${peerId}/confirm`, { grant: setup.grant });
        stats.path = confirmed.headers.get("X-Quasar-Transport");
        if (!["direct", "relayed"].includes(stats.path)) throw new QuasarSdkError(426, "Invalid peer path confirmation.");
    } catch (error) { fail(error); throw error; }

    return Object.freeze({
        get closed() { return closed; }, stats,
        request(plugin, operation, payload, signal) { return value(invoke("request", { plugin, operation, payload }, signal)); },
        reserve(kind, plugin, operation, payload, signal) { return value(invoke("reserve", { kind, plugin, operation, payload }, signal)); },
        read(resource, range, signal) { return invoke("start", { resource, range }, signal).result.promise; },
        async upload(resource, source, signal) {
            const stream = source instanceof Blob ? source.stream() : source instanceof ReadableStream ? source
                : source instanceof ArrayBuffer || ArrayBuffer.isView(source) ? new Blob([source]).stream() : null;
            if (!stream) throw new TypeError("SDK uploads require a Blob, File, ArrayBuffer or ReadableStream.");
            const entry = invoke("start", { resource }, signal);
            signal = AbortSignal.any([signal || stop.signal, stop.signal, entry.abortStream.signal]);
            await wait(entry.ready.promise, signal);
            const reader = stream.getReader();
            let sequence = 0;
            try {
                while (true) {
                    const { done, value: bytes } = await wait(reader.read(), signal);
                    if (done) break;
                    if (!(bytes instanceof Uint8Array)) throw new TypeError("SDK upload streams must contain Uint8Array chunks.");
                    await sendInput(entry, bytes, () => sequence++, signal);
                }
                await command({ type: "upload.end", id: entry.id, bytes: entry.sent });
                return await value(entry);
            } catch (error) {
                void command({ type: "cancel", id: entry.id }).catch(() => {});
                reject(entry, error); throw error;
            } finally { await reader.cancel().catch(() => {}); }
        },
        async circuit(ticket) {
            const entry = invoke("circuit", { ticket }, stop.signal);
            const result = await entry.result.promise;
            await wait(entry.ready.promise, stop.signal);
            const signal = AbortSignal.any([stop.signal, entry.abortStream.signal]);
            let sequence = 0, sending = Promise.resolve();
            return {
                body: result.body,
                set onmessage(callback) { entry.onmessage = callback; for (const message of entry.messages.splice(0)) callback(message); },
                send(bytes) {
                    const task = sending.then(() => sendInput(entry, bytes, () => sequence++, signal));
                    sending = task.catch(() => {}); return task;
                },
                close() { void command({ type: "cancel", id: entry.id }).catch(() => {}); reject(entry, new DOMException("Circuit closed", "AbortError")); },
            };
        },
        async release(resource) {
            if (closed) return;
            try { await value(invoke("release", { resource })); } catch { /* teardown cannot replay feature work */ }
        },
        close() { fail(); },
    });
}

export async function download(root, stream, fileName, signal) {
    if (!globalThis.isSecureContext || !navigator.serviceWorker)
        throw new QuasarSdkError(409, "Direct browser downloads require a secure browser context.");
    const registration = await navigator.serviceWorker.register(new URL("download-worker.js", root), { scope: new URL("download/", root).href });
    const worker = registration.active || registration.installing || registration.waiting;
    if (!worker) throw new QuasarSdkError(503, "Direct download worker is unavailable.");
    if (worker.state !== "activated") await wait(new Promise((resolve, reject) => worker.addEventListener("statechange", () => {
        if (worker.state === "activated") resolve();
        if (worker.state === "redundant") reject(new QuasarSdkError(503, "Direct download worker could not start."));
    })), AbortSignal.any([signal, AbortSignal.timeout(15000)]));
    const channel = new MessageChannel(), ready = deferred(), done = deferred();
    const reader = stream.body.getReader();
    let finished = false, pulling = false;
    function finish(error) {
        if (finished) return;
        finished = true; signal.removeEventListener("abort", abort);
        if (error) {
            channel.port1.postMessage({ type: "error" }); done.reject(error);
            void reader.cancel(error).catch(() => {});
        } else done.resolve();
        channel.port1.close();
    }
    const abort = () => finish(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    channel.port1.onmessage = async event => {
        if (event.data?.type === "registered") { ready.resolve(); return; }
        if (event.data?.type === "cancel") { finish(new DOMException("Download cancelled", "AbortError")); return; }
        if (event.data?.type !== "pull" || pulling || finished) { finish(new Error("Invalid direct download request.")); return; }
        pulling = true;
        try {
            const result = await reader.read();
            if (result.done) { channel.port1.postMessage({ type: "end" }); finish(); }
            else channel.port1.postMessage({ type: "chunk", bytes: result.value }, [result.value.buffer]);
        } catch (error) { finish(error); }
        finally { pulling = false; }
    };
    const ticket = [...crypto.getRandomValues(new Uint8Array(32))].map(byte => byte.toString(16).padStart(2, "0")).join("");
    worker.postMessage({ type: "init", ticket, contentType: stream.contentType || "application/octet-stream",
        fileName: (fileName || stream.fileName || "download").slice(0, 255), length: stream.length }, [channel.port2]);
    try {
        await wait(ready.promise, AbortSignal.any([signal, AbortSignal.timeout(10000)]));
        const anchor = document.createElement("a");
        anchor.href = new URL("download/" + ticket, root).href; anchor.hidden = true;
        document.body.append(anchor); anchor.click(); anchor.remove();
        return done.promise;
    } catch (error) { finish(error); throw error; }
}
