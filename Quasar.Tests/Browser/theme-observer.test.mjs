import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
const source = await readFile(new URL('../../Quasar/wwwroot/quasar-theme.js', import.meta.url), 'utf8');
const { observeSystemTheme } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('OS mode changes notify every live layout, and disposal stops callbacks', async () => {
    const listeners = new Set();
    globalThis.window = { matchMedia(query) {
        assert.equal(query, '(prefers-color-scheme: dark)');
        return { matches: true, addEventListener(_, callback) { listeners.add(callback); }, removeEventListener(_, callback) { listeners.delete(callback); } };
    } };
    const values = [];
    const subscription = observeSystemTheme({ invokeMethodAsync(method, dark) {
        assert.equal(method, 'OnSystemDarkModeChangedAsync');
        values.push(dark);
        return Promise.resolve();
    } });
    const disconnected = observeSystemTheme({ invokeMethodAsync() { return Promise.reject(new Error('Circuit disconnected')); } });
    for (const listener of listeners) listener({ matches: false });
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(values, [true, false]);
    subscription.dispose();
    for (const listener of listeners) listener({ matches: true });
    assert.deepEqual(values, [true, false]);
    disconnected.dispose();
    assert.equal(listeners.size, 0);
    delete globalThis.window;
});
