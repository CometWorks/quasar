const shell = document.getElementById("quasar-wasm-shell");
if (shell) {
    function showFailure() {
        const loading = shell.querySelector("#quasar-wasm-loading");
        loading.hidden = false;
        loading.querySelector("#quasar-wasm-loading-label").textContent = "The dashboard could not start.";
        loading.querySelector("progress").hidden = true;
        loading.querySelector(".quasar-wasm-spinner").hidden = true;
        const reload = loading.querySelector("button");
        reload.hidden = false;
        reload.onclick = () => location.reload();
    }
    try {
        const settings = JSON.parse(document.getElementById("quasar-connection").textContent);
        const { startDashboard } = await import("./dashboard.js");
        await startDashboard(settings, showFailure);
    } catch { showFailure(); }
}
