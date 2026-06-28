import { els, state } from "./state.js";
import { fitCameraToScene, setCameraMode, updateLighting, updateSceneBounds } from "./scene.js";

export function wireControls(actions) {
    configureVoxelControl();
    els.reloadScene.addEventListener("click", actions.reloadScene);
    els.pickContent.addEventListener("click", actions.pickContent);
    els.pickMods.addEventListener("click", actions.pickMods);
    els.resetCamera.addEventListener("click", fitCameraToScene);
    els.cameraMode.addEventListener("change", () => setCameraMode(els.cameraMode.value));
    els.showGridHelper.addEventListener("change", () => {
        if (state.floorGrid) state.floorGrid.visible = els.showGridHelper.checked;
    });
    els.showVoxels.addEventListener("change", () => {
        if (state.voxelGroup) state.voxelGroup.visible = els.showVoxels.checked;
        updateSceneBounds(false);
    });
    els.showLighting.addEventListener("change", () => {
        updateLighting();
    });
    window.addEventListener("keydown", event => {
        if (state.cameraMode === "fly" && !isTextEntryTarget(event.target) && isFlyKey(event.code)) {
            state.flyKeys.add(event.code);
            event.preventDefault();
        }
    });
    window.addEventListener("keyup", event => {
        if (state.cameraMode !== "fly" || !isFlyKey(event.code)) return;
        state.flyKeys.delete(event.code);
        event.preventDefault();
    });
    window.addEventListener("blur", () => state.flyKeys.clear());
}

export function configureVoxelControl() {
    const supported = !!(state.voxelSupport && state.voxelSupport.present);
    els.showVoxels.disabled = !supported;
    els.showVoxels.checked = supported && !!state.voxelSupport.enabled;
    const row = els.showVoxels.closest("label") || els.showVoxels.parentElement;
    if (row) row.classList.toggle("is-disabled", !supported);
    if (!supported) els.showVoxels.title = "Add voxels=1 to the URL to request voxel data.";
    else els.showVoxels.title = "";
}

function isFlyKey(code) {
    return code === "KeyW" || code === "KeyA" || code === "KeyS" || code === "KeyD" || code === "ShiftLeft" || code === "ShiftRight";
}

function isTextEntryTarget(target) {
    if (!target || document.pointerLockElement === state.renderer.domElement) return false;
    const tagName = target.tagName && target.tagName.toLowerCase();
    return tagName === "input" || tagName === "select" || tagName === "textarea" || target.isContentEditable;
}
