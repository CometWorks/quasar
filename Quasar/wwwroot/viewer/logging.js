import { els } from "./state.js";

const entries = [];
let pendingRender = false;

export function log(message, isWarning = false) {
    const line = `${new Date().toLocaleTimeString()} ${isWarning ? "WARN" : "INFO"} ${message}`;
    entries.push(line);
    if (entries.length > 500) entries.shift();
    scheduleLogRender();
}

export function downloadLog() {
    const blob = new Blob([entries.join("\n") + "\n"], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = "quasar-viewer.log";
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
