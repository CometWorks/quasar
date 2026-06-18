export const state = {
    renderer: null,
    scene: null,
    camera: null,
    controls: null,
    ambientLight: null,
    sunLight: null,
    sunMarker: null,
    floorGrid: null,
    resizeObserver: null,
    gridGroup: null,
    raycaster: null,
    pointer: null,
    cameraMode: "orbit",
    flyKeys: new Set(),
    flyYaw: 0,
    flyPitch: 0,
    lastFrameTime: 0,
    currentBounds: null,
    currentGridSize: 2.5,
    lastScene: null,
    contentFolder: null,
    contentFolderName: "",
    modelResolution: new Map(),
    textureResolution: new Map(),
    textureStats: { listed: 0, found: 0, loaded: 0, missing: 0, failed: 0 },
    textureCache: new Map(),
    stats: {},
};

export const els = {};

export function cacheElements() {
    for (const id of [
        "viewport", "sceneSummary", "reloadScene", "contentStatus", "pickContent", "showGridHelper", "showSun",
        "cameraMode", "resetCamera", "stats", "log", "downloadLog", "hoverReadout", "cameraHint"
    ]) {
        els[id] = document.getElementById(id);
    }
}
