import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const script = readFileSync(process.env.QUASAR_PUSH_SCRIPT ?? new URL('../../Quasar/wwwroot/quasar-push.js', import.meta.url), 'utf8');
const origin = 'https://quasar.test';
class Events extends EventTarget {
    listeners = new Set();
    addEventListener(type, handler) { super.addEventListener(type, handler); this.listeners.add(handler); }
    removeEventListener(type, handler) { super.removeEventListener(type, handler); this.listeners.delete(handler); }
}
function worker(state, script = '/push-worker.js') {
    return Object.assign(new Events(), { state, scriptURL: origin + script });
}
function harness(active, installing) {
    const calls = { reads: 0, subscribes: 0, globalReady: 0 };
    const timers = new Map();
    const subscription = { toJSON: () => ({ endpoint: 'https://push.test/subscription', keys: { p256dh: 'key', auth: 'auth' } }) };
    const registration = Object.assign(new Events(), {
        active, installing, waiting: null,
        pushManager: {
            async getSubscription() { calls.reads++; return null; },
            async subscribe(options) {
                assert.equal(registration.active.state, 'activated');
                assert.equal(registration.active.scriptURL, origin + '/push-worker.js');
                assert.deepEqual([...options.applicationServerKey], [1, 2, 3]);
                calls.subscribes++;
                return subscription;
            }
        }
    });
    const serviceWorker = {
        async getRegistration() { return registration; },
        async register(url, options) { assert.equal(url, '/push-worker.js'); assert.equal(options.scope, '/'); return registration; },
        get ready() { calls.globalReady++; throw new Error('Global readiness does not identify Quasar’s registration.'); }
    };
    const context = vm.createContext({ window: { isSecureContext: true, PushManager: {}, Notification: {} },
        Notification: { permission: 'granted' }, navigator: { serviceWorker }, location: { origin }, URL, atob,
        setTimeout(fn) { const id = Symbol(); timers.set(id, fn); return id; }, clearTimeout(id) { timers.delete(id); }
    });
    vm.runInContext(script, context);
    return { api: context.window.quasarPush, registration, serviceWorker, calls, timers };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('uses the returned active Quasar registration without global readiness', async () => {
    const h = harness(worker('activated'));
    assert.equal((await h.api.subscribe('AQID')).endpoint, 'https://push.test/subscription');
    assert.equal(h.calls.globalReady, 0);
    assert.equal(h.calls.subscribes, 1);
});

test('status does not treat an activating Quasar worker as ready', async () => {
    const h = harness(worker('activating'));
    assert.equal((await h.api.status()).subscription, null);
    assert.equal(h.calls.reads, 0);
});

test('status ignores another service worker', async () => {
    const h = harness(worker('activated', '/other-worker.js'));
    assert.equal((await h.api.status()).subscription, null);
    assert.equal(h.calls.reads, 0);
});

test('waits through installation and activation instead of using the previous worker', async () => {
    const next = worker('installing');
    const h = harness(worker('activated', '/other-worker.js'), next);
    const pending = h.api.subscribe('AQID');
    await tick();
    assert.equal(h.calls.subscribes, 0);
    h.registration.installing = null;
    h.registration.active = next;
    next.state = 'activating';
    next.dispatchEvent(new Event('statechange'));
    await tick();
    assert.equal(h.calls.subscribes, 0);
    next.state = 'activated';
    next.dispatchEvent(new Event('statechange'));
    await pending;
    assert.equal(h.calls.subscribes, 1);
    assert.equal(h.timers.size, 0);
    assert.equal(next.listeners.size, 0);
    assert.equal(h.registration.listeners.size, 0);
});

test('reports failed installation without attempting push registration', async () => {
    const next = worker('installing');
    const h = harness(null, next);
    const outcome = assert.rejects(h.api.subscribe('AQID'), /Quasar push service worker installation failed/);
    await tick();
    next.state = 'redundant';
    next.dispatchEvent(new Event('statechange'));
    await outcome;
    assert.equal(h.calls.subscribes, 0);
    assert.equal(h.timers.size, 0);
    assert.equal(next.listeners.size, 0);
});

test('a stuck waiting worker times out and removes listeners', async () => {
    const h = harness(null);
    const waiting = worker('installed');
    h.registration.waiting = waiting;
    const outcome = assert.rejects(h.api.subscribe('AQID'), /Quasar push service worker activation timed out/);
    await tick();
    assert.equal(h.timers.size, 1);
    [...h.timers.values()][0]();
    await outcome;
    assert.equal(h.calls.subscribes, 0);
    assert.equal(h.timers.size, 0);
    assert.equal(waiting.listeners.size, 0);
    assert.equal(h.registration.listeners.size, 0);
});

test('registration errors are distinguished from push-provider errors', async () => {
    const h = harness(null);
    h.serviceWorker.register = async () => { throw new Error('Worker script returned HTTP 404.'); };
    await assert.rejects(h.api.subscribe('AQID'), /Quasar push service worker registration failed: Worker script returned HTTP 404/);
    assert.equal(h.calls.subscribes, 0);
});

test('push-only worker activates updates while tabs are still open', async () => {
    const handlers = new Map();
    let skipped = false;
    vm.runInNewContext(readFileSync(new URL('../../Quasar/wwwroot/push-worker.js', import.meta.url), 'utf8'), {
        self: { addEventListener: (type, handler) => handlers.set(type, handler),
            skipWaiting: async () => { skipped = true; } }
    });
    let completed;
    handlers.get('install')({ waitUntil: task => { completed = task; } });
    await completed;
    assert.equal(skipped, true);
});
