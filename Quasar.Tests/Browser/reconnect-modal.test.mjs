import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const script = readFileSync(new URL('../../Quasar/Components/Layout/ReconnectModal.razor.js', import.meta.url), 'utf8');

function harness(reconnect = async () => false, { base = '/', replies = [], workerId = 'old-worker', resumeCircuit = async () => false, connectionState = 'connected', storage = new Map() } = {}) {
    const requests = [], timers = new Map();
    let nextTimer = 0, now = 0;
    const modal = Object.assign(new EventTarget(), {
        open: false,
        showModal() { this.open = true; },
        close() { this.open = false; },
        classList: { replace() {} }
    });
    const window = new EventTarget();
    window.quasarConnectionState = connectionState;
    window.setTimeout = callback => { timers.set(++nextTimer, callback); return nextTimer; };
    window.clearTimeout = id => timers.delete(id);
    const retry = new EventTarget(), resume = new EventTarget();
    const warning = { hidden: true }, message = {}, reload = new EventTarget(), modalReload = new EventTarget();
    const document = Object.assign(new EventTarget(), {
        visibilityState: 'visible',
        baseURI: 'https://quasar.test' + base,
        getElementById: id => id === 'components-reconnect-modal' ? modal
            : id === 'quasar-connection' ? { textContent: JSON.stringify({ workerId }) }
            : id === 'components-reconnect-button' ? retry
            : id === 'quasar-connection-warning' ? warning
            : id === 'quasar-connection-warning-message' ? message
            : id === 'quasar-connection-reload' ? reload
            : id === 'components-reconnect-reload' ? modalReload : resume
    });
    let reloads = 0, reconnects = 0;
    vm.runInNewContext(script, {
        window, document, URL, AbortController, AbortSignal, Date: { now: () => now },
        sessionStorage: {
            getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value),
            removeItem: key => storage.delete(key)
        },
        async fetch(url, options) {
            requests.push({ path: url.pathname, ...options });
            const reply = replies.shift() ?? Response.json({ status: 'not-ready' }, { status: 503 });
            if (reply instanceof Error) throw reply;
            return reply;
        },
        location: { reload() { reloads++; } },
        Blazor: { reconnect() { reconnects++; return reconnect(); }, resumeCircuit }
    });
    return {
        modal, retry, resume, document, requests, timers, warning, message, reload, modalReload,
        connection(state) {
            const event = new Event('quasar-connection-state-changed');
            event.detail = { state };
            window.dispatchEvent(event);
        },
        async tick(elapsed = 1000) {
            now += elapsed;
            assert.ok(timers.size, 'No recovery probe scheduled');
            const [id, callback] = timers.entries().next().value;
            timers.delete(id);
            callback();
            await new Promise(resolve => setImmediate(resolve));
        },
        get reloads() { return reloads; }, get reconnects() { return reconnects; },
        state(state) {
            const event = new Event('components-reconnect-state-changed');
            event.detail = { state };
            modal.dispatchEvent(event);
        },
        restart(active) {
            window.quasarRestartReloadSession = active ? 'restart' : null;
            window.quasarRestartFeedback = active ? {} : null;
            window.dispatchEvent(new Event('quasar-restart-feedback-changed'));
        }
    };
}

test('stalled hydration warns after ten seconds and reload works without a circuit', async () => {
    const h = harness(undefined, { connectionState: 'connecting' });
    assert.equal(h.warning.hidden, true);
    await h.tick();
    assert.equal(h.warning.hidden, false);
    assert.match(h.message.textContent, /Live updates and controls are not available yet/);
    h.reload.dispatchEvent(new Event('click'));
    assert.equal(h.reloads, 1);
    h.connection('connected');
    assert.equal(h.warning.hidden, true);
});

test('startup rejection warns immediately and starts readiness recovery', () => {
    const h = harness(undefined, { connectionState: 'connecting' });
    h.connection('failed');
    assert.equal(h.warning.hidden, false);
    assert.match(h.message.textContent, /Could not establish the live connection/);
    assert.equal(h.timers.size, 0);
    assert.equal(h.requests.length, 1);
});

test('startup result already available when the warning module loads is respected', () => {
    const h = harness(undefined, { connectionState: 'failed' });
    assert.equal(h.warning.hidden, false);
    assert.equal(h.timers.size, 0);
});

test('successful hydration cancels its warning timer', () => {
    const h = harness(undefined, { connectionState: 'connecting' });
    h.connection('connected');
    assert.equal(h.timers.size, 0);
    assert.equal(h.warning.hidden, true);
});

for (const base of ['/', '/jack/']) {
    test(`failed startup automatically reloads a ready authenticated panel at ${base}`, async () => {
        const h = harness(undefined, { base, connectionState: 'failed', replies: [
            Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' }),
            Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' })
        ] });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(h.reloads, 0, 'Startup retries must back off');
        await h.tick(5000);
        assert.equal(h.reloads, 1);
        assert.ok(h.requests.every(request => request.path.startsWith(base + 'api/')));
        assert.equal(h.reconnects, 0, 'A circuit does not exist yet');
        assert.equal(h.timers.size, 0);
    });
}

test('slow ICE setup gets a full minute before recovery reloads the page', async () => {
    const h = harness(undefined, { connectionState: 'connecting', replies: [
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' }),
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' })
    ] });
    await h.tick(10000);
    assert.equal(h.warning.hidden, false);
    assert.match(h.message.textContent, /retry automatically/);
    assert.equal(h.reloads, 0);
    await h.tick(50000);
    assert.equal(h.reloads, 1);
});

test('startup reload backoff survives navigation and clears after successful hydration', async () => {
    const storage = new Map();
    const replies = () => [
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' }),
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' }),
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' })
    ];
    const first = harness(undefined, { connectionState: 'failed', storage, replies: replies() });
    await new Promise(resolve => setImmediate(resolve));
    await first.tick(5000);
    assert.equal(first.reloads, 1);
    const second = harness(undefined, { connectionState: 'failed', storage, replies: replies() });
    await new Promise(resolve => setImmediate(resolve));
    await second.tick(5000);
    assert.equal(second.reloads, 0);
    await second.tick(5000);
    assert.equal(second.reloads, 1);
    const third = harness(undefined, { connectionState: 'connecting', storage });
    third.connection('connected');
    assert.equal(storage.size, 0);
});

test('startup recovery never reloads on an outage, gateway error or expired login', async () => {
    const h = harness(undefined, { connectionState: 'failed', replies: [
        new Error('Offline'), new Response('<html>Gateway error</html>'),
        Response.json({ status: 'ready' }), Response.json({}, { status: 401 })
    ] });
    await new Promise(resolve => setImmediate(resolve));
    await h.tick(60000);
    await h.tick(60000);
    assert.equal(h.reloads, 0);
    assert.equal(h.reconnects, 0);
});

test('hydration completing during startup recovery cancels a late reload', async () => {
    let finish;
    const h = harness(undefined, { connectionState: 'failed', replies: [
        Response.json({ status: 'ready' }), new Promise(resolve => { finish = resolve; })
    ] });
    await new Promise(resolve => setImmediate(resolve));
    h.connection('connected');
    finish(Response.json({ workerId: 'new-worker' }));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.reloads, 0);
    assert.equal(h.timers.size, 0);
});

test('exhausted framework retries continue automatically without concurrent reconnects', async () => {
    let finish;
    const h = harness(() => new Promise(resolve => { finish = resolve; }), { replies: [
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' })
    ] });
    h.state('failed');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.reconnects, 1);
    h.retry.dispatchEvent(new Event('click'));
    assert.equal(h.reconnects, 1);
    finish(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.reloads, 0);
    assert.equal(h.timers.size, 0);
});

test('automatic reconnect retries transient failures with a five-second interval', async () => {
    let attempts = 0;
    const h = harness(async () => {
        if (++attempts === 1) throw new Error('Peer unavailable');
        return true;
    }, { replies: Array.from({ length: 4 }, () => [
        Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' })
    ]).flat() });
    h.state('failed');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.reconnects, 1);
    await h.tick(1000);
    assert.equal(h.reconnects, 1);
    await h.tick(4000);
    assert.equal(h.reconnects, 2);
    assert.equal(h.reloads, 0);
    assert.equal(h.timers.size, 0);
});

test('framework loss replaces the startup warning and offers a client-side reload', () => {
    const h = harness(undefined, { connectionState: 'failed' });
    h.state('failed');
    assert.equal(h.warning.hidden, true);
    assert.equal(h.modal.open, true);
    h.modalReload.dispatchEvent(new Event('click'));
    assert.equal(h.reloads, 1);
});

test('the connection warning cannot be dismissed while the circuit is unavailable', () => {
    const h = harness();
    h.state('show');
    const cancel = new Event('cancel', { cancelable: true });
    h.modal.dispatchEvent(cancel);
    assert.equal(cancel.defaultPrevented, true);
});

test('framework retry countdown keeps worker recovery running', async () => {
    const h = harness(undefined, { replies: [
        Response.json({ status: 'not-ready' }, { status: 503 }),
        Response.json({ status: 'ready' }), Response.json({ workerId: 'new-worker' })
    ] });
    h.state('show');
    await new Promise(resolve => setImmediate(resolve));
    for (let seconds = 5; seconds > 0; seconds--) h.state('retrying');
    assert.equal(h.modal.open, true);
    assert.equal(h.timers.size, 1, 'Countdown updates must not cancel or duplicate worker recovery');
    await h.tick();
    assert.equal(h.reloads, 1);
});

test('active restart closes an existing connection popup and owns rejected-circuit recovery', async () => {
    const h = harness();
    h.state('show');
    assert.equal(h.modal.open, true);
    h.restart(true);
    assert.equal(h.modal.open, false);
    for (const state of ['show', 'failed', 'rejected']) h.state(state);
    h.retry.dispatchEvent(new Event('click'));
    h.resume.dispatchEvent(new Event('click'));
    h.document.dispatchEvent(new Event('visibilitychange'));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.modal.open, false);
    assert.equal(h.reconnects, 0);
    assert.equal(h.reloads, 0);
});

test('failed restart restores normal reconnect UI and retry', async () => {
    const h = harness();
    h.restart(true);
    h.state('failed');
    h.restart(false);
    assert.equal(h.modal.open, true);
    h.retry.dispatchEvent(new Event('click'));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.reconnects, 1);
    assert.equal(h.reloads, 1);
});

test('unexpected connection loss retains normal show, hide and rejection behavior', () => {
    const h = harness();
    h.state('show');
    assert.equal(h.modal.open, true);
    h.state('hide');
    assert.equal(h.modal.open, false);
    h.state('rejected');
    assert.equal(h.reloads, 1);
});

test('a retry already in flight cannot reload over a newly started restart', async () => {
    let finish;
    const h = harness(() => new Promise(resolve => { finish = resolve; }));
    h.retry.dispatchEvent(new Event('click'));
    h.restart(true);
    finish(false);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.reloads, 0);
});

for (const base of ['/', '/jack/']) {
    test(`worker replacement reloads as soon as ready at ${base}`, async () => {
        const h = harness(undefined, { base, replies: [
            Response.json({ status: 'not-ready' }, { status: 503 }),
            Response.json({ status: 'ready' }), Response.json({ workerId: 'old-worker' }),
            Response.json({ status: 'ready' }), Response.json({ workerId: 'new-worker' })
        ] });
        h.state('show');
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(h.requests.length, 1);
        await h.tick();
        assert.equal(h.reloads, 0, 'A transient disconnect must preserve the existing circuit');
        await h.tick();
        assert.equal(h.reloads, 1);
        assert.equal(h.timers.size, 0);
        assert.deepEqual(h.requests.map(request => request.path), [
            base + 'api/ready', base + 'api/ready', base + 'api/version',
            base + 'api/ready', base + 'api/version'
        ]);
        for (const request of h.requests) {
            assert.equal(request.cache, 'no-store');
            assert.equal(request.credentials, 'same-origin');
            assert.equal(request.redirect, 'error');
            assert.ok(request.signal instanceof AbortSignal);
        }
    });
}

test('recovery keeps probing after framework retries exhaust, without reloading on errors', async () => {
    const h = harness(undefined, { replies: [
        new Error('Tunnel unavailable'),
        Response.json({ status: 'ready' }), Response.json({}, { status: 401 }),
        new Response('<html>Gateway error</html>'),
        Response.json({ status: 'ready' }), Response.json({ version: '1.6.0.8' }),
        Response.json({ status: 'ready' }), Response.json({ workerId: 'new-worker' })
    ] });
    h.state('show');
    await new Promise(resolve => setImmediate(resolve));
    h.state('failed');
    for (let index = 0; index < 3; index++) {
        await h.tick();
        assert.equal(h.reloads, 0);
    }
    await h.tick();
    assert.equal(h.reloads, 1);
});

for (const action of ['hide', 'paused', 'restart', 'retry', 'resume']) {
    test(`${action} cancels an in-flight recovery probe`, async () => {
        let finish;
        const h = harness(async () => true, { resumeCircuit: async () => true, replies: [
            Response.json({ status: 'ready' }), new Promise(resolve => { finish = resolve; })
        ] });
        h.state('show');
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(h.requests.length, 2);
        if (action === 'restart') h.restart(true);
        else if (action === 'retry' || action === 'resume') h[action].dispatchEvent(new Event('click'));
        else h.state(action);
        await new Promise(resolve => setImmediate(resolve));
        finish(Response.json({ workerId: 'new-worker' }));
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(h.reloads, 0);
        assert.equal(h.timers.size, 0);
        assert.ok(h.requests.every(request => request.signal.aborted));
    });
}
