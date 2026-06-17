import * as THREE from "three";
import { els, state } from "./state.js";
import { blockBox } from "./geometry.js";
import { blockMaterial, wireMaterial } from "./materials.js";
import { colorFromHash, matrixDtoToThree } from "./math.js";
import { disposeObjectTree, fitCameraToScene, replaceFloorGrid } from "./scene.js";
import { resolveModelAsset } from "./mwm-loader.js";
import { loadTexture } from "./texture-loader.js";
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
    let modelMeshes = 0;
    let proxyMeshes = 0;
    for (const block of scene.blockInstances || []) {
        const definition = definitions.get(block.blockTypeId);
        const box = blockBox(block, scene.grid.gridSize || 2.5);
        const blockMeshes = createBlockMeshes(block, definition);
        if (blockMeshes.length) {
            for (const mesh of blockMeshes) group.add(mesh);
            modelMeshes += blockMeshes.length;
        } else {
            const mesh = createBlockProxy(block, definition, box);
            group.add(mesh);
            proxyMeshes++;
        }
        bounds.union(box);
    }

    state.currentBounds = bounds;
    state.currentGridSize = scene.grid.gridSize || 2.5;
    replaceFloorGrid(bounds, state.currentGridSize);
    fitCameraToScene();

    state.stats.Blocks = (scene.blockInstances || []).length;
    state.stats["Model meshes"] = modelMeshes;
    state.stats["Proxy meshes"] = proxyMeshes;
    state.stats["Models listed"] = (scene.modelAssets || []).length;
    state.stats["Models found locally"] = resolutionStats.found;
    state.stats["Models parsed"] = resolutionStats.parsed;
    state.stats["Models missing"] = resolutionStats.missing;
    renderSummary(scene, resolutionStats);
}

function createBlockMeshes(block, definition) {
    const meshes = [];
    if (block.modelParts && block.modelParts.length) {
        for (const part of block.modelParts) {
            const mesh = createModelMesh(part.modelAssetId, block, matrixDtoToThree(part.localMatrix));
            if (mesh) meshes.push(mesh);
        }
    } else {
        const assetId = block.currentModelAssetId || (definition && definition.modelAssetId) || "";
        const matrix = matrixDtoToThree(block.rotation);
        if (definition && definition.modelOffset) matrix.multiply(new THREE.Matrix4().makeTranslation(
            Number(definition.modelOffset.x) || 0,
            Number(definition.modelOffset.y) || 0,
            Number(definition.modelOffset.z) || 0));
        const mesh = createModelMesh(assetId, block, matrix);
        if (mesh) meshes.push(mesh);
    }

    for (const subpart of block.subparts || []) {
        const mesh = createModelMesh(subpart.modelAssetId, block, matrixDtoToThree(subpart.localMatrix));
        if (mesh) meshes.push(mesh);
    }

    return meshes;
}

function createModelMesh(assetId, block, matrix) {
    const resolved = assetId ? state.modelResolution.get(assetId) : null;
    const model = resolved && resolved.status === "parsed" ? resolved.model : null;
    if (!model) return null;

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(model.positions, 3));
    if (model.normals) geometry.setAttribute("normal", new THREE.BufferAttribute(model.normals, 3));
    if (model.uvs) geometry.setAttribute("uv", new THREE.BufferAttribute(model.uvs, 2));
    geometry.setIndex(new THREE.BufferAttribute(model.indices, 1));
    for (const group of model.groups) geometry.addGroup(group.start, group.count, group.materialIndex);
    if (!model.normals) geometry.computeVertexNormals();

    const materials = model.groups.map(group => createModelMaterial(model, group));
    const mesh = new THREE.Mesh(geometry, materials);
    mesh.matrixAutoUpdate = false;
    mesh.matrix.copy(matrix);
    mesh.userData.block = block;
    return mesh;
}

function createModelMaterial(model, group) {
    const technique = String(group.technique || "MESH").toUpperCase();
    const transparent = technique.includes("GLASS") || technique.includes("ALPHA") || technique.includes("HOLO") || technique.includes("SHIELD");
    const material = new THREE.MeshStandardMaterial({
        color: colorFromHash(`${model.logicalPath}|${group.materialName || group.materialIndex}`),
        roughness: 0.72,
        metalness: 0.22,
        transparent,
        opacity: technique.includes("GLASS") ? 0.38 : transparent ? 0.7 : 1,
        side: technique.includes("SINGLE_SIDED") ? THREE.FrontSide : THREE.DoubleSide,
    });
    applyModelTextures(material, group, technique);
    return material;
}

function applyModelTextures(material, group, technique) {
    const base = textureSelection(group.textures, technique.includes("GLASS")
        ? ["GlassTexture", "TransparentTexture", "ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]
        : ["ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]);
    if (base) {
        loadTexture(base.path, base.slot).then(texture => {
            material.map = texture;
            material.color.set(0xffffff);
            material.needsUpdate = true;
        }).catch(error => log(`Texture fallback retained for ${base.path}: ${error.message}`, true));
    }

    const normal = textureSelection(group.textures, ["NormalGlossTexture", "NormalTexture", "NormalMapTexture"]);
    if (normal) {
        loadTexture(normal.path, normal.slot).then(texture => {
            material.normalMap = texture;
            material.normalScale.set(-1, 1);
            material.needsUpdate = true;
        }).catch(error => log(`Normal texture fallback retained for ${normal.path}: ${error.message}`, true));
    }
}

function textureSelection(textures, preferredSlots) {
    const entries = Object.entries(textures || {}).filter(([, path]) => !!path);
    for (const preferred of preferredSlots) {
        const entry = entries.find(([slot]) => slot.toLowerCase() === preferred.toLowerCase());
        if (entry) return { slot: entry[0], path: entry[1] };
    }
    for (const [slot, path] of entries) {
        const text = `${slot} ${path}`.toLowerCase();
        if (preferredSlots.some(preferred => text.includes(preferred.replace(/Texture$/i, "").toLowerCase()))) return { slot, path };
    }
    return null;
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
    let parsed = 0;
    let missing = 0;
    if (!state.contentFolder) {
        log("No local Content folder selected; all models render as proxies.", true);
        return { found, parsed, missing: (scene.modelAssets || []).length };
    }

    for (const asset of modelAssets.values()) {
        const result = await resolveModelAsset(asset);
        state.modelResolution.set(asset.assetId, result);
        if (result.status === "missing") {
            missing++;
            log(result.message, true);
        } else {
            found++;
            if (result.status === "parsed") parsed++;
            if (result.status === "proxy") log(result.message, true);
        }
    }
    return { found, parsed, missing };
}

function renderSummary(scene, resolutionStats) {
    els.sceneSummary.innerHTML = "";
    addSummary("Grid", scene.grid && scene.grid.displayName);
    addSummary("Entity", scene.grid && scene.grid.id);
    addSummary("Blocks", (scene.blockInstances || []).length.toLocaleString());
    addSummary("Models", (scene.modelAssets || []).length.toLocaleString());
    addSummary("Found", resolutionStats.found.toLocaleString());
    addSummary("Parsed", resolutionStats.parsed.toLocaleString());
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
