import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';

const source = await readFile(new URL('../../Quasar/wwwroot/quasar-charts.js', import.meta.url), 'utf8');

test('fleet chart reuses analytics data, leaves gaps, follows custom palettes, and clears removed servers', async () => {
    const plots = [];
    const styles = new Map([['primary', '#00c0fd'], ['text-secondary', '#adb1b8'], ['divider', '#3b3e43']]);
    const payload = { from: 100, to: 130, charts: [{ metric: 'simspeed', axis: { min: 0, max: 1 }, x: [100, 115, 130], series: [{ uniqueName: 'alpha', y: [1, null, .9] }] }] };
    class Plot {
        constructor(options, data) { this.options = options; this.data = data; this.destroyed = false; plots.push(this); }
        setData(data) { this.data = data; }
        setSize() {}
        setScale() {}
        destroy() { this.destroyed = true; }
    }
    const context = vm.createContext({
        window: { addEventListener() {} },
        document: { baseURI: 'https://dash.test/my-server-panel/', getElementById: () => ({ clientWidth: 700, clientHeight: 220, getBoundingClientRect: () => ({ width: 700, height: 220 }) }) },
        getComputedStyle: () => ({ getPropertyValue: key => styles.get(key.replace('--quasar-palette-', '')) || '' }),
        uPlot: Plot, URLSearchParams, URL, ResizeObserver: class { observe() {} disconnect() {} },
        fetch: async url => { assert.match(String(url), /metrics=simspeed/); assert.equal(url.pathname, '/my-server-panel/api/analytics/series'); return { ok: true, text: async () => JSON.stringify(payload) }; },
    });
    vm.runInContext(source, context);
    const charts = context.window.quasarCharts;
    const request = { endpoint: '/api/analytics/series', from: 100, to: 130, servers: ['alpha'], names: { alpha: 'Europa' }, metrics: [{ key: 'simspeed', containerId: 'fleet', height: 220 }], lines: true, selectable: false, dark: true };
    const result = await charts.sync(request);
    assert.equal(result.hasData, true);
    assert.equal(plots[0].options.series[1].label, 'Europa');
    assert.equal(plots[0].options.series[1].stroke, '#00c0fd');
    assert.equal(plots[0].options.series[1].spanGaps, false);
    assert.equal(plots[0].data[1][1], null);
    assert.equal(plots[0].options.cursor.drag.x, false);
    await charts.sync(request);
    assert.equal(plots.length, 1);
    styles.set('primary', '#ff8800');
    styles.set('text-secondary', '#123456');
    await charts.sync(request);
    assert.equal(plots[0].destroyed, true);
    assert.equal(plots[1].options.series[1].stroke, '#ff8800');
    assert.equal(plots[1].options.axes[0].stroke, '#123456');
    await charts.sync({ ...request, servers: [] });
    assert.equal(plots[1].destroyed, true);
});
