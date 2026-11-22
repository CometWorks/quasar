function connectionState(state) {
    window.quasarConnectionState = state;
    window.dispatchEvent(new CustomEvent("quasar-connection-state-changed", { detail: { state } }));
}

connectionState("connecting");
try {
    const settings = JSON.parse(document.getElementById("quasar-connection").textContent);
    if (settings.wasm) {
        const { startDashboard } = await import("./dashboard.js");
        await startDashboard(settings);
    } else {
        const { startBlazor } = await import("./circuit.js");
        await startBlazor(settings);
    }
    connectionState("connected");
} catch {
    connectionState("failed");
}
