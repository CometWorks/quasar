export const state = {
    renderer: null,
    scene: null,
    camera: null,
    controls: null,
    ambientLight: null,
    sunLight: null,
    sunLightTarget: null,
    sunMarker: null,
    sunMarkerLine: null,
    sunDirection: null,
    sunIntensity: 1,
    floorGrid: null,
    resizeObserver: null,
    gridGroup: null,
    gridLightGroup: null,
    gridLights: [],
    voxelGroup: null,
    voxelMeshes: [],
    voxelSupport: { present: false, enabled: false },
    raycaster: null,
    pointer: null,
    cameraMode: "orbit",
    flyKeys: new Set(),
    flyYaw: 0,
    flyPitch: 0,
    lastFrameTime: 0,
    currentBounds: null,
    currentGridSize: null,
    currentFloorGridAlignment: null,
    viewTransform: null,
    viewRotation: null,
    sceneRenderCounts: { modelMeshes: 0, proxyMeshes: 0, voxelProxies: 0, voxelMeshChunks: 0, voxelMeshParts: 0, voxelMeshVertices: 0, voxelMeshTriangles: 0, voxelClippedChunks: 0, voxelClippedPolygons: 0 },
    lastScene: null,
    contentFolder: null,
    contentFolderName: "",
    modelResolution: new Map(),
    textureResolution: new Map(),
    textureStats: { listed: 0, found: 0, loaded: 0, missing: 0, failed: 0 },
    textureCache: new Map(),
    textureLoadPromises: new Map(),
    timings: {},
    stats: {},
};

export const els = {};

export function cacheElements() {
    for (const id of [
        "viewport", "sceneSummary", "reloadScene", "contentStatus", "pickContent", "folderPicker", "showGridHelper", "showVoxels", "showLighting",
        "cameraMode", "resetCamera", "stats", "log", "downloadLog", "hoverReadout", "cameraHint"
    ]) {
        els[id] = document.getElementById(id);
    }
}
