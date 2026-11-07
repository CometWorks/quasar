function connectionState(state) {
    window.quasarConnectionState = state;
    window.dispatchEvent(new CustomEvent("quasar-connection-state-changed", { detail: { state } }));
}

connectionState("connecting");
try {
    // Catch module loading failures as well as session, peer and circuit failures.
    const { startBlazor } = await import("./circuit.js");
    const settings = JSON.parse(document.getElementById("quasar-connection").textContent);
    await startBlazor(settings);
    connectionState("connected");
} catch {
    connectionState("failed");
}
