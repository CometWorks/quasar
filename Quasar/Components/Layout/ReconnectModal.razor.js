// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
let reconnectState = "hide";
const restartOwnsRecovery = () => !!(window.quasarRestartReloadSession && window.quasarRestartFeedback);
window.addEventListener("quasar-restart-feedback-changed", () => {
    if (restartOwnsRecovery()) {
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
        reconnectModal.close();
        return;
    }
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
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (restartOwnsRecovery()) return;
            if (!resumeSuccessful) {
                location.reload();
            } else {
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
