import test from "node:test";
import assert from "node:assert/strict";
import { createClient, QuasarSdkError } from "../Quasar/wwwroot/_quasar/sdk/v1/client.js";

const originalFetch = globalThis.fetch;
const originalLocation = globalThis.location;
test.beforeEach(() => { globalThis.location = { href: "https://dash.test/my-panel/_quasar/plugins/viewer/", origin: "https://dash.test" }; });
test.afterEach(() => { globalThis.fetch = originalFetch; globalThis.location = originalLocation; });

test("preserves the explicit panel prefix, antiforgery, credentials and cancellation", async () => {
    const calls = [];
    globalThis.fetch = async (url, options) => {
        calls.push({ url: String(url), options });
        return Response.json(calls.length === 1 ? { version: 1, headerName: "RequestVerificationToken", requestToken: "csrf" } : { answer: 42 });
    };
    const abort = new AbortController();
    const signal = abort.signal;
    const client = createClient({ baseUrl: "https://dash.test/my-panel/" });
    assert.deepEqual(await client.request("example.plugin", "read", {}, { signal }), { answer: 42 });
    assert.equal(calls[0].url, "https://dash.test/my-panel/_quasar/sdk/v1/session");
    assert.equal(calls[1].url, "https://dash.test/my-panel/_quasar/sdk/v1/operations/example.plugin/read");
    for (const { options } of calls) {
        assert.equal(options.signal, calls[0].options.signal);
        assert.equal(options.credentials, "same-origin");
        assert.equal(options.redirect, "error");
        assert.equal(options.cache, "no-store");
    }
    assert.equal(calls[1].options.headers.RequestVerificationToken, "csrf");
});

test("rejects a foreign base, raw destination IDs and oversized control payloads", async () => {
    globalThis.fetch = () => { throw new Error("must not call fetch"); };
    assert.throws(() => createClient({ baseUrl: "https://other.test/" }), TypeError);
    const client = createClient({ baseUrl: "https://dash.test/my-panel/" });
    await assert.rejects(client.request("../other", "read"), TypeError);
    await assert.rejects(client.request("example.plugin", "read", { bytes: "x".repeat(65536) }), TypeError);
});

test("reports access denial without replaying an operation", async () => {
    let calls = 0;
    globalThis.fetch = async () => {
        calls++;
        return calls === 1 ? Response.json({ version: 1, headerName: "csrf", requestToken: "token" })
            : Response.json({ detail: "Forbidden" }, { status: 403 });
    };
    await assert.rejects(createClient({ baseUrl: "https://dash.test/my-panel/" }).request("example.plugin", "write"),
        error => error instanceof QuasarSdkError && error.status === 403);
    assert.equal(calls, 2);
});

test("rejects protocol mismatch before sending the operation", async () => {
    let calls = 0;
    globalThis.fetch = async () => { calls++; return Response.json({ version: 2, headerName: "csrf", requestToken: "token" }); };
    await assert.rejects(createClient({ baseUrl: "https://dash.test/my-panel/" }).request("example.plugin", "read"),
        error => error.status === 426);
    assert.equal(calls, 1);
});

test("binary reads remain pull-driven and dispose their owned lease", async () => {
    const calls = [];
    const id = "a".repeat(32);
    let pulls = 0;
    globalThis.fetch = async (url, options) => {
        calls.push({ url: String(url), options });
        if (String(url).endsWith("/session")) return Response.json({ version: 1, headerName: "csrf", requestToken: "token" });
        if (String(url).endsWith("/example.plugin/file")) return Response.json({ id, kind: "read", url: `/my-panel/_quasar/sdk/v1/resources/${id}` });
        if (options.method === "DELETE") return new Response(null, { status: 204 });
        return new Response(new ReadableStream({ pull(controller) { pulls++; controller.enqueue(new Uint8Array(65536)); } }));
    };
    const client = createClient({ baseUrl: "https://dash.test/my-panel/" });
    const file = await client.openRead("example.plugin", "file");
    assert.ok(pulls <= 1);
    const reader = file.body.getReader();
    assert.equal((await reader.read()).value.length, 65536);
    await reader.cancel();
    await file.dispose();
    assert.equal(calls.filter(call => call.options.method === "DELETE").length, 1);
    assert.equal(calls[2].options.signal.aborted, true);
    client.dispose();
});

test("uploads are single-use and carry cancellation and antiforgery", async () => {
    const calls = [];
    const id = "b".repeat(32);
    globalThis.fetch = async (url, options) => {
        calls.push({ url: String(url), options });
        if (String(url).endsWith("/session")) return Response.json({ version: 1, headerName: "csrf", requestToken: "token" });
        if (String(url).endsWith("/example.plugin/upload")) return Response.json({ id, kind: "upload", url: `/my-panel/_quasar/sdk/v1/resources/${id}` });
        return Response.json({ committed: true });
    };
    const client = createClient({ baseUrl: "https://dash.test/my-panel/" });
    const upload = await client.openWrite("example.plugin", "upload");
    const bytes = new Blob([new Uint8Array([1, 2, 3])]);
    assert.deepEqual(await upload.write(bytes), { committed: true });
    assert.equal(calls[2].options.method, "PUT");
    assert.equal(calls[2].options.body, bytes);
    assert.equal(calls[2].options.headers.csrf, "token");
    await assert.rejects(upload.write(bytes), /single-use/);
    client.dispose();
    await assert.rejects(client.request("example.plugin", "read"), /disposed/);
});

test("subscription disposal closes its resource without replay", async () => {
    const id = "c".repeat(32);
    let deleted = 0;
    globalThis.fetch = async (url, options) => {
        if (String(url).endsWith("/session")) return Response.json({ version: 1, headerName: "csrf", requestToken: "token" });
        if (String(url).endsWith("/example.plugin/watch")) return Response.json({ id, kind: "subscription", url: `/my-panel/_quasar/sdk/v1/resources/${id}` });
        if (options.method === "DELETE") { deleted++; return new Response(null, { status: 204 }); }
        return new Response('{"value":1}\n{"value":2}\n', { headers: { "Content-Type": "application/x-ndjson" } });
    };
    const client = createClient({ baseUrl: "https://dash.test/my-panel/" });
    for await (const item of client.subscribe("example.plugin", "watch")) { assert.equal(item.value, 1); break; }
    assert.equal(deleted, 1);
    client.dispose();
});
