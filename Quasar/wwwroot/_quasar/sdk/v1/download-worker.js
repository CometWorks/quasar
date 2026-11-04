// Only SDK-owned download URLs use this narrower worker. Ordinary app/push scopes stay intact.
const tickets = new Map();
const base = new URL("../../../../", self.registration.scope);
const stop = (ticket, reason) => {
    clearTimeout(ticket.timer);
    ticket.port.postMessage({ type: "cancel", reason });
    ticket.port.close();
};
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
self.addEventListener("message", event => {
    const message = event.data, port = event.ports[0];
    let source;
    try { source = new URL(event.source.url); } catch { return; }
    if (!port || source.origin !== base.origin || !source.pathname.startsWith(base.pathname)
        || message?.type !== "init" || !/^[a-f0-9]{64}$/.test(message.ticket) || tickets.has(message.ticket) || tickets.size >= 64
        || typeof message.contentType !== "string" || message.contentType.length > 256 || /[\r\n]/.test(message.contentType)
        || typeof message.fileName !== "string" || message.fileName.length > 255
        || message.length != null && (!Number.isSafeInteger(message.length) || message.length < 0)) return;
    const ticket = { ...message, port, owner: event.source.id };
    ticket.timer = setTimeout(() => { tickets.delete(message.ticket); stop(ticket, "Download was not started."); }, 60000);
    tickets.set(message.ticket, ticket);
    port.postMessage({ type: "registered" });
});
self.addEventListener("fetch", event => {
    const url = new URL(event.request.url);
    if (!url.href.startsWith(self.registration.scope)) return;
    const id = url.pathname.slice(new URL(self.registration.scope).pathname.length);
    const ticket = tickets.get(id);
    if (!ticket || event.clientId && event.clientId !== ticket.owner) {
        event.respondWith(new Response("Direct download is unavailable.", { status: 404 })); return;
    }
    tickets.delete(id); clearTimeout(ticket.timer);
    let pulling, finished = false;
    const body = new ReadableStream({
        start(controller) {
            ticket.port.onmessage = message => {
                if (finished) return;
                clearTimeout(ticket.timer);
                if (pulling && message.data?.type === "chunk" && message.data.bytes instanceof Uint8Array && message.data.bytes.byteLength <= 64 * 1024) {
                    controller.enqueue(message.data.bytes); pulling?.(); pulling = null;
                } else if (message.data?.type === "end") {
                    finished = true; controller.close(); pulling?.(); ticket.port.close();
                } else {
                    finished = true; controller.error(new Error("Direct download interrupted.")); pulling?.(); stop(ticket, "Download interrupted.");
                }
            };
        },
        pull(controller) {
            if (finished) return;
            ticket.port.postMessage({ type: "pull" });
            ticket.timer = setTimeout(() => {
                finished = true; controller.error(new Error("Direct download deadline.")); pulling?.(); stop(ticket, "Download stalled.");
            }, 30000);
            return new Promise(resolve => { pulling = resolve; });
        },
        cancel() { finished = true; clearTimeout(ticket.timer); stop(ticket, "Download cancelled."); pulling?.(); },
    }, { highWaterMark: 0 });
    const name = ticket.fileName.replace(/[\u0000-\u001f\u007f/\\]/g, "_") || "download";
    const headers = new Headers({ "Content-Type": ticket.contentType, "Cache-Control": "private, no-store",
        "Content-Disposition": `attachment; filename*=UTF-8''${encodeURIComponent(name.toWellFormed()).replace(/['()*]/g, value => "%" + value.charCodeAt(0).toString(16))}`,
        "X-Content-Type-Options": "nosniff" });
    if (ticket.length != null) headers.set("Content-Length", String(ticket.length));
    event.respondWith(new Response(body, { headers }));
});
