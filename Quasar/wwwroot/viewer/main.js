import { cacheElements, els, state } from "./state.js";
import { initScene, animate } from "./scene.js";
import { configureContextControl, configureVoxelControl, wireControls } from "./controls.js";
import { fetchEntityScene, parseContextFlag, parseVoxelFlag } from "./quasar-api.js";
import { pickContentFolder, pickModsFolder, restoreContentFolder, restoreModsFolder } from "./content-folder.js";
import { renderGridScene } from "./grid-renderer.js";
import { downloadLog, log } from "./logging.js";

document.addEventListener("DOMContentLoaded", start);

async function start() {
    cacheElements();
    state.voxelSupport = parseVoxelFlag();
    state.contextSupport = parseContextFlag();
    initScene();
    wireControls({ reloadScene, pickContent: selectContentFolder, pickMods: selectModsFolder });
    els.downloadLog.addEventListener("click", downloadLog);
    animate();

    try {
        const restored = await restoreContentFolder();
        updateContentStatus(restored ? `Using saved Content folder: ${state.contentFolderName}` : "No Content folder selected.");
    } catch (error) {
        log(`Could not restore Content folder: ${error.message}`, true);
    }

    try {
        const restored = await restoreModsFolder();
        updateModsStatus(restored ? `Using saved Mods folder: ${state.modsFolderName}` : "No Mods folder selected.");
    } catch (error) {
        updateModsStatus("No Mods folder selected.");
        log(`Could not restore Mods folder: ${error.message}`, true);
    }

    await reloadScene();
}

async function reloadScene() {
    els.reloadScene.disabled = true;
    try {
        state.timings = {};
        state.voxelSupport = parseVoxelFlag();
        state.contextSupport = parseContextFlag();
        configureVoxelControl();
        configureContextControl();
        log(`Requesting scene snapshot ${state.contextSupport.enabled ? "with" : "without"} context and ${state.voxelSupport.enabled ? "with" : "without"} voxel data from Quasar.`);
        const fetchStart = performance.now();
        const scene = await fetchEntityScene();
        addTiming("sceneSnapshotFetch", performance.now() - fetchStart);
        await renderGridScene(scene);
        const firstVoxel = scene.voxels && scene.voxels[0];
        log(`Loaded scene ${scene.grid && scene.grid.id || firstVoxel && (firstVoxel.displayName || firstVoxel.id) || "unknown"}.`);
    } catch (error) {
        log(error.message, true);
    } finally {
        els.reloadScene.disabled = false;
    }
}

async function selectModsFolder() {
    els.pickMods.disabled = true;
    try {
        const handle = await pickModsFolder();
        updateModsStatus(`Using Mods folder: ${handle.name || "Mods"}`);
        if (state.lastScene) await renderGridScene(state.lastScene);
    } catch (error) {
        if (error.name === "AbortError") return;
        updateModsStatus(error.message, true);
        log(error.message, true);
    } finally {
        els.pickMods.disabled = false;
    }
}

function addTiming(key, durationMs) {
    const metric = state.timings[key] || { count: 0, totalMs: 0, maxMs: 0 };
    metric.count++;
    metric.totalMs += durationMs;
    metric.maxMs = Math.max(metric.maxMs, durationMs);
    state.timings[key] = metric;
}

async function selectContentFolder() {
    els.pickContent.disabled = true;
    try {
        const handle = await pickContentFolder();
        updateContentStatus(`Using Content folder: ${handle.name || "Content"}`);
        if (state.lastScene) await renderGridScene(state.lastScene);
    } catch (error) {
        if (error.name === "AbortError") return;
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

function updateModsStatus(message, isError = false) {
    els.modsStatus.textContent = message;
    els.modsStatus.classList.toggle("is-error", isError);
}
