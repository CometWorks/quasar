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
    const signal = new AbortController().signal;
    const client = createClient({ baseUrl: "https://dash.test/my-panel/" });
    assert.deepEqual(await client.request("example.plugin", "read", {}, { signal }), { answer: 42 });
    assert.equal(calls[0].url, "https://dash.test/my-panel/_quasar/sdk/v1/session");
    assert.equal(calls[1].url, "https://dash.test/my-panel/_quasar/sdk/v1/operations/example.plugin/read");
    for (const { options } of calls) {
        assert.equal(options.signal, signal);
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
