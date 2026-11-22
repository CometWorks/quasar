import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Quasar/wwwroot/_quasar/sdk/v1/boot.js', import.meta.url), 'utf8');
const script = '(async () => {' + source.replace('import("./dashboard.js")', 'loadDashboard()') + '})()';

function harness(settings, loadDashboard, hasShell = true) {
    const nodes = new Map();
    function node(selector) {
        if (!nodes.has(selector)) nodes.set(selector, { hidden: false, querySelector: node });
        return nodes.get(selector);
    }
    let reloads = 0;
    const done = vm.runInNewContext(script, {
        document: { getElementById: id => id === "quasar-connection" ? { textContent: settings } : hasShell && node(id) },
        loadDashboard, location: { reload() { reloads++; } },
    });
    return { done, node, get reloads() { return reloads; } };
}

test("starts the WASM dashboard using the panel settings", async () => {
    let hosted;
    const h = harness('{"hosted":true}', async () => ({ startDashboard(settings) { hosted = settings.hosted; } }));
    await h.done;
    assert.equal(hosted, true);
});

test("static pages do not start a dashboard runtime", async () => {
    let loads = 0;
    const h = harness("{}", async () => { loads++; }, false);
    await h.done;
    assert.equal(loads, 0);
});

for (const failure of ["module", "runtime", "settings"]) {
    test("offers a working reload after " + failure + " failure", async () => {
        const h = harness(failure === "settings" ? "{" : "{}", async () => {
            if (failure === "module") throw Error();
            return { startDashboard() { throw Error(); } };
        });
        await h.done;
        assert.equal(h.node("#quasar-wasm-loading-label").textContent, "The dashboard could not start.");
        assert.equal(h.node("progress").hidden, true);
        h.node("button").onclick();
        assert.equal(h.reloads, 1);
    });
}
