// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
reconnectModal.addEventListener("cancel", event => event.preventDefault());
const connectionWarning = document.getElementById("quasar-connection-warning");
const connectionMessage = document.getElementById("quasar-connection-warning-message");
let reconnectState = "hide", workerRecovery, retryInFlight = false;
let startupPending = true, startupRetryAt = Date.now() + 60000;
const workerId = JSON.parse(document.getElementById("quasar-connection")?.textContent || "{}").workerId;
const startupRetryKey = "quasar-startup-retries:" + document.baseURI;
let startupRetries = 0;
try { startupRetries = Math.max(0, Math.min(4, Number(sessionStorage.getItem(startupRetryKey)) || 0)); } catch { }
const restartOwnsRecovery = () => !!(window.quasarRestartReloadSession && window.quasarRestartFeedback);
const reloadPage = () => location.reload();
document.getElementById("quasar-connection-reload").addEventListener("click", reloadPage);
document.getElementById("components-reconnect-reload").addEventListener("click", reloadPage);
const startupTimer = window.setTimeout(() => {
    showConnectionWarning("Connecting is taking longer than expected. Live updates and controls are not available yet. Quasar will retry automatically.");
    startWorkerRecovery();
}, 10000);
function showConnectionWarning(message) {
    connectionMessage.textContent = message;
    connectionWarning.hidden = false;
}
function handleConnectionState(state) {
    if (state === "connected" || state === "failed") window.clearTimeout(startupTimer);
    if (state === "connected") {
        startupPending = false;
        connectionWarning.hidden = true;
        stopWorkerRecovery();
        try { sessionStorage.removeItem(startupRetryKey); } catch { }
    } else if (state === "failed" && startupPending) {
        startupRetryAt = Date.now() + Math.min(60000, 5000 * 2 ** startupRetries);
        showConnectionWarning("Could not establish the live connection to Quasar. Live updates and controls are unavailable. Quasar will retry automatically when the server is ready.");
        startWorkerRecovery();
    }
}
window.addEventListener("quasar-connection-state-changed", event => handleConnectionState(event.detail.state));
handleConnectionState(window.quasarConnectionState);
window.addEventListener("quasar-restart-feedback-changed", () => {
    if (restartOwnsRecovery()) {
        stopWorkerRecovery();
        reconnectModal.close();
        document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else {
        handleReconnectStateChanged({ detail: { state: reconnectState } });
        if (reconnectState !== "hide" && reconnectState !== "rejected") reconnectModal.showModal();
    }
});

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

function handleReconnectStateChanged(event) {
    // Once the framework has a circuit, its reconnect UI owns connection warnings.
    startupPending = false;
    window.clearTimeout(startupTimer);
    connectionWarning.hidden = true;
    reconnectState = event.detail.state;
    if (restartOwnsRecovery()) {
        stopWorkerRecovery();
        reconnectModal.close();
        return;
    }
    if (["show", "retrying", "failed"].includes(reconnectState)) startWorkerRecovery();
    else stopWorkerRecovery();
    if (["show", "retrying", "failed", "paused", "resume-failed"].includes(reconnectState)) {
        reconnectModal.showModal();
    }
    if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

function stopWorkerRecovery() {
    if (!workerRecovery) return;
    window.clearTimeout(workerRecovery.timer);
    workerRecovery.abort.abort();
    workerRecovery = null;
}

function startWorkerRecovery() {
    if (!workerId || workerRecovery || restartOwnsRecovery()) return;
    const recovery = { abort: new AbortController(), nextRetryAt: 0 };
    workerRecovery = recovery;
    // Probe independently of SignalR/RTC so startup can recover and a replaced
    // worker does not leave the browser trying to restore an obsolete circuit.
    const check = async () => {
        try {
            const options = { cache: "no-store", credentials: "same-origin", redirect: "error",
                signal: AbortSignal.any([recovery.abort.signal, AbortSignal.timeout(5000)]) };
            const ready = await fetch(new URL("api/ready", document.baseURI), options);
            if (!ready.ok || (await ready.json()).status !== "ready") return;
            if (workerRecovery !== recovery) return;
            const response = await fetch(new URL("api/version", document.baseURI), options);
            if (!response.ok) return;
            const current = await response.json();
            if (workerRecovery !== recovery || restartOwnsRecovery()
                || typeof current.workerId !== "string" || !current.workerId) return;
            // Blazor.start can only run once. Before hydration, recovery needs a
            // fresh document, with backoff retained across unsuccessful reloads.
            if (current.workerId !== workerId || startupPending && Date.now() >= startupRetryAt) {
                if (startupPending) {
                    try { sessionStorage.setItem(startupRetryKey, String(startupRetries + 1)); } catch { }
                }
                stopWorkerRecovery();
                location.reload();
            } else if (!startupPending && reconnectState === "failed" && Date.now() >= recovery.nextRetryAt) {
                recovery.nextRetryAt = Date.now() + 5000;
                await retry();
            }
        } catch {
            // Outages, sign-in redirects and gateway error pages are not readiness.
        } finally {
            if (workerRecovery === recovery) recovery.timer = window.setTimeout(check, 1000);
        }
    };
    void check();
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    if (restartOwnsRecovery() || retryInFlight) return;
    retryInFlight = true;

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (restartOwnsRecovery()) return;
        if (successful) stopWorkerRecovery();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (restartOwnsRecovery()) return;
            if (!resumeSuccessful) {
                stopWorkerRecovery();
                location.reload();
            } else {
                stopWorkerRecovery();
                reconnectModal.close();
            }
        }
    } catch (err) {
        if (restartOwnsRecovery()) return;
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } finally {
        retryInFlight = false;
    }
}

async function resume() {
    if (restartOwnsRecovery()) return;
    try {
        const successful = await Blazor.resumeCircuit();
        if (restartOwnsRecovery()) return;
        stopWorkerRecovery();
        if (!successful) {
            location.reload();
        }
    } catch {
        if (restartOwnsRecovery()) return;
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
