import { els, state } from "./state.js";

const entries = [];
let pendingRender = false;

export function log(message, isWarning = false) {
    const line = `${new Date().toLocaleTimeString()} ${isWarning ? "WARN" : "INFO"} ${message}`;
    entries.push(line);
    if (entries.length > 500) entries.shift();
    scheduleLogRender();
}

export function downloadLog() {
    downloadText("quasar-viewer.log", `${entries.join("\n")}\n`);
}

export function exportStatistics() {
    const lines = [
        "Quasar Viewer Statistics",
        `Generated: ${new Date().toISOString()}`,
        `URL: ${window.location.href}`,
        "",
        "Scene",
        ...collectDefinitionListLines(els.sceneSummary),
        "",
        "Statistics",
        ...Object.entries(state.stats || {}).map(([key, value]) => `${key}: ${formatStatValue(value)}`),
    ];
    downloadText("quasar-viewer-statistics.txt", `${lines.join("\n")}\n`);
}

function collectDefinitionListLines(list) {
    if (!list) return [];
    const children = Array.from(list.children);
    const lines = [];
    for (let i = 0; i < children.length - 1; i += 2) {
        lines.push(`${children[i].textContent || ""}: ${children[i + 1].textContent || ""}`);
    }
    return lines;
}

function formatStatValue(value) {
    return typeof value === "number" ? String(value) : String(value ?? "");
}

function downloadText(fileName, content) {
    const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
}

function scheduleLogRender() {
    if (!els.log || pendingRender) return;
    pendingRender = true;
    requestAnimationFrame(() => {
        pendingRender = false;
        if (els.log) els.log.textContent = entries.join("\n");
    });
}
