import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
const playwright = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
for (const name of ["chromium", "firefox"]) test(`RTC3 real SDK data and native download: ${name}`, { timeout: 110000 }, async () => {
    const browser = await playwright[name].launch({ headless: true, ...(name === "chromium" ? { executablePath: process.env.CHROME_PATH,
        args: ["--disable-features=WebRtcHideLocalIpsWithMdns"] } : {}) });
    try {
        const context = await browser.newContext({ acceptDownloads: true, extraHTTPHeaders: { "X-Fixture-Key": process.env.QUASAR_RTC_TEST_KEY } });
        const page = await context.newPage();
        const errors = [], payloadHttp = [];

        page.on("pageerror", error => errors.push(error.message));
        page.on("request", request => { if (/\/(operations|resources)\//.test(request.url())) payloadHttp.push(request.url()); });
        await page.goto(process.env.QUASAR_RTC_TEST_URL);
        const result = await page.evaluate(async () => {
            await fetch(new URL("fixture/login", location.href), { method: "POST" });
            const { createClient } = await import(new URL("_quasar/sdk/v1/client.js", location.href));
            const client = globalThis.sdk = createClient({ baseUrl: location.href });
            const request = (op, payload = {}) => client.request("rtc.fixture", op, payload);
            const read = options => client.openRead("rtc.fixture", "file", {}, options);
            const until = async check => { for (let i = 0; i < 100; i++) { if (await check()) return; await new Promise(resolve => setTimeout(resolve, 50)); } throw new Error("Cleanup deadline"); };
            const large = await request("large");
            if (large.length !== 150000 || !/^x+$/.test(large)) throw new Error("Large JSON truncated");
            const range = await read({ range: "bytes=10-19" });
            if (range.status !== 206 || range.contentRange !== "bytes 10-19/33554432" || [...new Uint8Array(await new Response(range.body).arrayBuffer())].join() !== "10,11,12,13,14,15,16,17,18,19") throw new Error("Range mismatch");
            await range.dispose();
            const before = await request("stats");
            const slow = await read();
            await new Promise(resolve => setTimeout(resolve, 200));
            const after = await request("stats");
            if (after.read - before.read > 512 * 1024) throw new Error("Unread stream buffered too much");
            const started = performance.now();
            const full = await read();
            let length = 0;
            const reader = full.body.getReader();
            while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                for (let i = 0; i < value.length; i++) if (value[i] !== ((length + i) & 255)) throw new Error("Corrupt file");
                length += value.length;
            }
            if (length !== 33554432) throw new Error("File truncated");
            await full.dispose();
            const controlStart = performance.now();
            if ((await request("echo", { ok: true })).ok !== true) throw new Error("Control failed");
            const latency = performance.now() - controlStart;
            if (latency > 5000) throw new Error("Slow transfer starved controls");
            await slow.dispose();
            const upload = await client.openWrite("rtc.fixture", "upload");
            const bytes = Uint8Array.from({ length: 400000 }, (_, i) => i & 255);
            const expected = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map(b => b.toString(16).padStart(2, "0")).join("").toUpperCase();
            const written = await upload.write(bytes);
            if (written.length !== bytes.length || written.hash !== expected) throw new Error("Upload mismatch");
            await upload.dispose();
            const abort = new AbortController();
            const interrupted = await client.openWrite("rtc.fixture", "upload", {}, { signal: abort.signal });
            const partial = new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(100)); } });
            const work = interrupted.write(partial).then(() => { throw new Error("Partial upload committed"); }, error => { if (error.name !== "AbortError") throw error; });
            await new Promise(resolve => setTimeout(resolve, 100)); abort.abort(); await work;
            await until(async () => (await request("stats")).commits === before.commits + 1);
            const subscription = client.subscribe("rtc.fixture", "watch");
            let items = 0;
            for await (const value of subscription) { if (value !== items++) throw new Error("Bad subscription"); if (items === 5) break; }
            await until(async () => (await request("stats")).subscriptionsClosed > before.subscriptionsClosed);
            const oversized = await client.openWrite("rtc.fixture", "upload");
            let rejected = false;
            try { await oversized.write(new Uint8Array(1024 * 1024 + 1)); } catch (error) { rejected = error.status === 413 || error.status === 499; }
            finally { await oversized.dispose(); }
            if (!rejected) throw new Error("Oversized upload accepted");
            const revoked = await read();
            await fetch(new URL("fixture/file-deny", location.href), { method: "POST" });
            const revoking = performance.now();
            let forbidden = false;
            try { await new Response(revoked.body).arrayBuffer(); } catch { forbidden = true; }
            await revoked.dispose();
            if (!forbidden || performance.now() - revoking > 2500) throw new Error("Existing stream survived authorization change");
            try { await read(); throw new Error("Disabled operation accepted"); } catch (error) { if (error.status !== 403) throw error; }
            await fetch(new URL("fixture/file-allow", location.href), { method: "POST" });
            return { length, latency, duration: performance.now() - started, bounded: after.read - before.read };
        });
        const downloadEvent = page.waitForEvent("download");
        const transfer = page.evaluate(() => globalThis.sdk.download("rtc.fixture", "file", {}, { fileName: "direct.bin" })).then(() => null, error => error);
        const download = await downloadEvent;
        assert.equal(download.suggestedFilename(), "direct.bin");
        let path; try { path = await download.path(); } catch (error) { console.error("TRANSFER", await transfer); throw error; }
        assert.equal(await download.failure(), null);
        const transferError = await transfer; if (transferError) throw transferError;
        const bytes = await readFile(path);
        assert.equal(bytes.length, 32 * 1024 * 1024);
        const pattern = Buffer.from(Array.from({ length: 65536 }, (_, i) => i & 255));
        const expected = createHash("sha256"); for (let i = 0; i < 512; i++) expected.update(pattern);
        assert.equal(createHash("sha256").update(bytes).digest("hex"), expected.digest("hex"));
        await page.evaluate(() => globalThis.sdk.dispose());
        assert.deepEqual(payloadHttp, [], "Feature payload must not use HTTP compatibility routes");
        assert.deepEqual(errors, []);
        await page.evaluate(async () => {
            await fetch(new URL("fixture/login?user=bob", location.href), { method: "POST" });
            const { createClient } = await import(new URL("_quasar/sdk/v1/client.js", location.href));
            const viewer = createClient({ baseUrl: location.href });
            try {
                let refused = false;
                try { await viewer.openWrite("rtc.fixture", "upload"); } catch (error) { refused = error.status === 403; }
                if (!refused) throw new Error("Viewer gained upload permission");
            } finally { viewer.dispose(); }
        });
        console.log(JSON.stringify({ browser: name, ...result, downloadBytes: bytes.length, httpPayloadRequests: payloadHttp.length }));
    } finally { await browser.close(); }
});
