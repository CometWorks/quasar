import { startBlazor } from "./circuit.js";
const settings = JSON.parse(document.getElementById("quasar-connection").textContent);
startBlazor(settings).catch(() => {
    const message = document.createElement("div");
    message.setAttribute("role", "alert");
    message.textContent = "The panel connection is unavailable. Reload to reconnect.";
    document.body.prepend(message);
});
