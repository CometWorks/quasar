// Client-side analytics charts. The Blazor Server page only sends a small request descriptor over
// the SignalR circuit; this module fetches the actual series data over plain HTTP (so it never
// touches the circuit) and renders it with uPlot on a canvas — far lighter for the browser than the
// previous server-driven SVG charts, especially on Firefox.
window.quasarCharts = (function () {
    const instances = new Map();      // containerId -> { chart, seriesCount, ro, height }
    const SYNC_KEY = "quasar-analytics"; // uPlot auto-subscribes charts that set cursor.sync.key here

    let dotNet = null;            // .NET ref used to report a drag-selected time range back to Blazor
    let suspendUpdates = false;   // true while a selection drag is in progress, so refreshes don't disrupt it

    function init(dotNetRef) {
        dotNet = dotNetRef;
    }

    // A drag can end anywhere — even past a chart's right edge, where that chart's own canvas never
    // receives the mouseup (so selecting through the very end of the chart would otherwise never zoom).
    // Finalize any pending selection across all charts here, then resume refreshes. Deferred so uPlot
    // finishes its own mouseup handling and u.select/__lastSel are final.
    if (typeof window !== "undefined" && !window.__quasarChartsMouseup) {
        window.__quasarChartsMouseup = true;
        window.addEventListener("mouseup", () => {
            setTimeout(() => {
                for (const inst of instances.values()) finalizeSelection(inst.chart);
                suspendUpdates = false;
            }, 0);
        });
    }

    function axisColors(dark) {
        return {
            label: dark ? "rgba(255,255,255,0.70)" : "rgba(0,0,0,0.70)",
            grid: "rgba(128,128,128,0.24)",
            tick: "rgba(128,128,128,0.40)",
        };
    }

    function makeValueFormatter(axis) {
        const decimals = axis && typeof axis.decimals === "number" ? axis.decimals : 0;
        const kilo = !!(axis && axis.kilo);
        return function (v) {
            if (v == null || Number.isNaN(v)) return "";
            if (kilo) return Math.round(v / 1000) + "k";
            return v.toFixed(decimals);
        };
    }

    // Time-only x-axis labels: "HH:MM:SS".
    function formatTick(t) {
        const pad = (n) => (n < 10 ? "0" + n : "" + n);
        const d = new Date(t * 1000);
        const time = pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds());
        return time;
    }

    function buildYRange(axis) {
        // Honor a fixed min and/or max (e.g. SimSpeed 0..1.1) while nicely auto-scaling the open side.
        return function (u, dataMin, dataMax) {
            const lo = dataMin == null ? 0 : dataMin;
            const hi = dataMax == null ? 1 : dataMax;
            const [niceLo, niceHi] = uPlot.rangeNum(lo, hi, 0.1, true);
            const min = axis && axis.min != null ? axis.min : niceLo;
            let max = axis && axis.max != null ? axis.max : niceHi;
            if (max <= min) max = min + 1;
            return [min, max];
        };
    }

    function buildOptions(chart, width, height, dark, colors, spanSeconds, xMin, xMax) {
        const colorsArr = colors && colors.length ? colors : ["#3b82f6", "#7dd3fc", "#a7f3d0", "#fde68a", "#f0abfc", "#c4b5fd"];
        const c = axisColors(dark);
        const valueFmt = makeValueFormatter(chart.axis);

        const series = [{}]; // x
        chart.series.forEach((s, i) => {
            const color = colorsArr[i % colorsArr.length];
            series.push({
                label: s.label,
                stroke: color,
                // Dots only: no connecting line (return null path), just filled markers.
                paths: () => null,
                points: { show: true, size: 5, stroke: color, fill: color, width: 0 },
                value: (u, v) => (v == null ? "-" : valueFmt(v)),
            });
        });

        return {
            width: width,
            height: height,
            ms: 1e3, // x values are unix seconds, not milliseconds
            cursor: {
                sync: { key: SYNC_KEY },
                y: false,
                points: { size: 7 },
                // Drag an x-range to select it: draws a highlight band but does NOT zoom locally
                // (setScale:false). The released selection is reported to Blazor, which repins the page.
                drag: { x: true, y: false, setScale: false },
            },
            legend: { show: true, live: true },
            hooks: {
                // Capture the selection while dragging; uPlot may reset u.select on mouseup before our
                // own listener runs, so we remember the last meaningful drag here.
                setSelect: [
                    (u) => {
                        const s = u.select;
                        if (s && s.width >= 3)
                            u.__lastSel = { left: s.left, width: s.width };
                    },
                ],
            },
            scales: {
                x: { time: true, min: xMin, max: xMax },
                y: { range: buildYRange(chart.axis) },
            },
            axes: [
                {
                    stroke: c.label,
                    grid: { show: false },
                    ticks: { stroke: c.tick, width: 1 },
                    size: 52, // room for the two-line YYYY-MM-DD / HH:MM:SS label
                    values: (u, splits) => splits.map(formatTick),
                },
                {
                    stroke: c.label,
                    grid: { stroke: c.grid, width: 1, dash: [3, 3] },
                    ticks: { stroke: c.tick, width: 1 },
                    size: 60,
                    values: (u, splits) => splits.map(valueFmt),
                },
            ],
            series: series,
        };
    }

    function applyXScale(u, xMin, xMax) {
        if (Number.isFinite(xMin) && Number.isFinite(xMax) && xMax > xMin)
            u.setScale("x", { min: xMin, max: xMax });
    }

    // Height held back from the canvas so uPlot's legend (rendered as a sibling below the plot) sits
    // inside the card instead of crossing its bottom edge. Computed deterministically from the series
    // count — one short row per series — so it never feeds back off the live DOM (measuring the legend
    // right after creation reads a transient tall value and collapses the plot).
    function legendReserveFor(seriesCount, baseHeight) {
        const reserve = 8 + Math.max(1, seriesCount) * 16;
        return Math.min(reserve, Math.round((baseHeight || 280) * 0.4));
    }

    // Height is driven by the panel's intended height (passed from the page), NOT the container's live
    // height: uPlot inserts its own DOM into the container, so re-measuring it would feed back. Only
    // the width is taken from the container, so charts still reflow responsively.
    function plotSize(el, fallbackHeight, legendReserve) {
        const rect = el.getBoundingClientRect();
        const width = Math.max(120, Math.floor(rect.width || el.clientWidth || 320));
        const baseHeight = Math.max(160, Math.floor(fallbackHeight || rect.height || el.clientHeight || 280));
        const height = Math.max(120, baseHeight - (legendReserve || 0));
        return { width, height };
    }

    // Called on mouse release over a chart: if the user dragged a range, report it to Blazor (which
    // repins the page to that absolute window) and clear the highlight. Resumes refreshes either way.
    function finalizeSelection(u) {
        suspendUpdates = false;
        const sel = u.__lastSel;
        if (!sel || sel.width < 3) return;
        u.__lastSel = null;

        const a = u.posToVal(sel.left, "x");
        const b = u.posToVal(sel.left + sel.width, "x");
        const from = Math.round(Math.min(a, b));
        const to = Math.round(Math.max(a, b));
        u.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
        if (to - from >= 1 && dotNet)
            dotNet.invokeMethodAsync("OnChartRangeSelected", from, to);
    }

    function renderChart(containerId, chart, dark, colors, spanSeconds, fallbackHeight, xMin, xMax) {
        const el = document.getElementById(containerId);
        if (!el) return;

        // uPlot wants columnar data: [xs, y0, y1, ...]. Nulls render as gaps (undrawn points).
        const data = [chart.x.map(Number)];
        chart.series.forEach((s) => data.push(s.y.map((v) => (v == null ? null : Number(v)))));

        const reserve = legendReserveFor(chart.series.length, fallbackHeight);
        const existing = instances.get(containerId);

        if (existing && existing.seriesCount === chart.series.length) {
            existing.chart.setData(data);
            existing.chart.setSize(plotSize(el, fallbackHeight, reserve));
            applyXScale(existing.chart, xMin, xMax);
            return;
        }

        if (existing) destroy(containerId);

        const size = plotSize(el, fallbackHeight, reserve);
        const opts = buildOptions(chart, size.width, size.height, dark, colors, spanSeconds, xMin, xMax);
        const chartInstance = new uPlot(opts, data, el);
        applyXScale(chartInstance, xMin, xMax);

        if (chartInstance.over) {
            // Pause real-time refreshes during a drag; the global mouseup handler finalizes the selected
            // range on release (it covers releases past the chart's edge too).
            chartInstance.over.addEventListener("mousedown", () => { chartInstance.__lastSel = null; suspendUpdates = true; });
        }

        let raf = 0;
        const ro = new ResizeObserver(() => {
            if (raf) return;
            raf = requestAnimationFrame(() => {
                raf = 0;
                const inst = instances.get(containerId);
                if (!inst) return;
                inst.chart.setSize(plotSize(el, fallbackHeight, legendReserveFor(inst.seriesCount, fallbackHeight)));
            });
        });
        ro.observe(el);

        instances.set(containerId, { chart: chartInstance, seriesCount: chart.series.length, ro: ro });
    }

    function destroy(containerId) {
        const inst = instances.get(containerId);
        if (!inst) return;
        try { if (inst.ro) inst.ro.disconnect(); } catch (e) { /* ignore */ }
        try { inst.chart.destroy(); } catch (e) { /* ignore */ }
        instances.delete(containerId);
    }

    // request: { endpoint, from, to, maxPoints, servers[], metrics[{key, containerId, height}],
    //            names{uniqueName: displayName}, colors[], dark }
    async function sync(request) {
        // Don't disturb an in-progress drag selection with a real-time refresh.
        if (suspendUpdates) return { ok: true, skipped: true, charts: 0, bytes: 0 };

        const metrics = (request && request.metrics) || [];
        const wantedContainers = new Set(metrics.map((m) => m.containerId));

        // Drop charts whose panel is no longer visible.
        for (const id of Array.from(instances.keys())) {
            if (!wantedContainers.has(id)) destroy(id);
        }

        if (!metrics.length || !request.servers || !request.servers.length) {
            return { ok: true, charts: 0, bytes: 0 };
        }

        const params = new URLSearchParams();
        params.set("from", String(request.from));
        params.set("to", String(request.to));
        params.set("maxPoints", String(request.maxPoints || 1000));
        request.servers.forEach((s) => params.append("servers", s));
        metrics.forEach((m) => params.append("metrics", m.key));

        let text;
        try {
            const resp = await fetch(request.endpoint + "?" + params.toString(), {
                credentials: "same-origin",
                cache: "no-store",
                headers: { "Accept": "application/json" },
            });
            if (!resp.ok) return { ok: false, status: resp.status, charts: 0, bytes: 0 };
            text = await resp.text();
        } catch (e) {
            return { ok: false, status: -1, error: String(e), charts: 0, bytes: 0 };
        }

        let payload;
        try { payload = JSON.parse(text); } catch (e) { return { ok: false, status: -2, charts: 0, bytes: 0 }; }

        const requestFrom = Number(request.from);
        const requestTo = Number(request.to);
        const payloadFrom = Number(payload.from);
        const payloadTo = Number(payload.to);
        const xMin = Number.isFinite(payloadFrom) ? payloadFrom : (Number.isFinite(requestFrom) ? requestFrom : 0);
        const xMaxRaw = Number.isFinite(payloadTo) ? payloadTo : (Number.isFinite(requestTo) ? requestTo : xMin + 1);
        const spanSeconds = Math.max(1, xMaxRaw - xMin);
        const xMax = Number.isFinite(xMaxRaw) && xMaxRaw > xMin ? xMaxRaw : xMin + spanSeconds;
        const containerByMetric = new Map(metrics.map((m) => [m.key, m]));
        const heightByMetric = new Map(metrics.map((m) => [m.key, m.height]));
        const names = request.names || {};
        let rendered = 0;
        const present = new Set();

        for (const chart of payload.charts || []) {
            const panel = containerByMetric.get(chart.metric);
            if (!panel) continue;
            // Attach human-readable series labels from the request's name map (kept server-agnostic).
            chart.series.forEach((s) => { s.label = names[s.uniqueName] || s.uniqueName; });
            renderChart(panel.containerId, chart, !!request.dark, request.colors, spanSeconds, heightByMetric.get(chart.metric), xMin, xMax);
            present.add(panel.containerId);
            rendered++;
        }

        // A requested metric with no data this round: clear any stale chart for it.
        for (const m of metrics) {
            if (!present.has(m.containerId)) destroy(m.containerId);
        }

        return { ok: true, charts: rendered, bytes: text.length };
    }

    function disposeAll() {
        for (const id of Array.from(instances.keys())) destroy(id);
    }

    return { init, sync, dispose: destroy, disposeAll };
})();
