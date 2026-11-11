import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';

const source = await readFile(new URL('../../Quasar/wwwroot/quasar-charts.js', import.meta.url), 'utf8');

// QSR-24: the hover legend showed "1970-01-01 1:29am" because the options told uPlot the x values
// were milliseconds times 1000. The legend must show the sample's local time with seconds.
test('chart hover shows the sample time as local YYYY-MM-DD HH:MM:SS, day-step ticks show the date', async () => {
    const plots = [];
    class Plot {
        constructor(options, data) { this.options = options; this.data = data; plots.push(this); }
        setData() {}
        setSize() {}
        setScale() {}
        destroy() {}
    }
    const t = 1794146615; // 2026-11-08 14:03:35 UTC
    const payload = { from: t - 60, to: t, charts: [{ metric: 'simspeed', axis: { min: 0, max: 1.1, decimals: 2 }, x: [t - 60, t], series: [{ uniqueName: 'alpha', y: [1, 1] }] }] };
    const context = vm.createContext({
        window: { addEventListener() {}, quasarSdk: { async request() { return payload; } } },
        document: { getElementById: () => ({ clientWidth: 700, clientHeight: 220, getBoundingClientRect: () => ({ width: 700, height: 220 }) }) },
        uPlot: Plot, ResizeObserver: class { observe() {} disconnect() {} },
    });
    vm.runInContext(source, context);
    const request = { from: t - 60, to: t, servers: ['alpha'], names: {}, metrics: [{ key: 'simspeed', containerId: 'c', height: 220 }], lines: true };
    await context.window.quasarCharts.sync(request);

    const options = plots[0].options;
    // uPlot reads x as unix seconds only with its default ms (1e-3); anything else shifts every date.
    assert.ok(options.ms === undefined || options.ms === 1e-3, `ms = ${options.ms}`);

    // The browser's own time zone decides the shown time; Node picks up TZ changes at runtime.
    const savedTz = process.env.TZ;
    try {
        process.env.TZ = 'UTC';
        assert.equal(options.series[0].value(null, t), '2026-11-08 14:03:35');
        process.env.TZ = 'Asia/Kolkata';
        assert.equal(options.series[0].value(null, t), '2026-11-08 19:33:35');
        process.env.TZ = 'America/New_York';
        assert.equal(options.series[0].value(null, t), '2026-11-08 09:03:35');

        // X-axis ticks: the time while they are less than a day apart, the date once they sit on midnights.
        const ticks = options.axes[0].values;
        assert.deepEqual(ticks(null, [t, t + 300], 0, 50, 300), ['09:03:35', '09:08:35']);
        assert.deepEqual(ticks(null, [t, t + 86400], 0, 50, 86400), ['2026-11-08', '2026-11-09']);
    } finally {
        if (savedTz === undefined) delete process.env.TZ; else process.env.TZ = savedTz;
    }
    assert.equal(options.series[0].value(null, null), '-');

    await context.window.quasarCharts.sync({ ...request, timeZone: 'Asia/Tokyo' });
    assert.equal(plots.length, 2);
    assert.equal(plots[1].options.series[0].value(null, t), '2026-11-08 23:03:35');
    assert.deepEqual(plots[1].options.axes[0].values(null, [t, t + 3600], 0, 50, 300), ['23:03:35', '00:03:35']);
    assert.equal(plots[1].data[0][1], t);
});
