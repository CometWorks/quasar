import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Quasar/wwwroot/_quasar/sdk/v1/boot.js', import.meta.url), 'utf8');
// Keep module loading controllable so failed and stalled imports are covered too.
const script = '(async () => {' + source.replace('import("./circuit.js")', 'loadCircuit()') + '})()';

function harness(loadCircuit, settings = '{"hosted":true}') {
    const events = [], window = { dispatchEvent(event) { events.push(event.detail.state); } };
    const done = vm.runInNewContext(script, {
        window, loadCircuit,
        document: { getElementById: () => ({ textContent: settings }) },
        CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } }
    });
    return { window, events, done };
}

for (const hosted of [true, false]) {
    test(`startup reports connected only after hydration completes (hosted=${hosted})`, async () => {
        let complete;
        const h = harness(async () => ({ startBlazor(settings) {
            assert.equal(settings.hosted, hosted);
            return new Promise(resolve => { complete = resolve; });
        } }), JSON.stringify({ hosted }));
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(h.events, ['connecting']);
        complete();
        await h.done;
        assert.deepEqual(h.events, ['connecting', 'connected']);
        assert.equal(h.window.quasarConnectionState, 'connected');
    });
}

for (const failure of ['module', 'session', 'settings']) {
    test(`startup reports ${failure} failure to the client-side warning`, async () => {
        const h = harness(async () => {
            if (failure === 'module') throw new TypeError('Module unavailable');
            return { async startBlazor() { throw new Error('Connection unavailable'); } };
        }, failure === 'settings' ? '{broken' : '{"hosted":true}');
        await h.done;
        assert.deepEqual(h.events, ['connecting', 'failed']);
        assert.equal(h.window.quasarConnectionState, 'failed');
    });
}
