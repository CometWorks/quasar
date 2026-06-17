import * as THREE from "three";
import { els, state } from "./state.js";
import { blockBox } from "./geometry.js";
import { blockMaterial, wireMaterial } from "./materials.js";
import { matrixDtoToThree } from "./math.js";
import { disposeObjectTree, fitCameraToScene, replaceFloorGrid } from "./scene.js";
import { resolveModelAsset } from "./mwm-loader.js";
import { log } from "./logging.js";

export async function renderGridScene(scene) {
    state.lastScene = scene;
    if (state.gridGroup) {
        state.scene.remove(state.gridGroup);
        disposeObjectTree(state.gridGroup);
    }

    const group = new THREE.Group();
    group.name = "QuasarGrid";
    group.matrixAutoUpdate = false;
    group.matrix.copy(matrixDtoToThree(scene.grid && scene.grid.worldMatrix));
    state.gridGroup = group;
    state.scene.add(group);

    const definitions = new Map((scene.blockDefinitions || []).map(definition => [definition.id, definition]));
    const modelAssets = new Map((scene.modelAssets || []).map(asset => [asset.assetId, asset]));
    const resolutionStats = await resolveReferencedModels(scene, modelAssets);

    const bounds = new THREE.Box3();
    let rendered = 0;
    for (const block of scene.blockInstances || []) {
        const definition = definitions.get(block.blockTypeId);
        const box = blockBox(block, scene.grid.gridSize || 2.5);
        const mesh = createBlockProxy(block, definition, box);
        group.add(mesh);
        bounds.union(box);
        rendered++;
    }

    state.currentBounds = bounds;
    state.currentGridSize = scene.grid.gridSize || 2.5;
    replaceFloorGrid(bounds, state.currentGridSize);
    fitCameraToScene();

    state.stats.Blocks = (scene.blockInstances || []).length;
    state.stats["Proxy meshes"] = rendered;
    state.stats["Models listed"] = (scene.modelAssets || []).length;
    state.stats["Models found locally"] = resolutionStats.found;
    state.stats["Models missing"] = resolutionStats.missing;
    renderSummary(scene, resolutionStats);
}

function createBlockProxy(block, definition, box) {
    const material = blockMaterial(block.blockTypeId || block.id, proxyOpacity(definition));
    const size = new THREE.Vector3();
    const center = new THREE.Vector3();
    box.getSize(size);
    box.getCenter(center);

    const geometry = new THREE.BoxGeometry(Math.max(size.x, 0.05), Math.max(size.y, 0.05), Math.max(size.z, 0.05));
    const mesh = new THREE.Mesh(geometry, material);
    mesh.position.copy(center);
    mesh.userData.block = block;

    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), wireMaterial(0x93c5fd));
    edges.userData.block = block;
    mesh.add(edges);
    return mesh;
}

function proxyOpacity(definition) {
    return definition && definition.visibilityClass === "transparent" ? 0.36 : 0.72;
}

async function resolveReferencedModels(scene, modelAssets) {
    state.modelResolution.clear();
    let found = 0;
    let missing = 0;
    if (!state.contentFolder) {
        log("No local Content folder selected; all models render as proxies.", true);
        return { found, missing: (scene.modelAssets || []).length };
    }

    for (const asset of modelAssets.values()) {
        const result = await resolveModelAsset(asset);
        state.modelResolution.set(asset.assetId, result);
        if (result.status === "missing") {
            missing++;
            log(result.message, true);
        } else {
            found++;
            if (result.status === "proxy") log(result.message);
        }
    }
    return { found, missing };
}

function renderSummary(scene, resolutionStats) {
    els.sceneSummary.innerHTML = "";
    addSummary("Grid", scene.grid && scene.grid.displayName);
    addSummary("Entity", scene.grid && scene.grid.id);
    addSummary("Blocks", (scene.blockInstances || []).length.toLocaleString());
    addSummary("Models", (scene.modelAssets || []).length.toLocaleString());
    addSummary("Found", resolutionStats.found.toLocaleString());
    addSummary("Missing", resolutionStats.missing.toLocaleString());
    if (scene.warnings && scene.warnings.length) {
        for (const warning of scene.warnings) log(warning, true);
    }
}

function addSummary(label, value) {
    const dt = document.createElement("dt");
    const dd = document.createElement("dd");
    dt.textContent = label;
    dd.textContent = value || "-";
    els.sceneSummary.append(dt, dd);
}
