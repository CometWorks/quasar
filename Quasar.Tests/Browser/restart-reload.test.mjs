import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const script = readFileSync(new URL('../../Quasar/wwwroot/quasar-configs.js', import.meta.url), 'utf8');
const response = (payload, ok = true) => ({ ok, async json() { return payload; } });

function harness(replies, base = '/') {
    const requests = [], timers = [], storage = new Map();
    const window = {
        location: { href: '' },
        dispatchEvent() {},
        sessionStorage: {
            getItem: key => storage.get(key),
            setItem: (key, value) => storage.set(key, value),
            removeItem: key => storage.delete(key)
        },
        setTimeout(callback) { timers.push(callback); }
    };
    vm.runInNewContext(script, {
        window, document: { baseURI: 'https://quasar.test' + base }, URL, Event,
        async fetch(url, options) {
            requests.push({ path: url.pathname, ...options });
            assert.ok(replies.length, 'Unexpected request: ' + url);
            const reply = replies.shift();
            if (reply instanceof Error) throw reply;
            return reply;
        }
    });
    // Finish the page-load resume callback before starting a new update session.
    timers.shift()();
    return {
        window, requests, timers, storage,
        async tick() {
            assert.ok(timers.length, 'No timer scheduled');
            timers.shift()();
            await new Promise(resolve => setImmediate(resolve));
        },
        start(options = {}) {
            window.quasarConfigs.reloadWhenHealthy('/settings/updates', {
                expectedVersion: '1.3.2.123', initialDelayMs: 0, ...options
            });
        }
    };
}

test('restart feedback shows completed stages in an accessible progress bar', () => {
    const element = tag => ({
        tag, children: [], attributes: {}, classList: { toggle() {} },
        appendChild(child) { this.children.push(child); },
        replaceChildren() { this.children = []; },
        setAttribute(key, value) { this.attributes[key] = value; }
    });
    const document = { body: { appendChild() {}, contains: () => true }, createElement: element };
    const events = [];
    const window = { setTimeout() {}, dispatchEvent(event) { events.push(event.type); } };
    vm.runInNewContext(script, { window, document, URL, Event });
    window.quasarConfigs.showRestartFeedback();
    const state = window.quasarRestartFeedback;
    assert.equal(state.progress.tag, 'progress');
    assert.equal(state.progress.attributes['aria-label'], 'Restart steps completed');
    assert.equal(state.progress.max, 4);
    assert.equal(state.progress.value, 0);
    assert.deepEqual(events, ['quasar-restart-feedback-changed']);
    window.quasarConfigs.updateRestartFeedback('Checking worker health', 'health');
    assert.equal(state.progress.value, 2);
    window.quasarConfigs.updateRestartFeedback('Reloading', 'reload');
    assert.equal(state.progress.value, 3);
});

for (const base of ['/', '/my-panel/']) {
    test(`update waits for the target version with status-only health at ${base}`, async () => {
        const h = harness([
            response({ status: 'ok' }), response({ version: '1.3.2.122' }),
            response({ status: 'ok' }), response({ version: '1.3.2.123' })
        ], base);
        h.start();
        await h.tick();
        assert.equal(h.window.location.href, '');
        await h.tick();
        assert.equal(h.window.location.href, '');
        await h.tick();
        assert.equal(h.window.location.href, 'https://quasar.test' + base + 'settings/updates');
        assert.deepEqual(h.requests.map(request => request.path),
            [base + 'api/health', base + 'api/version', base + 'api/health', base + 'api/version']);
        assert.ok(h.requests.every(request => request.cache === 'no-store'));
        assert.equal(h.requests[1].redirect, 'error');
        assert.equal(h.storage.size, 0);
    });
}

for (const payload of [{ version: '1.3.2.123' }, { Version: '1.3.2.123' }]) {
    test(`rollback accepts legacy health payload ${JSON.stringify(payload)}`, async () => {
        const h = harness([response({ status: 'ok', ...payload })]);
        h.start();
        await h.tick();
        await h.tick();
        assert.ok(h.window.location.href.endsWith('/settings/updates'));
        assert.equal(h.requests.length, 1);
    });
}

test('restart without a target version needs only health after the outage', async () => {
    const h = harness([new Error('Worker stopped'), response({ status: 'ok' })]);
    h.start({ expectedVersion: '', requireUnhealthy: true });
    await h.tick();
    await h.tick();
    await h.tick();
    assert.ok(h.window.location.href.endsWith('/settings/updates'));
    assert.ok(h.requests.every(request => request.path === '/api/health'));
});

test('denied or missing version responses keep polling', async () => {
    const h = harness([
        response({ status: 'ok' }), response(null, false),
        response({ status: 'ok' }), response({})
    ]);
    h.start();
    await h.tick();
    await h.tick();
    assert.equal(h.window.location.href, '');
    assert.equal(h.timers.length, 1);
});

test('a superseded poll cannot reload after its version request completes', async () => {
    let resolveVersion;
    const version = new Promise(resolve => { resolveVersion = resolve; });
    const h = harness([response({ status: 'ok' }), version]);
    h.start();
    await h.tick();
    h.window.quasarConfigs.clearRestartFeedback();
    resolveVersion(response({ version: '1.3.2.123' }));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.window.location.href, '');
    assert.equal(h.timers.length, 0);
});
