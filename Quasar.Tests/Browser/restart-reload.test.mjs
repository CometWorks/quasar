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
        sessionStorage: {
            getItem: key => storage.get(key),
            setItem: (key, value) => storage.set(key, value),
            removeItem: key => storage.delete(key)
        },
        setTimeout(callback) { timers.push(callback); }
    };
    vm.runInNewContext(script, {
        window, document: { baseURI: 'https://quasar.test' + base }, URL,
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
