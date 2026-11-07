import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { createHash } from "node:crypto";
const playwright = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
for (const name of (process.env.RTC_BROWSER ? [process.env.RTC_BROWSER] : ["chromium", "firefox"])) test(`real Blazor render/interop/upload over RTC: ${name}`, { timeout: 140000 }, async () => {
    const browser = await playwright[name].launch({ headless: true, ...(name === "chromium" ? { executablePath: process.env.CHROME_PATH, args: ["--disable-features=WebRtcHideLocalIpsWithMdns"] } : { firefoxUserPrefs: { "media.peerconnection.ice.loopback": true, "media.peerconnection.ice.obfuscate_host_addresses": false } }) });
    try {
        const page = await browser.newPage(), sockets = [], errors = [], peers = [];
        page.on("response", async response => { if (response.url().endsWith("/rtc/attempts") && response.ok()) peers.push((await response.json()).id); });
        page.on("websocket", socket => sockets.push(socket.url()));
        page.on("response", response => { if (response.status() >= 400) console.log("HTTP FAILURE", new URL(response.url()).pathname, response.status()); });
        page.on("pageerror", error => errors.push(error.message));
        page.on("console", message => { if (message.type() === "error") console.log("BROWSER", name, message.text()); });
        await page.goto(new URL("fixture/login", process.env.QUASAR_CIRCUIT_URL).href);
        await page.waitForFunction(() => document.body.dataset.failure || document.body.dataset.interactive === "true");
        if (await page.getAttribute("body", "data-failure")) throw new Error(await page.getAttribute("body", "data-failure"));
        await page.click("#increment"); await page.waitForFunction(() => document.querySelector("#interop")?.textContent === "2");
        assert.equal(await page.textContent("#count"), "1");
        await page.click("#large"); await page.waitForFunction(() => document.querySelector("#render")?.textContent.length === 200000);
        const bytes = Buffer.from(Array.from({ length: 250000 }, (_, i) => i & 255));
        await page.setInputFiles("#upload", { name: "proof.bin", mimeType: "application/octet-stream", buffer: bytes });
        const hash = createHash("sha256").update(bytes).digest("hex").toUpperCase();
        await page.waitForFunction(value => document.querySelector("#hash")?.textContent === value, hash);
        await page.evaluate(async id => {
            const root = new URL("_quasar/sdk/v1/", document.baseURI);
            const tokens = await (await fetch(new URL("session", root))).json();
            const closed = await fetch(new URL("rtc/attempts/" + id, root), { method: "DELETE", headers: { [tokens.headerName]: tokens.requestToken } });
            if (closed.status !== 204) throw new Error("Could not simulate peer loss");
        }, peers[0]);
        await page.waitForFunction(() => document.querySelector("#components-reconnect-modal").classList.contains("components-reconnect-show"));
        assert.equal(await page.locator('#components-reconnect-modal').evaluate(element => element.open), true);
        assert.match(await page.textContent('#components-reconnect-description'), /Displayed data may be out of date/);
        await page.locator('#components-reconnect-reload').waitFor({ state: 'visible' });
        await page.waitForFunction(() => document.querySelector("#components-reconnect-modal").classList.contains("components-reconnect-hide"));
        await page.click("#increment");
        await page.waitForFunction(() => document.querySelector("#interop").textContent === "4");
        assert.equal(await page.textContent("#count"), "2", "Framework circuit reconnect retains its state without replaying clicks");
        assert.ok(peers.length >= 2);
        const negotiation = await page.evaluate(async () => {
            const root = new URL("_quasar/sdk/v1/", document.baseURI), endpoint = new URL("circuit/negotiate?negotiateVersion=1", root);
            const tokens = await (await fetch(new URL("session", root))).json();
            if ((await fetch(endpoint, { method: "POST" })).status !== 400) throw new Error("Circuit negotiation bypassed CSRF");
            const response = await fetch(endpoint, { method: "POST", headers: { [tokens.headerName]: tokens.requestToken } });
            if (!response.ok) throw new Error("Framework negotiation failed");
            return { ticket: (await response.json()).connectionToken, headerName: tokens.headerName, requestToken: tokens.requestToken };
        });
        if (process.env.NEBULA_RTC_TEST_MANAGER) {
            const status = await page.evaluate(async id => {
                return await (await fetch(new URL("_quasar/sdk/v1/rtc/attempts/" + id, document.baseURI))).json();
            }, peers.at(-1));
            assert.equal(status.path, "relayed", "A forced fallback must identify its actual selected TURN path");
            const relay = await (await fetch(new URL("fixture/status", process.env.NEBULA_RTC_TEST_MANAGER))).json();
            assert.ok(relay.payloadBytes >= bytes.length + 200000, "Real native TURN counters include the upload and render payload");
            console.log(JSON.stringify({ browser: name, relay: process.env.NEBULA_RTC_TEST_PROTOCOL, payloadBytes: relay.payloadBytes }));
        }
        const ticket = negotiation.ticket;
        const foreignOrigin = await page.request.post(new URL("_quasar/sdk/v1/circuit/negotiate?negotiateVersion=1", process.env.QUASAR_CIRCUIT_URL).href,
            { headers: { Origin: "https://outside.invalid", [negotiation.headerName]: negotiation.requestToken } });
        assert.equal(foreignOrigin.status(), 403, "A valid CSRF token cannot bypass the circuit Origin boundary");
        const other = await browser.newPage(); await other.goto(new URL("fixture/login", process.env.QUASAR_CIRCUIT_URL).href);
        await other.waitForFunction(() => document.body.dataset.interactive === "true");
        await other.evaluate(async ticket => {
            const root = new URL("_quasar/sdk/v1/", document.baseURI), tokens = await (await fetch(new URL("session", root))).json();
            const { connect } = await import(new URL("rtc.js", root)); const lifetime = new AbortController();
            const peer = await connect(root, tokens, lifetime.signal);
            try { await peer.circuit(ticket); throw new Error("Separate browser substituted a circuit binding"); }
            catch (error) { if (error.status !== 404) throw error; }
            finally { lifetime.abort(); peer.close(); }
        }, ticket);
        await other.close();
        await page.evaluate(async ticket => {
            const root = new URL("_quasar/sdk/v1/", document.baseURI), tokens = await (await fetch(new URL("session", root))).json();
            const { connect } = await import(new URL("rtc.js", root));
            for (const replay of [false, true]) {
                const lifetime = new AbortController(), peer = await connect(root, tokens, lifetime.signal);
                try {
                    const circuit = await peer.circuit(ticket);
                    if (replay) throw new Error("Circuit negotiation token replayed");
                    circuit.close();
                } catch (error) { if (!replay || error.status !== 404) throw error; }
                finally { lifetime.abort(); peer.close(); }
            }
        }, ticket);
        assert.equal(await page.locator("#admin-permission").count(), 1);
        await page.evaluate(async () => { if (!(await fetch(new URL("fixture/viewer", document.baseURI), { method: "POST" })).ok) throw new Error("Role change failed"); });
        await page.waitForFunction(() => !document.querySelector("#admin-permission"));
        assert.deepEqual(sockets, [], "No browser WebSocket may carry the Blazor circuit");
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ browser: name, renderCharacters: 200000, uploadBytes: bytes.length, webSockets: sockets.length, identity: "core authenticated", jsInterop: true, reconnected: true, roleRevoked: true }));
    } finally { await browser.close(); }
});

test('startup failures and stalled hydration show a warning without a working circuit', { timeout: 90000 }, async () => {
    const browser = await playwright.chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH,
        args: ['--disable-features=WebRtcHideLocalIpsWithMdns'] });
    try {
        for (const failure of ['module', 'session', 'stall']) {
            const page = await browser.newPage();
            const path = failure === 'module' ? '**/_quasar/sdk/v1/circuit.js' : '**/_quasar/sdk/v1/session';
            let release;
            const pending = new Promise(resolve => { release = resolve; });
            await page.route(path, async route => {
                if (failure === 'stall') { await pending; await route.continue(); }
                else await route.fulfill({ status: 503, body: 'Unavailable' });
            });
            await page.goto(new URL('fixture/login', process.env.QUASAR_CIRCUIT_URL).href);
            const warning = page.locator('#quasar-connection-warning');
            await warning.waitFor({ state: 'visible', timeout: 15000 });
            assert.equal(await warning.evaluate(element => getComputedStyle(element).position), 'fixed');
            assert.match(await warning.textContent(), failure === 'stall' ? /taking longer than expected/ : /Could not establish/);
            assert.notEqual(await page.getAttribute('body', 'data-interactive'), 'true');
            if (failure === 'stall') release();
            else {
                await page.unroute(path);
                await page.click('#quasar-connection-reload');
            }
            await page.waitForFunction(() => document.body.dataset.interactive === 'true');
            await warning.waitFor({ state: 'hidden' });
            await page.close();
        }
    } finally { await browser.close(); }
});

if (process.env.NEBULA_RTC_TEST_MANAGER) test("real hosting suspension closes TURN circuits without HTTP fallback", { timeout: 60000 }, async () => {
    const browser = await playwright.chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH,
        args: ["--disable-features=WebRtcHideLocalIpsWithMdns"] });
    try {
        const page = await browser.newPage(), peers = [], sockets = [];
        page.on("response", async response => { if (response.url().endsWith("/rtc/attempts") && response.ok()) peers.push((await response.json()).id); });
        page.on("websocket", socket => sockets.push(socket.url()));
        await page.goto(new URL("fixture/login", process.env.QUASAR_CIRCUIT_URL).href);
        await page.waitForFunction(() => document.body.dataset.interactive === "true");
        await page.click("#increment"); await page.waitForFunction(() => document.querySelector("#count")?.textContent === "1");
        const suspended = await fetch(new URL("fixture/suspend", process.env.NEBULA_RTC_TEST_MANAGER), { method: "POST" });
        assert.equal(suspended.status, 204);
        await page.waitForFunction(async id => (await fetch(new URL("_quasar/sdk/v1/rtc/attempts/" + id, document.baseURI))).status === 404,
            peers[0], { timeout: 40000, polling: 250 });
        assert.equal(await page.textContent("#count"), "1", "Disconnect never replays a committed click");
        assert.deepEqual(sockets, [], "Suspension cannot reopen an HTTP/WebSocket payload transport");
        const deadline = AbortSignal.timeout(40000);
        let native;
        do {
            native = await (await fetch(new URL("fixture/status", process.env.NEBULA_RTC_TEST_MANAGER), { signal: deadline })).json();
            if (native.allocations === 0) break;
            deadline.throwIfAborted();
            await new Promise(resolve => setTimeout(resolve, 250));
        } while (true);
        assert.equal(native.allocations, 0, "Revocation releases real TURN sockets");
        console.log(JSON.stringify({ suspended: true, relay: process.env.NEBULA_RTC_TEST_PROTOCOL, allocations: native.allocations, replayedClicks: 0 }));
    } finally { await browser.close(); }
});
