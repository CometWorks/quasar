import { els, state } from "./state.js";
import { fitCameraToScene, setCameraMode } from "./scene.js";

export function wireControls(actions) {
    els.reloadScene.addEventListener("click", actions.reloadScene);
    els.pickContent.addEventListener("click", actions.pickContent);
    els.resetCamera.addEventListener("click", fitCameraToScene);
    els.cameraMode.addEventListener("change", () => setCameraMode(els.cameraMode.value));
    els.showGridHelper.addEventListener("change", () => {
        if (state.floorGrid) state.floorGrid.visible = els.showGridHelper.checked;
    });
    els.showSun.addEventListener("change", () => {
        if (state.sunMarker) state.sunMarker.visible = els.showSun.checked;
    });
    window.addEventListener("keydown", event => {
        if (state.cameraMode === "fly" && isFlyKey(event.code)) {
            state.flyKeys.add(event.code);
            event.preventDefault();
        }
    });
    window.addEventListener("keyup", event => state.flyKeys.delete(event.code));
    window.addEventListener("blur", () => state.flyKeys.clear());
}

function isFlyKey(code) {
    return code === "KeyW" || code === "KeyA" || code === "KeyS" || code === "KeyD";
}
