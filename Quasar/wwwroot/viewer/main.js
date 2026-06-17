import { cacheElements, els, state } from "./state.js";
import { initScene, animate } from "./scene.js";
import { wireControls } from "./controls.js";
import { fetchEntityScene } from "./quasar-api.js";
import { pickContentFolder, restoreContentFolder } from "./content-folder.js";
import { renderGridScene } from "./grid-renderer.js";
import { downloadLog, log } from "./logging.js";

document.addEventListener("DOMContentLoaded", start);

async function start() {
    cacheElements();
    initScene();
    wireControls({ reloadScene, pickContent: selectContentFolder });
    els.downloadLog.addEventListener("click", downloadLog);
    animate();

    try {
        const restored = await restoreContentFolder();
        updateContentStatus(restored ? `Using saved Content folder: ${state.contentFolderName}` : "No Content folder selected.");
    } catch (error) {
        log(`Could not restore Content folder: ${error.message}`, true);
    }

    await reloadScene();
}

async function reloadScene() {
    els.reloadScene.disabled = true;
    try {
        log("Requesting metadata-only scene snapshot from Quasar.");
        const scene = await fetchEntityScene();
        await renderGridScene(scene);
        log(`Loaded scene ${scene.grid && scene.grid.id}.`);
    } catch (error) {
        log(error.message, true);
    } finally {
        els.reloadScene.disabled = false;
    }
}

async function selectContentFolder() {
    els.pickContent.disabled = true;
    try {
        const handle = await pickContentFolder();
        updateContentStatus(`Using Content folder: ${handle.name || "Content"}`);
        if (state.lastScene) await renderGridScene(state.lastScene);
    } catch (error) {
        updateContentStatus(error.message, true);
        log(error.message, true);
    } finally {
        els.pickContent.disabled = false;
    }
}

function updateContentStatus(message, isError = false) {
    els.contentStatus.textContent = message;
    els.contentStatus.classList.toggle("is-error", isError);
}
