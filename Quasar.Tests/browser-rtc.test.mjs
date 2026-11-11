import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";

const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const base = process.env.QUASAR_RTC_TEST_URL;
const mode = process.env.QUASAR_RTC_TEST_MODE;
const key = process.env.QUASAR_RTC_TEST_KEY;

test(`RTC 2 real Quasar cookie/session boundary: ${mode}`, { timeout: 90000 }, async () => {
    const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH, headless: true,
        args: ["--disable-features=WebRtcHideLocalIpsWithMdns"] });
    try {
        const first = await browser.newContext({ extraHTTPHeaders: { "X-Fixture-Key": key } });
        const second = await browser.newContext({ extraHTTPHeaders: { "X-Fixture-Key": key } });
        const page = await first.newPage();
        const other = await second.newPage();
        await Promise.all([page.goto(base), other.goto(base)]);
        const result = await page.evaluate(async ({ mode }) => {
            const root = new URL("_quasar/sdk/v1/", location.href);
            const response = (path, body, method = "POST", headers = {}) => fetch(new URL(path, location.href), {
                method, credentials: "same-origin", cache: "no-store", redirect: "error", headers: { "Content-Type": "application/json", ...headers },
                body: method === "GET" || method === "DELETE" ? undefined : JSON.stringify(body ?? {}),
            });
            const timeout = (promise, milliseconds) => {
                let timer;
                return Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("RTC test deadline")), milliseconds); })])
                    .finally(() => clearTimeout(timer));
            };
            const fixture = path => response("fixture/" + path);
            const countZero = async () => {
                for (let index = 0; index < 100; index++) {
                    const value = await (await response("fixture/count", null, "GET")).json();
                    if (value.count === 0) return;
                    await new Promise(resolve => setTimeout(resolve, 50));
                }
                throw new Error("Revoked peer did not release its slot");
            };
            const anonymous = await fetch(new URL("rtc/attempts", root), { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
            if (anonymous.status !== 401) throw new Error("Anonymous setup was not denied");
            await fixture("login");
            const session = await (await fetch(new URL("session", root))).json();
            const csrf = { [session.headerName]: session.requestToken };
            const pair = async (confirm = true) => {
                const pc = new RTCPeerConnection({ iceServers: [] });
                const control = pc.createDataChannel("quasar.control.v1", { ordered: true, protocol: "quasar-sdk.v1" });
                const bulk = pc.createDataChannel("quasar.bulk.v1", { ordered: true, protocol: "quasar-sdk.v1" });
                const opened = Promise.all([new Promise(resolve => control.onopen = resolve), new Promise(resolve => bulk.onopen = resolve)]);
                await pc.setLocalDescription(await pc.createOffer());
                if (pc.iceGatheringState !== "complete") await timeout(new Promise(resolve => pc.onicegatheringstatechange = () => {
                    if (pc.iceGatheringState === "complete") resolve();
                }), 10000);
                const nonce = [...crypto.getRandomValues(new Uint8Array(32))].map(value => value.toString(16).padStart(2, "0")).join("");
                const offer = { version: 1, nonce, description: pc.localDescription.toJSON() };
                const created = await response("_quasar/sdk/v1/rtc/attempts", offer, "POST", csrf);
                if (created.status !== 200) throw new Error(`Setup failed with HTTP ${created.status}`);
                const setup = await created.json();
                if (setup.version !== 1 || setup.grant.length !== 64 || setup.id.length !== 32) throw new Error("Invalid setup contract");
                const reused = await response("_quasar/sdk/v1/rtc/attempts", offer, "POST", csrf);
                if (reused.status !== 409) throw new Error("Setup nonce replay succeeded");
                await pc.setRemoteDescription(setup.description);
                await timeout(opened, 15000);
                if (!confirm) return { pc, id: setup.id, grant: setup.grant };
                const confirmed = await response(`_quasar/sdk/v1/rtc/attempts/${setup.id}/confirm`, { grant: setup.grant }, "POST", csrf);
                if (confirmed.status !== 204) throw new Error(`Confirmation failed with HTTP ${confirmed.status}`);
                const replayed = await response(`_quasar/sdk/v1/rtc/attempts/${setup.id}/confirm`, { grant: setup.grant }, "POST", csrf);
                if (replayed.status !== 409) throw new Error("Setup grant replay succeeded");
                const status = await (await response(`_quasar/sdk/v1/rtc/attempts/${setup.id}`, null, "GET")).json();
                if (!status.active) throw new Error("Peer was not activated");
                return { pc, id: setup.id, control, bulk };
            };
            if (mode === "hosted") {
                const abandoned = await pair(false);
                await fixture("advance");
                await countZero();
                const expired = await response(`_quasar/sdk/v1/rtc/attempts/${abandoned.id}/confirm`, { grant: abandoned.grant }, "POST", csrf);
                if (expired.status !== 404) throw new Error("Expired setup grant was accepted");
                abandoned.pc.close();
            }
            const connected = await pair();
            for (const update of [{ candidates: Array(65).fill({ candidate: "candidate:x" }) },
                { candidates: [{ candidate: "x".repeat(513) }] }, { candidates: [], offset: -1 }, { candidates: [null] }]) {
                const invalid = await response(`_quasar/sdk/v1/rtc/attempts/${connected.id}/ice`, update, "POST", csrf);
                if (invalid.status !== 400) throw new Error("Unbounded ICE update accepted");
            }
            // Keep only the public attempt ID for the second, same-user browser.
            globalThis.testPeerId = connected.id;
            if (mode === "hosted") {
                await fixture("outage");
                await fixture("advance");
                await countZero();
                connected.pc.close();
                await fixture("resume");
                const replacement = await pair();
                await fixture("replace");
                await fixture("tick");
                await countZero();
                replacement.pc.close();
                return { anonymous: true, replayDenied: true, managerOutageExpired: true, installationReplacementClosed: true };
            }
            await fixture("deny");
            await countZero();
            connected.pc.close();
            await fixture("allow");
            const view = await pair();
            view.pc.close();
            await countZero();
            for (const invalid of ["duplicate", "credit", "depth"]) {
                const malformed = await pair();
                if (invalid === "duplicate") {
                    const frame = JSON.stringify({ v: 1, type: "request", id: 1, plugin: "rtc.fixture", operation: "echo", payload: {} });
                    malformed.control.send(frame); malformed.control.send(frame);
                } else if (invalid === "credit") malformed.control.send(JSON.stringify({ v: 1, type: "credit", id: 99, bytes: 1 }));
                else malformed.control.send('{"v":1,"type":"request","id":1,"plugin":"rtc.fixture","operation":"echo","payload":' + '['.repeat(40) + '0' + ']'.repeat(40) + '}');
                await countZero(); malformed.pc.close();
            }
            const logout = await pair();
            globalThis.testPeerId = logout.id;
            // Leave this connection alive until the second browser's ownership check.
            globalThis.testPeer = logout.pc;
            globalThis.testCsrf = csrf;
            return { anonymous: true, replayDenied: true, permissionChangeClosed: true, id: logout.id };
        }, { mode });
        assert.ok(result.anonymous && result.replayDenied);
        if (mode === "hosted") {
            assert.ok(result.managerOutageExpired && result.installationReplacementClosed);
        } else {
            assert.ok(result.permissionChangeClosed);
            const otherStatus = await other.evaluate(async id => {
                await fetch(new URL("fixture/login", location.href), { method: "POST" });
                await fetch(new URL("_quasar/sdk/v1/session", location.href));
                const root = new URL("_quasar/sdk/v1/", location.href);
                const session = await (await fetch(new URL("session", root))).json();
                const ice = await fetch(new URL(`rtc/attempts/${id}/ice`, root), { method: "POST",
                    headers: { "Content-Type": "application/json", [session.headerName]: session.requestToken }, body: JSON.stringify({ candidates: [] }) });
                if (ice.status !== 404) throw new Error("Another browser session exchanged peer candidates");
                return (await fetch(new URL(`rtc/attempts/${id}`, root))).status;
            }, result.id);
            assert.equal(otherStatus, 404, "A separate browser session for the same user must not substitute its identity");
            await page.evaluate(async () => {
                const response = await fetch(new URL("fixture/logout", location.href), { method: "POST", headers: globalThis.testCsrf });
                if (response.status !== 204) throw new Error("Logout failed");
                for (let index = 0; index < 100; index++) {
                    const result = await (await fetch(new URL("fixture/count", location.href))).json();
                    if (result.count === 0) { globalThis.testPeer.close(); return; }
                    await new Promise(resolve => setTimeout(resolve, 50));
                }
                throw new Error("Logout did not close the direct session");
            });
        }
    } finally { await browser.close(); }
});
