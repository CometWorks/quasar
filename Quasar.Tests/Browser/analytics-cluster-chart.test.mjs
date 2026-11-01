import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';

const source = await readFile(new URL('../../Quasar/wwwroot/quasar-charts.js', import.meta.url), 'utf8');

test('cluster nodes share metric charts and retain their colors when series are missing or reordered', async () => {
    const plots = [];
    const payload = { from: 100, to: 130, charts: [
        { metric: 'cpu', axis: {}, x: [100, 115, 130], series: [
            { uniqueName: 'slot-a', y: [150, null, 160] },
            { uniqueName: 'slot-b', y: [50, 55, 60] },
            { uniqueName: 'wa', y: [5, 5, 5] },
        ] },
        { metric: 'simcpu', axis: {}, x: [100, 115, 130], series: [
            { uniqueName: 'slot-b', y: [25, null, 30] },
        ] },
    ] };
    class Plot {
        constructor(options, data) { this.options = options; this.data = data; plots.push(this); }
        setData(data) { this.data = data; }
        setSize() {}
        setScale() {}
        destroy() {}
    }
    const context = vm.createContext({
        window: { addEventListener() {} },
        document: { baseURI: 'https://dash.test/', getElementById: () => ({ clientWidth: 700, getBoundingClientRect: () => ({ width: 700, height: 320 }) }) },
        uPlot: Plot, URLSearchParams, URL, ResizeObserver: class { observe() {} disconnect() {} },
        fetch: async () => ({ ok: true, text: async () => JSON.stringify(payload) }),
    });
    vm.runInContext(source, context);
    const request = {
        endpoint: 'api/analytics/series', from: 100, to: 130, servers: ['slot-a', 'slot-b', 'wa'],
        names: { 'slot-a': 'Survival / Node A', 'slot-b': 'Survival / Node B', wa: 'Survival / World Authority' },
        seriesColors: { 'slot-a': '#123456', 'slot-b': '#abcdef', wa: '#fedcba' },
        metrics: [{ key: 'cpu', containerId: 'cpu', height: 320 }, { key: 'simcpu', containerId: 'simcpu', height: 320 }],
        lines: true,
    };
    await context.window.quasarCharts.sync(request);
    assert.equal(plots.length, 2); // One graph per metric, not one graph per node.
    assert.equal(plots[0].options.series.length, 4); // Timeline plus all three nodes.
    assert.equal(plots[0].options.series[2].label, 'Survival / Node B');
    assert.equal(plots[0].options.series[2].stroke, '#abcdef');
    assert.equal(plots[1].options.series[1].stroke, '#abcdef');
    assert.equal(plots[0].options.series[3].label, 'Survival / World Authority');
    assert.equal(plots[0].options.series[1].spanGaps, false);
    assert.equal(plots[0].data[1][1], null);

    payload.charts[0].series.reverse();
    await context.window.quasarCharts.sync(request);
    const reordered = plots.at(-1).options.series;
    assert.equal(reordered[1].stroke, '#fedcba');
    assert.equal(reordered[3].stroke, '#123456');
});
