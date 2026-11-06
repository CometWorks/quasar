// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
let reconnectState = "hide";
const workerId = JSON.parse(document.getElementById("quasar-connection")?.textContent || "{}").workerId;
let workerRecovery;
const restartOwnsRecovery = () => !!(window.quasarRestartReloadSession && window.quasarRestartFeedback);
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
    reconnectState = event.detail.state;
    if (restartOwnsRecovery()) {
        stopWorkerRecovery();
        reconnectModal.close();
        return;
    }
    if (reconnectState === "show" || reconnectState === "failed") startWorkerRecovery();
    else stopWorkerRecovery();
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
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
    if (!workerId || workerRecovery) return;
    const recovery = { abort: new AbortController() };
    workerRecovery = recovery;
    // A replaced worker cannot restore the old circuit. Check independently of
    // SignalR/RTC retries so a healthy hosted panel does not wait for their timeout.
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
            if (workerRecovery === recovery && !restartOwnsRecovery()
                && typeof current.workerId === "string" && current.workerId && current.workerId !== workerId) {
                stopWorkerRecovery();
                location.reload();
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
    if (restartOwnsRecovery()) return;

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
