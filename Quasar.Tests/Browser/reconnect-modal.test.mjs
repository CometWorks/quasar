import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const script = readFileSync(new URL('../../Quasar/Components/Layout/ReconnectModal.razor.js', import.meta.url), 'utf8');

function harness(reconnect = async () => false) {
    const modal = Object.assign(new EventTarget(), {
        open: false,
        showModal() { this.open = true; },
        close() { this.open = false; },
        classList: { replace() {} }
    });
    const window = new EventTarget();
    const retry = new EventTarget(), resume = new EventTarget();
    const document = Object.assign(new EventTarget(), {
        visibilityState: 'visible',
        getElementById: id => id === 'components-reconnect-modal' ? modal
            : id === 'components-reconnect-button' ? retry : resume
    });
    let reloads = 0, reconnects = 0;
    vm.runInNewContext(script, {
        window, document,
        location: { reload() { reloads++; } },
        Blazor: { reconnect() { reconnects++; return reconnect(); }, resumeCircuit: async () => false }
    });
    return {
        modal, retry, resume, document,
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
