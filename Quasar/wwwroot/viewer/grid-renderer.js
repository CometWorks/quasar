import * as THREE from "three";
import { els, state } from "./state.js";
import { blockBox, boundsToBox3 } from "./geometry.js";
import { colorFromHash, matrixDtoToThree, num, vec3 } from "./math.js";
import { disposeObjectTree, fitCameraToScene, updateLighting, updateSceneBounds, updateSunLightPosition } from "./scene.js";
import { resolveModelAsset } from "./mwm-loader.js";
import { loadTexture, textureToCanvas } from "./texture-loader.js";
import { log } from "./logging.js";
import { getContentFolderCacheGeneration, resolveContentFile } from "./content-folder.js";
import { drawLcdBitmapText, getLoadedLcdBitmapFont, lcdBitmapTextScale, loadLcdBitmapFont, supportedLcdFontId } from "./lcd-font-loader.js";

let textureStatsToken = 0;
let modelRenderToken = 0;
let armorSkinDefinitionsGeneration = -1;
let armorSkinDefinitionsPromise = null;
let armorSkinDefinitions = new Map();
let transparentMaterialDefinitionsGeneration = -1;
let transparentMaterialDefinitionsPromise = null;
let transparentMaterialDefinitions = new Map();
const MAX_CONCURRENT_MODEL_RESOLVES = 48;
const MODEL_REBUILD_THROTTLE_MS = 100;
const MAX_VIEWER_LIGHTS = 128;

export async function renderGridScene(scene) {
    const renderToken = ++modelRenderToken;
    state.lastScene = scene;
    state.stats = {};
    if (state.gridGroup) {
        state.scene.remove(state.gridGroup);
        disposeObjectTree(state.gridGroup);
        state.gridLightGroup = null;
        state.gridLights = [];
    }
    if (state.voxelGroup) {
        state.scene.remove(state.voxelGroup);
        disposeObjectTree(state.voxelGroup);
        state.voxelGroup = null;
        state.voxelMeshes = [];
    }

    configureRelativeView(scene);
    configureEnvironment(scene);

    const group = new THREE.Group();
    group.name = "QuasarGrid";
    group.matrixAutoUpdate = false;
    group.matrix.copy(gridViewMatrix(scene));
    state.gridGroup = group;
    state.scene.add(group);
    buildGridLightGroup(scene, group);

    const definitions = new Map((scene.blockDefinitions || []).map(definition => [definition.id, definition]));
    const modelAssets = new Map((scene.modelAssets || []).map(asset => [asset.assetId, asset]));
    const renderTextureToken = ++textureStatsToken;
    state.modelResolution.clear();
    const textureStats = initializeTextureStats(collectReferencedTextureAssets(scene));
    const resolutionStats = { found: 0, parsed: 0, missing: 0, listed: (scene.modelAssets || []).length };
    const progress = createProgressiveModelRender(scene, definitions, group, renderTextureToken, renderToken);

    const bounds = new THREE.Box3();
    for (const block of scene.blockInstances || []) bounds.union(blockBox(block, scene.grid.gridSize || 2.5));
    state.currentBounds = bounds;
    state.currentGridSize = scene.grid.gridSize || 2.5;
    state.currentFloorGridAlignment = floorGridAlignment(scene);
    renderVoxelBodies(scene.voxels || []);
    progress.rebuild();
    updateSceneBounds(false);
    updateSunLightPosition();
    fitCameraToScene();
    updateModelStats(resolutionStats, progress.lastRenderStats, modelAssets.size);
    updateTextureStats();
    state.stats["Voxel bodies"] = (scene.voxels || []).length;
    state.stats["Voxel proxies"] = state.sceneRenderCounts.voxelProxies;
    state.stats["LCD surfaces"] = countLcdSurfaces(scene);
    updateGridLightStats(collectSceneLightSources(scene));
    renderSummary(scene, resolutionStats, textureStats);

    await ensureArmorSkinDefinitionsLoaded();
    await ensureTransparentMaterialDefinitionsLoaded();
    if (renderToken !== modelRenderToken) return;

    await resolveReferencedModelsProgressively(scene, modelAssets, resolutionStats, progress, renderToken);
    if (renderToken !== modelRenderToken) return;
    progress.rebuild();
    updateModelStats(resolutionStats, progress.lastRenderStats, modelAssets.size);
    updateSummaryModelStats(resolutionStats);
    updateTimingStats();
}

function createProgressiveModelRender(scene, definitions, group, textureToken, renderToken) {
    let modelLayer = null;
    let rebuildQueued = false;
    let rebuildTimer = 0;
    let completedSinceLastRebuild = 0;
    const totalModels = (scene.modelAssets || []).length;
    const rebuildModelStep = progressiveRebuildModelStep(scene, totalModels);
    const rebuildMaxDelayMs = progressiveRebuildMaxDelayMs(scene);

    function queueRebuildFrame() {
        requestAnimationFrame(() => {
            rebuildQueued = false;
            if (renderToken !== modelRenderToken) return;
            progress.rebuild();
        });
    }

    function queueRebuild(delayMs) {
        rebuildQueued = true;
        if (delayMs > 0) {
            rebuildTimer = window.setTimeout(() => {
                rebuildTimer = 0;
                queueRebuildFrame();
            }, delayMs);
            return;
        }

        queueRebuildFrame();
    }

    const progress = {
        lastRenderStats: { modelMeshes: 0, proxyMeshes: 0, proxyBatches: 0, modelBatches: 0, sharedGeometries: 0, sharedMaterials: 0 },
        scheduleRebuild() {
            if (renderToken !== modelRenderToken) return;
            completedSinceLastRebuild++;
            const allModelsResolved = state.modelResolution.size >= totalModels;
            const readyForRebuild = allModelsResolved || completedSinceLastRebuild >= rebuildModelStep;
            if (rebuildQueued) {
                if (readyForRebuild && rebuildTimer) {
                    window.clearTimeout(rebuildTimer);
                    rebuildTimer = 0;
                    queueRebuildFrame();
                }
                return;
            }

            queueRebuild(readyForRebuild ? MODEL_REBUILD_THROTTLE_MS : rebuildMaxDelayMs);
        },
        rebuild() {
            if (rebuildTimer) {
                window.clearTimeout(rebuildTimer);
                rebuildTimer = 0;
            }
            rebuildQueued = false;
            completedSinceLastRebuild = 0;
            const renderContext = createRenderContext(textureToken);
            const nextLayer = buildModelLayer(scene, definitions, renderContext);
            const previousLayer = modelLayer;
            modelLayer = nextLayer.layer;
            group.add(modelLayer);
            if (previousLayer) {
                group.remove(previousLayer);
                disposeObjectTree(previousLayer);
            }
            progress.lastRenderStats = nextLayer.stats;
            state.sceneRenderCounts.modelMeshes = nextLayer.stats.modelMeshes;
            state.sceneRenderCounts.proxyMeshes = nextLayer.stats.proxyMeshes;
            updateModelRenderStats(nextLayer.stats);
            updateTextureStats();
        },
    };
    return progress;
}

function progressiveRebuildModelStep(scene, totalModels) {
    const blocks = (scene.blockInstances || []).length;
    if (blocks > 2000) return Math.max(24, Math.ceil(totalModels / 8));
    if (blocks > 1000) return Math.max(16, Math.ceil(totalModels / 10));
    return Math.max(8, Math.ceil(totalModels / 12));
}

function progressiveRebuildMaxDelayMs(scene) {
    const blocks = (scene.blockInstances || []).length;
    if (blocks > 2000) return 900;
    if (blocks > 1000) return 650;
    return 250;
}

function floorGridAlignment(scene) {
    const gridSize = scene.grid && scene.grid.gridSize || 2.5;
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;

    for (const block of scene.blockInstances || []) {
        const min = block.min || block.cell;
        const max = block.max || block.cell || min;
        if (!min || !max) continue;
        minX = Math.min(minX, Number(min.x) || 0);
        maxX = Math.max(maxX, Number(max.x) || 0);
        minZ = Math.min(minZ, Number(min.z) || 0);
        maxZ = Math.max(maxZ, Number(max.z) || 0);
    }

    if (!Number.isFinite(minX) || !Number.isFinite(maxX) || !Number.isFinite(minZ) || !Number.isFinite(maxZ)) {
        return { offsetX: 0, offsetZ: 0, cellCountX: 0, cellCountZ: 0 };
    }

    const cellCountX = Math.max(1, Math.round(maxX - minX + 1));
    const cellCountZ = Math.max(1, Math.round(maxZ - minZ + 1));
    return {
        offsetX: floorAxisOffset(cellCountX, gridSize),
        offsetZ: floorAxisOffset(cellCountZ, gridSize),
        cellCountX,
        cellCountZ,
    };
}

function floorAxisOffset(cellCount, gridSize) {
    return Math.abs(cellCount % 2) === 1 ? gridSize * 0.5 : 0;
}

function buildModelLayer(scene, definitions, renderContext) {
    const layer = new THREE.Group();
    layer.name = "QuasarGridModels";
    let modelMeshes = 0;
    let proxyMeshes = 0;
    const proxyBatches = new Map();

    for (const block of scene.blockInstances || []) {
        const definition = definitions.get(block.blockTypeId);
        const box = blockBox(block, scene.grid.gridSize || 2.5);
        const blockMeshes = createBlockMeshes(block, definition, renderContext);
        if (blockMeshes.length) {
            for (const mesh of blockMeshes) queueModelBatch(mesh, renderContext);
            modelMeshes += blockMeshes.length;
        } else {
            queueBlockProxy(proxyBatches, block, definition, box);
            proxyMeshes++;
        }
    }

    flushProxyBatches(layer, proxyBatches);
    const modelBatches = flushModelBatches(layer, renderContext);
    return {
        layer,
        stats: {
            modelMeshes,
            proxyMeshes,
            proxyBatches: proxyBatches.size,
            modelBatches,
            sharedGeometries: renderContext.geometries.size,
            sharedMaterials: renderContext.materials.size,
        },
    };
}

function createRenderContext(textureToken) {
    return {
        textureToken,
        geometries: new Map(),
        materials: new Map(),
        batches: new Map(),
    };
}

function configureRelativeView(scene) {
    const worldMatrix = matrixDtoToThree(scene.grid && scene.grid.worldMatrix);
    const center = gridCenterWorld(scene, worldMatrix);
    const inverseRotation = new THREE.Matrix4().extractRotation(worldMatrix).invert();
    state.viewRotation = inverseRotation;
    state.viewTransform = inverseRotation.clone().multiply(new THREE.Matrix4().makeTranslation(-center.x, -center.y, -center.z));
}

function configureEnvironment(scene) {
    state.sunDirection = vec3(scene.environment && scene.environment.sunDirection).normalize();
    if (state.sunDirection.lengthSq() === 0) state.sunDirection.set(0.33946735, 0.70979536, -0.61721337).normalize();
    state.sunIntensity = Math.max(0.15, num(scene.environment && scene.environment.sunIntensity, 1));
    updateLighting();
}

function buildGridLightGroup(scene, gridGroup) {
    state.gridLights = [];
    const sources = collectSceneLightSources(scene);
    const lightGroup = new THREE.Group();
    lightGroup.name = "QuasarGridLights";
    gridGroup.add(lightGroup);
    state.gridLightGroup = lightGroup;

    const center = gridLocalCenter(scene);
    const selectedSources = sources
        .slice()
        .sort((a, b) => lightSourceSortKey(b, center) - lightSourceSortKey(a, center))
        .slice(0, MAX_VIEWER_LIGHTS);

    for (const source of selectedSources) {
        const light = createGridLight(source, lightGroup);
        if (!light) continue;
        light.userData.lightSource = source;
        state.gridLights.push(light);
    }

    if (sources.length > selectedSources.length) log(`Grid lights capped at ${selectedSources.length} of ${sources.length}.`, "warn");
    updateLighting();
}

function collectSceneLightSources(scene) {
    const sources = [];
    const seen = new Set();
    const add = source => {
        if (!source) return;
        const key = source.id || `${source.blockId || ""}:${source.kind || ""}:${formatViewerVector(source.position)}`;
        if (seen.has(key)) return;
        seen.add(key);
        sources.push(source);
    };

    for (const source of scene.lightSources || []) add(source);
    for (const block of scene.blockInstances || []) {
        for (const subpart of block.subparts || []) {
            for (const source of subpart.lightSources || []) add(source);
        }
    }

    return sources;
}

function formatViewerVector(vector) {
    return `${num(vector && vector.x, 0).toFixed(3)}, ${num(vector && vector.y, 0).toFixed(3)}, ${num(vector && vector.z, 0).toFixed(3)}`;
}

function createGridLight(source, lightGroup) {
    if (!source || source.enabled === false || num(source.intensity, 0) <= 0) return null;
    const kind = String(source.kind || "point").toLowerCase();
    const color = lightColor(source.color);
    const position = vec3(source.position);
    const intensity = Math.max(0, num(source.intensity, 0));
    const radius = Math.max(0, num(source.radius, 0));
    const reflectorRadius = Math.max(radius, num(source.reflectorRadius, 0));
    const decay = lightDecay(source.falloff);

    if (kind === "spot") {
        const coneRadians = THREE.MathUtils.degToRad(Math.max(1, Math.min(179, num(source.coneDegrees, 52)))) * 0.5;
        const light = new THREE.SpotLight(color, intensity, reflectorRadius || radius || 1, coneRadians, 0.35, decay);
        light.name = `GridSpotLight:${source.id || source.blockId || "unknown"}`;
        light.position.copy(position);
        light.castShadow = false;

        const direction = vec3(source.direction);
        if (direction.lengthSq() === 0) direction.set(0, 0, -1);
        direction.normalize();
        light.target.position.copy(position).addScaledVector(direction, Math.max(reflectorRadius || radius || 1, 1));
        lightGroup.add(light.target);
        lightGroup.add(light);
        return light;
    }

    const light = new THREE.PointLight(color, intensity, radius || 1, decay);
    light.name = `GridPointLight:${source.id || source.blockId || "unknown"}`;
    light.position.copy(position);
    light.castShadow = false;
    lightGroup.add(light);
    return light;
}

function gridLocalCenter(scene) {
    const bounds = gridLocalBounds(scene);
    return bounds && !bounds.isEmpty() ? bounds.getCenter(new THREE.Vector3()) : new THREE.Vector3();
}

function lightSourceSortKey(source, center) {
    const enabledWeight = source && source.enabled !== false ? 1_000_000_000 : 0;
    const position = vec3(source && source.position);
    const distancePenalty = Math.min(1_000_000, position.distanceTo(center));
    const impact = Math.max(0, num(source && source.intensity, 0)) * Math.max(num(source && source.radius, 0), num(source && source.reflectorRadius, 0), 1);
    return enabledWeight + impact * 1000 - distancePenalty;
}

function lightColor(color) {
    if (!color) return new THREE.Color(1, 1, 1);
    return new THREE.Color((Number(color.r) || 0) / 255, (Number(color.g) || 0) / 255, (Number(color.b) || 0) / 255);
}

function lightDecay(falloff) {
    const value = num(falloff, 1.5);
    return Math.min(2, Math.max(0, 3 - value));
}

function gridViewMatrix(scene) {
    const worldMatrix = matrixDtoToThree(scene.grid && scene.grid.worldMatrix);
    return (state.viewTransform || new THREE.Matrix4()).clone().multiply(worldMatrix);
}

function gridCenterWorld(scene, worldMatrix) {
    const worldBounds = boundsToBox3(scene.grid && scene.grid.bounds);
    if (worldBounds && !worldBounds.isEmpty()) return worldBounds.getCenter(new THREE.Vector3());

    const localBounds = gridLocalBounds(scene);
    if (localBounds && !localBounds.isEmpty()) return localBounds.getCenter(new THREE.Vector3()).applyMatrix4(worldMatrix);

    return new THREE.Vector3().setFromMatrixPosition(worldMatrix);
}

function gridLocalBounds(scene) {
    const bounds = new THREE.Box3();
    let hasBounds = false;
    for (const chunk of scene.chunks || []) {
        const min = vec3(chunk.localAabbMin);
        const max = vec3(chunk.localAabbMax);
        bounds.union(new THREE.Box3(min, max));
        hasBounds = true;
    }
    if (hasBounds) return bounds;

    const gridSize = scene.grid && scene.grid.gridSize || 2.5;
    for (const block of scene.blockInstances || []) {
        bounds.union(blockBox(block, gridSize));
        hasBounds = true;
    }
    return hasBounds ? bounds : null;
}

function renderVoxelBodies(voxels) {
    state.sceneRenderCounts.voxelProxies = 0;
    if (!voxels.length) return;

    const group = new THREE.Group();
    group.name = "QuasarVoxels";
    group.matrixAutoUpdate = false;
    group.matrix.copy(state.viewTransform || new THREE.Matrix4());
    group.visible = !els.showVoxels || els.showVoxels.checked;
    state.voxelGroup = group;
    state.scene.add(group);

    for (const voxel of voxels) {
        const mesh = createVoxelProxy(voxel);
        if (!mesh) continue;
        group.add(mesh);
        state.voxelMeshes.push(mesh);
        state.sceneRenderCounts.voxelProxies++;
    }
}

function createVoxelProxy(voxel) {
    const bounds = boundsToBox3(voxel.worldAabb);
    if (!bounds || bounds.isEmpty()) return null;

    const center = bounds.getCenter(new THREE.Vector3());
    const size = bounds.getSize(new THREE.Vector3());
    const color = voxel.kind === "planet" ? 0x22c55e : 0xa3e635;
    const material = new THREE.MeshStandardMaterial({
        color,
        roughness: 0.95,
        metalness: 0,
        transparent: true,
        opacity: voxel.kind === "planet" ? 0.08 : 0.18,
        wireframe: true,
    });

    let geometry;
    if (voxel.kind === "planet" && voxel.planet) {
        const radius = Math.max(num(voxel.planet.averageRadius, 0), num(voxel.planet.maximumRadius, 0), size.x / 2, size.y / 2, size.z / 2);
        geometry = new THREE.SphereGeometry(Math.max(radius, 1), 48, 24);
    } else {
        geometry = new THREE.BoxGeometry(Math.max(size.x, 0.1), Math.max(size.y, 0.1), Math.max(size.z, 0.1));
    }

    const mesh = new THREE.Mesh(geometry, material);
    mesh.name = `Voxel:${voxel.id || voxel.displayName || "unknown"}`;
    mesh.position.copy(center);
    mesh.userData.voxel = voxel;
    return mesh;
}

function createBlockMeshes(block, definition, renderContext) {
    const meshes = [];
    if (block.modelParts && block.modelParts.length) {
        for (const part of block.modelParts) {
            meshes.push(...createModelMeshes(part.modelAssetId, block, matrixDtoToThree(part.localMatrix), part.patternOffset, renderContext));
        }
    } else {
        const assetId = block.currentModelAssetId || (definition && definition.modelAssetId) || "";
        meshes.push(...createModelMeshes(assetId, block, composeModelInstanceMatrix(block, definition), null, renderContext));
    }

    for (const subpart of block.subparts || []) {
        meshes.push(...createModelMeshes(subpart.modelAssetId, block, matrixDtoToThree(subpart.localMatrix), null, renderContext));
    }

    return meshes;
}

function composeModelInstanceMatrix(block, definition) {
    if (block.rotation) return matrixDtoToThree(block.rotation);

    const matrix = new THREE.Matrix4().compose(
        vec3(block.translation),
        new THREE.Quaternion(),
        vec3(block.scale || { x: 1, y: 1, z: 1 }));
    const offset = definition && definition.modelOffset;
    if (offset) matrix.multiply(new THREE.Matrix4().makeTranslation(
        Number(offset.x) || 0,
        Number(offset.y) || 0,
        Number(offset.z) || 0));
    return matrix;
}

function createModelMeshes(assetId, block, matrix, patternOffset = null, renderContext = createRenderContext(textureStatsToken)) {
    const resolved = assetId ? state.modelResolution.get(assetId) : null;
    const model = resolved && resolved.status === "parsed" ? resolved.model : null;
    if (!model) return [];

    const renderables = [];
    const groupsByLayer = new Map();
    for (const group of model.groups) {
        if (isOfflineHiddenLcdMaterial(block, group.materialName)) continue;
        if (isResetLcdModelMaterialHidden(block, group.materialName)) continue;
        if (isLcdModelFallbackMaterialHidden(block, group.materialName)) continue;
        const material = sharedModelMaterial(model, group, block, renderContext);
        if (!material) continue;
        const layer = modelMaterialRenderLayer(material);
        let entries = groupsByLayer.get(layer);
        if (!entries) {
            entries = [];
            groupsByLayer.set(layer, entries);
        }
        entries.push({ group, material });
    }

    for (const [layer, entries] of groupsByLayer) {
        const groups = entries.map(entry => entry.group);
        const materials = entries.map(entry => entry.material);
        const geometry = sharedModelGeometry(model, patternOffset, renderContext, groups, layer);
        renderables.push({
            geometry,
            materials,
            matrix,
            block,
            colorMask: colorMaskForBlock(block),
            batchKey: `${geometry.userData.renderCacheKey}|${materials.map(material => material.userData.renderCacheKey).join("|")}`,
        });
    }
    return renderables;
}

function modelMaterialRenderLayer(material) {
    const mode = material.userData.seRenderMode;
    if (mode === "lcd") return "lcd";
    if (mode === "blended") return "blended";
    if (mode === "decal" || mode === "decal-cutout") return "decal";
    return "base";
}

function queueModelBatch(renderable, renderContext) {
    let batch = renderContext.batches.get(renderable.batchKey);
    if (!batch) {
        batch = {
            geometry: renderable.geometry,
            materials: renderable.materials,
            instances: [],
        };
        renderContext.batches.set(renderable.batchKey, batch);
    }
    batch.instances.push(renderable);
}

function flushModelBatches(group, renderContext) {
    const color = new THREE.Color();
    for (const [key, batch] of renderContext.batches) {
        const mesh = new THREE.InstancedMesh(batch.geometry, batch.materials, batch.instances.length);
        mesh.name = `ModelBatch:${key}`;
        mesh.matrixAutoUpdate = false;
        mesh.instanceMatrix.setUsage(THREE.StaticDrawUsage);
        mesh.renderOrder = modelBatchRenderOrder(batch.materials);
        mesh.castShadow = modelBatchCastsShadow(batch.materials);
        mesh.receiveShadow = true;
        mesh.userData.blocks = [];
        const useInstanceColor = modelBatchUsesInstanceColor(batch.materials);

        for (let i = 0; i < batch.instances.length; i++) {
            const instance = batch.instances[i];
            mesh.setMatrixAt(i, instance.matrix);
            if (useInstanceColor) {
                color.r = instance.colorMask.x;
                color.g = instance.colorMask.y;
                color.b = instance.colorMask.z;
                mesh.setColorAt(i, color);
            }
            mesh.userData.blocks.push(instance.block);
        }
        mesh.instanceMatrix.needsUpdate = true;
        if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
        if (typeof mesh.computeBoundingBox === "function") mesh.computeBoundingBox();
        if (typeof mesh.computeBoundingSphere === "function") mesh.computeBoundingSphere();
        mesh.onBeforeRender = applyDefaultBlockColorMaskUniforms;
        group.add(mesh);
    }
    return renderContext.batches.size;
}

function modelBatchUsesInstanceColor(materials) {
    return materials.some(material => material.userData.seRenderMode !== "lcd");
}

function modelBatchCastsShadow(materials) {
    return materials.some(material => {
        const mode = material.userData.seRenderMode;
        return mode !== "lcd" && mode !== "blended" && mode !== "decal";
    });
}

function modelBatchRenderOrder(materials) {
    // Transparent LCDs need to render after the floor grid. Otherwise Three.js
    // depth-sorts them against the grid by object center, which flips with view angle.
    if (materials.some(material => material.userData.seRenderMode === "lcd" && material.transparent)) return 2;
    if (materials.some(material => material.userData.seRenderMode === "blended")) return 2;
    if (materials.some(material => material.userData.seRenderMode === "decal" || material.userData.seRenderMode === "decal-cutout")) return 1;
    return 0;
}

function sharedModelGeometry(model, patternOffset, renderContext, groups = model.groups, layer = "all") {
    const key = `${model.geometryLogicalPath || model.logicalPath}|${patternOffsetKey(patternOffset)}|${layer}|${modelGeometryGroupsKey(groups)}`;
    if (renderContext.geometries.has(key)) return renderContext.geometries.get(key);

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(model.positions, 3));
    if (model.normals) geometry.setAttribute("normal", new THREE.BufferAttribute(model.normals, 3));
    if (model.uvs) geometry.setAttribute("uv", new THREE.BufferAttribute(transformModelUvs(model.uvs, patternOffset), 2));
    geometry.setIndex(new THREE.BufferAttribute(model.indices, 1));
    for (let i = 0; i < groups.length; i++) geometry.addGroup(groups[i].start, groups[i].count, i);
    if (!model.normals) geometry.computeVertexNormals();
    geometry.computeBoundingSphere();
    geometry.userData.renderCacheKey = key;
    renderContext.geometries.set(key, geometry);
    return geometry;
}

function modelGeometryGroupsKey(groups) {
    if (!groups || !groups.length) return "none";
    return groups.map(group => `${group.materialIndex}:${group.start}:${group.count}`).join(",");
}

function patternOffsetKey(patternOffset) {
    if (!patternOffset) return "default";
    const x = Number(patternOffset.x ?? patternOffset.X);
    const y = Number(patternOffset.y ?? patternOffset.Y);
    const z = Number(patternOffset.z ?? patternOffset.Z);
    const w = Number(patternOffset.w ?? patternOffset.W);
    return [x, y, z, w].map(value => Number.isFinite(value) ? value.toFixed(6) : "n").join(",");
}

function transformModelUvs(uvs, patternOffset) {
    const transformed = new Float32Array(uvs.length);
    const offset = patternUvOffset(patternOffset);
    // MWM UVs are already in Space Engineers shader orientation; generated cube parts only add atlas offsets.
    for (let i = 0; i < uvs.length; i += 2) {
        transformed[i] = uvs[i] + offset.x;
        transformed[i + 1] = uvs[i + 1] + offset.y;
    }
    return transformed;
}

function patternUvOffset(patternOffset) {
    if (!patternOffset) return { x: 0, y: 0 };
    const patternU = Number(patternOffset.z ?? patternOffset.Z);
    const patternV = Number(patternOffset.w ?? patternOffset.W);
    if (!Number.isFinite(patternU) || !Number.isFinite(patternV) || patternU === 0 || patternV === 0) return { x: 0, y: 0 };

    const offsetU = Number(patternOffset.x ?? patternOffset.X) / patternU;
    const offsetV = Number(patternOffset.y ?? patternOffset.Y) / patternV;
    return {
        x: Number.isFinite(offsetU) ? offsetU : 0,
        y: Number.isFinite(offsetV) ? offsetV : 0,
    };
}

function sharedModelMaterial(model, group, block, renderContext) {
    const technique = String(group.technique || "MESH").toUpperCase();
    const lcdSurface = lcdSurfaceForMaterial(block, group.materialName);
    if (lcdSurface && lcdReplacementMode(lcdSurface) !== "model") return sharedLcdMaterial(model, group, block, lcdSurface, renderContext, technique);

    const skin = materialSkinOverride(block, group.materialName);
    const transparentMaterial = transparentMaterialForGroup(group, technique);
    const renderMode = modelMaterialRenderMode(technique);
    const textures = materialTexturesForGroup(group, skin, transparentMaterial);
    const colorMaskable = isModelMaterialColorMaskable(group, technique, textures);
    const key = `${model.logicalPath}|${group.materialIndex}|${group.materialName}|${group.technique}|${stableTextureKey(textures)}|glass=${transparentMaterialKey(transparentMaterial)}|metalnessColorable=${skin && skin.metalnessColorable ? 1 : 0}`;
    if (renderContext.materials.has(key)) return renderContext.materials.get(key);

    const color = transparentMaterial && transparentMaterial.color ? new THREE.Color(transparentMaterial.color.r, transparentMaterial.color.g, transparentMaterial.color.b) : colorFromHash(`${model.logicalPath}|${group.materialName || group.materialIndex}`);
    const transparent = renderMode.blended || (renderMode.decal && !renderMode.cutout);
    const material = new THREE.MeshStandardMaterial({
        color,
        roughness: transparentMaterial && Number.isFinite(transparentMaterial.gloss) ? clamp(1 - transparentMaterial.gloss, 0, 1) : 0.72,
        metalness: 0.22,
        transparent,
        opacity: transparentMaterial && transparentMaterial.color ? clamp(transparentMaterial.color.a, 0, 1) : technique.includes("GLASS") ? 0.38 : renderMode.blended ? 0.7 : 1,
        depthWrite: !renderMode.blended && !renderMode.decal,
        side: modelMaterialSide(technique, renderMode),
    });
    if (transparentMaterial && transparentMaterial.color) material.userData.seTransparentMaterialColor = color.clone();
    if (renderMode.decal) {
        material.polygonOffset = true;
        material.polygonOffsetFactor = -1;
        material.polygonOffsetUnits = -1;
    }
    if (transparent) material.forceSinglePass = true;
    material.userData.renderCacheKey = key;
    material.userData.seRenderMode = modelMaterialRenderModeName(renderMode);
    applySpaceEngineersColorMasking(material, skin && skin.metalnessColorable, colorMaskable);
    applyModelTextures(material, { ...group, textures }, technique, renderMode, renderContext.textureToken, colorMaskable);
    renderContext.materials.set(key, material);
    return material;
}

function sharedLcdMaterial(model, group, block, lcdSurface, renderContext, technique) {
    const key = `${model.logicalPath}|${group.materialIndex}|${group.materialName}|lcd=${block.id || ""}:${lcdSurface.index || 0}:${lcdSurfaceKey(lcdSurface)}`;
    if (renderContext.materials.has(key)) return renderContext.materials.get(key);

    const transparentSurface = isTransparentLcdSurface(lcdSurface);

    const material = new THREE.MeshStandardMaterial({
        color: 0xffffff,
        roughness: 0.36,
        metalness: transparentSurface ? 0 : normalizedBackgroundAlpha(lcdSurface),
        emissive: 0xffffff,
        emissiveIntensity: 0.65,
        transparent: transparentSurface,
        opacity: transparentSurface ? transparentLcdSurfaceOpacity(lcdSurface) : 1,
        depthWrite: !transparentSurface,
        // Transparent LCD screen planes are transparent material parts in-game;
        // keep them single-sided so the front/back planes do not overlay.
        side: transparentSurface ? THREE.FrontSide : modelMaterialSide(technique, { blended: false, decal: false, cutout: false }),
    });
    material.userData.renderCacheKey = key;
    material.userData.seRenderMode = "lcd";
    applyLcdSurfaceTexture(material, lcdSurface, renderContext.textureToken);
    renderContext.materials.set(key, material);
    return material;
}

function lcdSurfaceForMaterial(block, materialName) {
    const key = String(materialName || "").trim().toLowerCase();
    if (!key) return null;
    for (const surface of block.lcdSurfaces || []) {
        if (String(surface.materialName || "").trim().toLowerCase() === key) return surface;
    }
    return null;
}

function isOfflineHiddenLcdMaterial(block, materialName) {
    const key = String(materialName || "").trim().toLowerCase();
    if (!key) return false;
    const hidden = block.lcdMaterialsToHideWhenOffline || [];
    if (!hidden.length) return false;
    if (!(block.lcdSurfaces || []).some(surface => surface && surface.isWorking === false)) return false;
    return hidden.some(name => String(name || "").trim().toLowerCase() === key);
}

function isResetLcdModelMaterialHidden(block, materialName) {
    const surface = lcdSurfaceForMaterial(block, materialName);
    if (!surface || lcdReplacementMode(surface) !== "model") return false;
    return !isTransparentScreenAreaMaterialName(materialName);
}

function isLcdModelFallbackMaterialHidden(block, materialName) {
    return (block.lcdSurfaces || []).some(other => isLcdMaterialFamilyMatch(other?.materialName, materialName)
        && !lcdSurfaceForMaterial(block, materialName)
        && lcdReplacementMode(other) !== "model"
        && !isTransparentLcdSurface(other));
}

function isLcdMaterialFamilyMatch(surfaceMaterialName, modelMaterialName) {
    const surface = lcdMaterialFamilyKey(surfaceMaterialName);
    const model = lcdMaterialFamilyKey(modelMaterialName);
    return !!surface && surface === model;
}

function lcdMaterialFamilyKey(materialName) {
    const key = String(materialName || "").trim().toLowerCase();
    const screenArea = /^screenarea(?:90|180|270)?$/.exec(key);
    if (screenArea) return "screenarea";
    return key;
}

function lcdReplacementMode(surface) {
    if (surface && surface.isWorking === false) return "placeholder";
    if (String(surface?.contentType || "").toUpperCase() === "NONE") return surface.emptyOnlineImage ? "placeholder" : "model";
    return "content";
}

function lcdSurfaceKey(surface) {
    const images = (surface.selectedImages || []).map(image => `${image.id || ""}:${image.texturePath || image.spritePath || ""}`).join(",");
    const onlineImage = surface.emptyOnlineImage ? `${surface.emptyOnlineImage.id || ""}:${surface.emptyOnlineImage.texturePath || surface.emptyOnlineImage.spritePath || ""}` : "";
    const offlineImage = surface.emptyOfflineImage ? `${surface.emptyOfflineImage.id || ""}:${surface.emptyOfflineImage.texturePath || surface.emptyOfflineImage.spritePath || ""}` : "";
    const sprites = (surface.sprites || []).map(sprite => `${sprite.index}:${sprite.type}:${sprite.data}:${sprite.texturePath || sprite.spritePath || ""}:${vectorKey(sprite.position)}:${vectorKey(sprite.size)}:${colorKey(sprite.color)}:${sprite.rotationOrScale}`).join(",");
    return [
        surface.contentType,
        surface.isWorking === false ? 0 : 1,
        surface.usesOnlineTextureWhenEmpty ? 1 : 0,
        onlineImage,
        offlineImage,
        surface.currentImageIndex,
        surface.currentlyShownImageId,
        surface.text,
        surface.font,
        surface.fontSize,
        surface.alignment,
        surface.textPadding,
        surface.preserveAspectRatio ? 1 : 0,
        colorKey(surface.backgroundColor),
        surface.backgroundAlpha,
        colorKey(surface.fontColor),
        colorKey(surface.scriptBackgroundColor),
        colorKey(surface.scriptForegroundColor),
        images,
        sprites,
    ].join("|");
}

function vectorKey(vector) {
    if (!vector) return "";
    return `${vector.x ?? vector.X ?? 0},${vector.y ?? vector.Y ?? 0}`;
}

function colorKey(color) {
    if (!color) return "";
    return `${color.r ?? color.R ?? 0},${color.g ?? color.G ?? 0},${color.b ?? color.B ?? 0},${color.a ?? color.A ?? 255}`;
}

function applyLcdSurfaceTexture(material, surface, textureToken) {
    const directPath = lcdDirectPlaceholderPath(surface) || lcdDirectTexturePath(surface);
    if (directPath) {
        loadTrackedTexture({ slot: "LcdTexture", path: directPath }, textureToken).then(texture => {
            material.map = texture;
            material.emissiveMap = texture;
            material.needsUpdate = true;
        }).catch(error => log(`LCD texture fallback retained for ${directPath}: ${error.message}`, true));
        return;
    }

    const canvasTexture = createLcdCanvasTexture(surface, textureToken, material);
    material.map = canvasTexture;
    material.emissiveMap = canvasTexture;
    material.needsUpdate = true;
}

function lcdDirectPlaceholderPath(surface) {
    if (lcdReplacementMode(surface) !== "placeholder") return "";
    const image = surface.isWorking === false ? surface.emptyOfflineImage : surface.emptyOnlineImage;
    return image && (image.spritePath || image.texturePath) || "";
}

function lcdDirectTexturePath(surface) {
    if (lcdReplacementMode(surface) !== "content") return "";
    const contentType = String(surface.contentType || "").toUpperCase();
    const hasText = !!String(surface.text || "");
    const hasSprites = (surface.sprites || []).length > 0;
    if (contentType !== "TEXT_AND_IMAGE" || hasText || hasSprites || surface.preserveAspectRatio || !isLcdBackgroundBlack(surface)) return "";

    const image = currentLcdImage(surface);
    return image && (image.spritePath || image.texturePath) || "";
}

function currentLcdImage(surface) {
    const images = surface.selectedImages || [];
    if (!images.length) return null;
    const shown = String(surface.currentlyShownImageId || "").toLowerCase();
    if (shown) {
        const byId = images.find(image => String(image.id || "").toLowerCase() === shown);
        if (byId) return byId;
    }

    const index = Math.max(0, Math.min(images.length - 1, Number(surface.currentImageIndex) || 0));
    return images[index];
}

function createLcdCanvasTexture(surface, textureToken, material) {
    const canvas = document.createElement("canvas");
    canvas.width = clamp(Math.round(Number(surface.textureWidth) || 512), 16, 2048);
    canvas.height = clamp(Math.round(Number(surface.textureHeight) || 512), 16, 2048);
    const layoutCanvas = lcdLayoutCanvas(surface, canvas);
    const texture = new THREE.CanvasTexture(canvas);
    texture.name = `lcd:${surface.materialName || surface.name || "surface"}`;
    texture.flipY = false;
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.wrapS = THREE.ClampToEdgeWrapping;
    texture.wrapT = THREE.ClampToEdgeWrapping;

    const context = { canvas: layoutCanvas, outputCanvas: canvas, ctx: layoutCanvas.getContext("2d"), texture, material, surface, images: new Map() };
    renderLcdCanvas(context);
    loadLcdCanvasImages(context, textureToken);
    loadLcdCanvasFonts(context);
    return texture;
}

function lcdLayoutCanvas(surface, textureCanvas) {
    const width = Number(surface.surfaceWidth) || textureCanvas.width;
    const height = Number(surface.surfaceHeight) || textureCanvas.height;
    if (Math.round(width) === textureCanvas.width && Math.round(height) === textureCanvas.height) return textureCanvas;

    const layoutCanvas = document.createElement("canvas");
    layoutCanvas.width = clamp(Math.round(width), 1, 4096);
    layoutCanvas.height = clamp(Math.round(height), 1, 4096);
    return layoutCanvas;
}

function loadLcdCanvasImages(context, textureToken) {
    for (const path of lcdCanvasTexturePaths(context.surface)) {
        loadTrackedTexture({ slot: "LcdTexture", path, logLabel: context.surface.__quasarLcdDebugLabel, logStage: "canvas-image" }, textureToken).then(texture => {
            context.images.set(path, textureToCanvas(texture, 0, 0, { premultipliedSpriteAlpha: true }));
            renderLcdCanvas(context);
            context.texture.needsUpdate = true;
            context.material.needsUpdate = true;
        }).catch(error => log(`LCD canvas texture skipped for ${path}: ${error.message}`, true));
    }
}

function loadLcdCanvasFonts(context) {
    for (const font of lcdCanvasFontIds(context.surface)) {
        loadLcdBitmapFont(font).then(() => {
            renderLcdCanvas(context);
            context.texture.needsUpdate = true;
            context.material.needsUpdate = true;
        }).catch(error => log(`LCD font fallback retained for ${font}: ${error.message}`, true));
    }
}

function lcdCanvasFontIds(surface) {
    const fonts = [];
    if (String(surface.text || "")) fonts.push(supportedLcdFontId(surface.font));
    for (const sprite of surface.sprites || []) {
        if (String(sprite.type || "").toUpperCase() === "TEXT" && String(sprite.data || "")) fonts.push(supportedLcdFontId(sprite.fontId));
    }
    return [...new Set(fonts)];
}

function lcdCanvasTexturePaths(surface) {
    const paths = [];
    const image = currentLcdImage(surface);
    if (image && (image.spritePath || image.texturePath)) paths.push(image.spritePath || image.texturePath);
    for (const sprite of surface.sprites || []) {
        const path = sprite.spritePath || sprite.texturePath;
        if (path) paths.push(path);
    }
    return [...new Set(paths)];
}

function renderLcdCanvas(context) {
    const { canvas, outputCanvas, ctx, surface } = context;
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.globalCompositeOperation = "source-over";
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = cssColor(surface.backgroundColor, lcdCanvasBackgroundAlpha(surface));
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    const image = currentLcdImage(surface);
    if (image && (image.spritePath || image.texturePath)) drawLcdTexture(context, image.spritePath || image.texturePath, { x: canvas.width / 2, y: canvas.height / 2 }, { x: canvas.width, y: canvas.height }, "CENTER", 0, surface.preserveAspectRatio);

    for (const sprite of sortedLcdSprites(surface.sprites || [])) drawLcdSprite(context, sprite);
    if (String(surface.text || "")) drawLcdText(context, surface.text, surface.font, surface.fontColor, surface.fontSize, surface.alignment, surface.textPadding, null, null, true);
    ctx.restore();
    if (outputCanvas !== canvas) {
        const outputContext = outputCanvas.getContext("2d");
        outputContext.save();
        outputContext.setTransform(1, 0, 0, 1, 0, 0);
        outputContext.clearRect(0, 0, outputCanvas.width, outputCanvas.height);
        outputContext.drawImage(canvas, 0, 0, outputCanvas.width, outputCanvas.height);
        outputContext.restore();
    }
    context.texture.needsUpdate = true;
}

function sortedLcdSprites(sprites) {
    return [...sprites].sort((left, right) => (Number(left.index) || 0) - (Number(right.index) || 0));
}

function drawLcdSprite(context, sprite) {
    const type = String(sprite.type || "").toUpperCase();
    if (type === "TEXTURE") {
        const path = sprite.spritePath || sprite.texturePath;
        if (!path) return;
        drawLcdTexture(context, path, sprite.position, sprite.size, sprite.alignment, Number(sprite.rotationOrScale) || 0, false, sprite.color);
    } else if (type === "TEXT") {
        drawLcdText(context, sprite.data || "", sprite.fontId, sprite.color, sprite.rotationOrScale, sprite.alignment, 0, sprite.position, sprite.size, false);
    }
}

function drawLcdTexture(context, path, position, size, alignment = "CENTER", rotation = 0, preserveAspect = false, color = null) {
    const image = context.images.get(path);
    if (!image) return;

    const ctx = context.ctx;
    const targetSize = normalizeCanvasVector(size, { x: context.canvas.width, y: context.canvas.height });
    const targetPosition = normalizeCanvasVector(position, { x: context.canvas.width / 2, y: context.canvas.height / 2 });
    const aligned = alignCanvasPosition(targetPosition, targetSize, alignment);
    let width = targetSize.x;
    let height = targetSize.y;
    let x = aligned.x;
    let y = aligned.y;
    if (preserveAspect && image.width && image.height) {
        const scale = Math.min(width / image.width, height / image.height);
        width = image.width * scale;
        height = image.height * scale;
        x = targetPosition.x - width / 2;
        y = targetPosition.y - height / 2;
    }

    ctx.save();
    ctx.translate(x + width / 2, y + height / 2);
    if (rotation) ctx.rotate(rotation);
    ctx.globalAlpha = normalizedColor(color).a;
    ctx.drawImage(image, -width / 2, -height / 2, width, height);
    ctx.restore();
}

function drawLcdText(context, text, font, color, scale, alignment = "LEFT", paddingPercent = 0, position = null, size = null, useSurfaceFontScale = false) {
    const ctx = context.ctx;
    const canvas = context.canvas;
    const normalized = normalizedColor(color || { r: 255, g: 255, b: 255, a: 255 });
    const padding = lcdTextPadding(canvas, paddingPercent, useSurfaceFontScale);
    const boxSize = normalizeCanvasVector(size, { x: canvas.width - padding.x * 2, y: canvas.height - padding.y * 2 });
    const boxPosition = normalizeCanvasVector(position, { x: canvas.width / 2, y: padding.y });
    const align = String(alignment || "LEFT").toUpperCase();

    const bitmapFont = getLoadedLcdBitmapFont(font);
    if (bitmapFont) {
        const topLeft = lcdTextTopLeft(boxPosition, boxSize, align, position, useSurfaceFontScale, padding);
        drawLcdBitmapText(
            ctx,
            bitmapFont,
            text,
            color,
            lcdBitmapTextScale(scale || 1, context.outputCanvas || canvas, useSurfaceFontScale),
            topLeft.x,
            topLeft.y,
            boxSize.x,
            align);
        return;
    }

    const scaleCanvas = context.outputCanvas || canvas;
    const fontSize = Math.max(8, Math.min(180, (Number(scale) || 1) * Math.min(scaleCanvas.width, scaleCanvas.height) / 16));
    ctx.save();
    ctx.font = `${fontSize}px ${lcdCanvasFont(font)}`;
    const lines = splitLcdTextLines(String(text || ""));

    ctx.fillStyle = `rgba(${normalized.r}, ${normalized.g}, ${normalized.b}, ${normalized.a})`;
    ctx.textBaseline = "top";
    ctx.textAlign = align === "RIGHT" ? "right" : align === "CENTER" ? "center" : "left";
    const x = align === "RIGHT" ? boxPosition.x + boxSize.x / 2 : align === "CENTER" ? boxPosition.x : boxPosition.x - boxSize.x / 2;
    let y = position ? boxPosition.y : padding.y;
    for (const line of lines) {
        ctx.fillText(line, x, y);
        y += fontSize * 1.22;
        if (y > canvas.height) break;
    }
    ctx.restore();
}

function lcdTextPadding(canvas, paddingPercent, useSurfaceFontScale) {
    if (!useSurfaceFontScale) return { x: 0, y: 0 };
    const padding = Math.max(0, Number(paddingPercent) || 0) * 0.01;
    return { x: canvas.width * padding, y: canvas.height * padding };
}

function lcdTextTopLeft(position, size, alignment, hasExplicitPosition, useSurfaceFontScale, padding) {
    if (useSurfaceFontScale) return { x: padding.x, y: padding.y };
    const align = String(alignment || "LEFT").toUpperCase();
    if (!hasExplicitPosition) return { x: position.x - size.x / 2, y: position.y - size.y / 2 };
    if (align === "RIGHT") return { x: position.x - size.x, y: position.y };
    if (align === "CENTER") return { x: position.x - size.x / 2, y: position.y };
    return { x: position.x, y: position.y };
}

function splitLcdTextLines(text) {
    const lines = text.replace(/\r\n/g, "\n").split("\n");
    return lines.length ? lines : [""];
}

function lcdCanvasFont(font) {
    const key = String(font || "").toLowerCase();
    if (key.includes("monospace") || key.includes("debug")) return "monospace";
    return "monospace";
}

function normalizeCanvasVector(value, fallback) {
    if (!value) return { ...fallback };
    return { x: Number(value.x ?? value.X) || fallback.x, y: Number(value.y ?? value.Y) || fallback.y };
}

function alignCanvasPosition(position, size, alignment) {
    const align = String(alignment || "CENTER").toUpperCase();
    if (align === "LEFT") return { x: position.x, y: position.y - size.y / 2 };
    if (align === "RIGHT") return { x: position.x - size.x, y: position.y - size.y / 2 };
    return { x: position.x - size.x / 2, y: position.y - size.y / 2 };
}

function cssColor(color, alphaOverride = null) {
    const normalized = normalizedColor(color, alphaOverride);
    return `rgba(${normalized.r}, ${normalized.g}, ${normalized.b}, ${normalized.a})`;
}

function normalizedColor(color, alphaOverride = null) {
    color = color || {};
    const a = alphaOverride == null ? color.a ?? color.A ?? 255 : alphaOverride;
    return {
        r: clamp(Number(color.r ?? color.R ?? 0), 0, 255),
        g: clamp(Number(color.g ?? color.G ?? 0), 0, 255),
        b: clamp(Number(color.b ?? color.B ?? 0), 0, 255),
        a: clamp(Number(a), 0, 255) / 255,
    };
}

function isLcdBackgroundBlack(surface) {
    const color = normalizedColor(surface?.backgroundColor);
    return color.r === 0 && color.g === 0 && color.b === 0;
}

function lcdCanvasBackgroundAlpha(surface) {
    if (isTransparentLcdSurface(surface)) {
        const alpha = surface.backgroundAlpha;
        if (alpha || isLcdBackgroundBlack(surface)) return alpha ?? surface.backgroundColor?.a ?? surface.backgroundColor?.A ?? 255;
        return surface.backgroundColor?.a ?? surface.backgroundColor?.A ?? 255;
    }
    return surface.backgroundColor?.a ?? surface.backgroundColor?.A ?? 255;
}

function isTransparentLcdSurface(surface) {
    return isTransparentScreenAreaMaterialName(`${surface?.materialName || ""} ${surface?.name || ""}`);
}

function normalizedBackgroundAlpha(surface) {
    return clamp(Number(surface?.backgroundAlpha ?? 0), 0, 255) / 255;
}

function transparentLcdSurfaceOpacity(surface) {
    for (const name of [surface?.materialName, surface?.name]) {
        const definition = transparentMaterialDefinitions.get(String(name || "").trim().toLowerCase());
        if (definition && definition.color && Number.isFinite(definition.color.a)) return clamp(definition.color.a, 0, 1);
    }
    return 0.8;
}

async function ensureArmorSkinDefinitionsLoaded() {
    if (!state.contentFolder) {
        armorSkinDefinitionsGeneration = -1;
        armorSkinDefinitionsPromise = null;
        armorSkinDefinitions = new Map();
        return armorSkinDefinitions;
    }

    const generation = getContentFolderCacheGeneration();
    if (armorSkinDefinitionsGeneration === generation && armorSkinDefinitionsPromise) {
        armorSkinDefinitions = await armorSkinDefinitionsPromise;
        return armorSkinDefinitions;
    }

    armorSkinDefinitionsGeneration = generation;
    armorSkinDefinitionsPromise = loadArmorSkinDefinitions();
    armorSkinDefinitions = await armorSkinDefinitionsPromise;
    state.stats["Armor skins loaded"] = armorSkinDefinitions.size;
    updateTimingStats();
    return armorSkinDefinitions;
}

async function loadArmorSkinDefinitions() {
    const start = performance.now();
    try {
        const resolved = await resolveContentFile("Data/AssetModifiers/ArmorModifiers.sbc");
        if (!resolved) return new Map();

        const file = await resolved.getFile();
        return parseArmorSkinDefinitions(await file.text());
    } catch (error) {
        log(`Armor skin definitions unavailable: ${error.message}`, true);
        return new Map();
    } finally {
        addTiming("armorSkinDefinitionRead", performance.now() - start);
    }
}

function parseArmorSkinDefinitions(text) {
    const document = new DOMParser().parseFromString(text, "application/xml");
    const parseError = document.querySelector("parsererror");
    if (parseError) throw new Error(parseError.textContent || "invalid ArmorModifiers.sbc XML");

    const definitions = new Map();
    for (const node of document.querySelectorAll("AssetModifier")) {
        const subtype = assetModifierSubtype(node);
        if (!subtype) continue;

        const definition = {
            metalnessColorable: directChildText(node, "MetalnessColorable").toLowerCase() === "true",
            texturesByLocation: new Map(),
        };
        const textures = directChild(node, "Textures");
        if (!textures) continue;

        for (const texture of directChildren(textures, "Texture")) {
            const location = (texture.getAttribute("Location") || "").trim();
            const slot = skinTextureSlot(texture.getAttribute("Type") || "");
            const filepath = (texture.getAttribute("Filepath") || "").trim().replaceAll("\\", "/");
            if (!location || !slot || !filepath) continue;

            const key = location.toLowerCase();
            const change = definition.texturesByLocation.get(key) || {};
            change[slot] = filepath;
            definition.texturesByLocation.set(key, change);
        }

        definitions.set(subtype.toLowerCase(), definition);
    }
    return definitions;
}

async function ensureTransparentMaterialDefinitionsLoaded() {
    if (!state.contentFolder) {
        transparentMaterialDefinitionsGeneration = -1;
        transparentMaterialDefinitionsPromise = null;
        transparentMaterialDefinitions = new Map();
        return transparentMaterialDefinitions;
    }

    const generation = getContentFolderCacheGeneration();
    if (transparentMaterialDefinitionsGeneration === generation && transparentMaterialDefinitionsPromise) {
        transparentMaterialDefinitions = await transparentMaterialDefinitionsPromise;
        return transparentMaterialDefinitions;
    }

    transparentMaterialDefinitionsGeneration = generation;
    transparentMaterialDefinitionsPromise = loadTransparentMaterialDefinitions();
    transparentMaterialDefinitions = await transparentMaterialDefinitionsPromise;
    state.stats["Transparent materials loaded"] = transparentMaterialDefinitions.size;
    updateTimingStats();
    return transparentMaterialDefinitions;
}

async function loadTransparentMaterialDefinitions() {
    const start = performance.now();
    try {
        const resolved = await resolveContentFile("Data/TransparentMaterials.sbc");
        if (!resolved) return new Map();

        const file = await resolved.getFile();
        return parseTransparentMaterialDefinitions(await file.text());
    } catch (error) {
        log(`Transparent material definitions unavailable: ${error.message}`, true);
        return new Map();
    } finally {
        addTiming("transparentMaterialDefinitionRead", performance.now() - start);
    }
}

function parseTransparentMaterialDefinitions(text) {
    const document = new DOMParser().parseFromString(text, "application/xml");
    const parseError = document.querySelector("parsererror");
    if (parseError) throw new Error(parseError.textContent || "invalid TransparentMaterials.sbc XML");

    const definitions = new Map();
    for (const node of document.querySelectorAll("TransparentMaterial")) {
        const subtype = assetModifierSubtype(node);
        if (!subtype) continue;

        const texture = normalizeLogicalTexturePath(directChildText(node, "Texture"));
        const glossTexture = normalizeLogicalTexturePath(directChildText(node, "GlossTexture"));
        definitions.set(subtype.toLowerCase(), {
            subtype,
            texture,
            glossTexture,
            color: parseVector4(directChild(node, "Color"), { r: 1, g: 1, b: 1, a: 0.38 }),
            gloss: num(directChildText(node, "Gloss"), 0.4),
        });
    }
    return definitions;
}

function parseVector4(node, fallback) {
    if (!node) return fallback;
    return {
        r: num(directChildText(node, "X"), fallback.r),
        g: num(directChildText(node, "Y"), fallback.g),
        b: num(directChildText(node, "Z"), fallback.b),
        a: num(directChildText(node, "W"), fallback.a),
    };
}

function normalizeLogicalTexturePath(path) {
    return String(path || "").trim().replaceAll("\\", "/");
}

function transparentMaterialForGroup(group, technique) {
    for (const name of [group.glassCCW, group.glassCW, group.materialName]) {
        const definition = transparentMaterialDefinitions.get(String(name || "").trim().toLowerCase());
        if (definition) return definition;
    }

    return technique.includes("GLASS") ? transparentMaterialDefinitions.get("glass") || null : null;
}

function isTransparentScreenAreaGroup(group) {
    return [group?.materialName, group?.glassCW, group?.glassCCW].some(isTransparentScreenAreaMaterialName);
}

function isTransparentScreenAreaMaterialName(name) {
    return String(name || "").toLowerCase().includes("transparentscreenarea");
}

function transparentMaterialKey(definition) {
    if (!definition) return "none";
    const color = definition.color || {};
    return [definition.subtype, definition.texture, definition.glossTexture, color.r, color.g, color.b, color.a, definition.gloss].join("|");
}

function materialTexturesForGroup(group, skin, transparentMaterial) {
    const textures = { ...(group.textures || {}) };
    if (skin) Object.assign(textures, skin.textures);
    if (transparentMaterial) {
        if (transparentMaterial.texture) textures.GlassTexture = transparentMaterial.texture;
        if (transparentMaterial.glossTexture) textures.GlassGlossTexture = transparentMaterial.glossTexture;
    }
    return textures;
}

function isModelMaterialColorMaskable(group, technique, textures) {
    return !!colorMaskTextureSelection(textures)
        && !technique.includes("GLASS")
        && !isTransparentScreenAreaGroup(group);
}

function assetModifierSubtype(node) {
    const id = directChild(node, "Id");
    if (!id) return "";
    return (id.getAttribute("Subtype") || directChildText(id, "SubtypeId") || "").trim();
}

function directChild(parent, name) {
    return directChildren(parent, name)[0] || null;
}

function directChildText(parent, name) {
    const child = directChild(parent, name);
    return child ? (child.textContent || "").trim() : "";
}

function directChildren(parent, name) {
    return Array.from(parent.children || []).filter(child => child.localName === name);
}

function skinTextureSlot(type) {
    const key = String(type || "").toLowerCase();
    if (key === "colormetal") return "ColorMetalTexture";
    if (key === "normalgloss") return "NormalGlossTexture";
    if (key === "extensions") return "AddMapsTexture";
    if (key === "alphamask") return "AlphaMaskTexture";
    return "";
}

function materialSkinOverride(block, materialName) {
    const skinSubtypeId = blockSkinSubtypeId(block);
    if (!skinSubtypeId || !materialName) return null;

    const definition = armorSkinDefinitions.get(skinSubtypeId.toLowerCase());
    if (!definition) return null;

    const textures = definition.texturesByLocation.get(String(materialName).toLowerCase());
    return textures ? { textures, metalnessColorable: definition.metalnessColorable } : null;
}

function blockSkinSubtypeId(block) {
    return String(block && (block.skinSubtypeId || block.SkinSubtypeId) || "").trim();
}

function modelMaterialRenderMode(technique) {
    const decal = technique.includes("DECAL");
    const cutout = technique.includes("ALPHA_MASKED") || technique.includes("DECAL_CUTOUT");
    const blended = !cutout && (technique.includes("GLASS") || technique.includes("HOLO") || technique.includes("SHIELD"));
    return { cutout, blended, decal };
}

function modelMaterialRenderModeName(renderMode) {
    if (renderMode.blended) return "blended";
    if (renderMode.decal) return renderMode.cutout ? "decal-cutout" : "decal";
    return renderMode.cutout ? "cutout" : "opaque";
}

function modelMaterialSide(technique, renderMode) {
    if (technique.includes("SINGLE_SIDED")) return THREE.FrontSide;
    if (renderMode.blended) return THREE.FrontSide;
    return THREE.DoubleSide;
}

function stableTextureKey(textures) {
    return Object.entries(textures || {})
        .filter(([, path]) => !!path)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([slot, path]) => `${slot}=${path}`)
        .join(";");
}

function applyModelTextures(material, group, technique, renderMode, textureToken, colorMaskable = true) {
    const usesTransparentMaterialTexture = technique.includes("GLASS") || !!group.textures?.GlassTexture || !!group.textures?.TransparentTexture;
    const base = textureSelection(group.textures, usesTransparentMaterialTexture
        ? ["GlassTexture", "TransparentTexture", "ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]
        : ["ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]);
    if (base) {
        loadTrackedTexture(base, textureToken).then(texture => {
            material.map = texture;
            material.color.copy(material.userData.seTransparentMaterialColor || new THREE.Color(0xffffff));
            setSpaceEngineersColorMetalTexture(material, colorMetalTextureSelectionHasMetalness(base));
            material.needsUpdate = true;
        }).catch(error => log(`Texture fallback retained for ${base.path}: ${error.message}`, true));
    }

    const alphaMask = (renderMode.cutout || renderMode.decal) ? alphaMaskTextureSelection(group.textures) : null;
    if (alphaMask) {
        loadTrackedTexture(alphaMask, textureToken).then(texture => {
            material.alphaMap = texture;
            setSpaceEngineersAlphaMaskTexture(material, texture);
            material.needsUpdate = true;
        }).catch(error => log(`Alpha-mask texture fallback retained for ${alphaMask.path}: ${error.message}`, true));
    }

    const colorMask = colorMaskable ? colorMaskTextureSelection(group.textures) : null;
    if (colorMask) {
        loadTrackedTexture(colorMask, textureToken).then(texture => {
            setSpaceEngineersColorMaskTexture(material, texture);
        }).catch(error => log(`Paint mask texture fallback retained for ${colorMask.path}: ${error.message}`, true));
    }

    const normalSlots = usesTransparentMaterialTexture
        ? ["GlassGlossTexture", "NormalGlossTexture", "NormalTexture", "NormalMapTexture"]
        : ["NormalGlossTexture", "NormalTexture", "NormalMapTexture"];
    const normal = textureSelection(group.textures, normalSlots);
    if (normal) {
        loadTrackedTexture(normal, textureToken).then(texture => {
            material.normalMap = texture;
            material.normalScale.set(-1, 1);
            setSpaceEngineersNormalGlossTexture(material, true);
            material.needsUpdate = true;
        }).catch(error => log(`Normal texture fallback retained for ${normal.path}: ${error.message}`, true));
    }
}

function alphaMaskTextureSelection(textures) {
    const entries = Object.entries(textures || {}).filter(([, path]) => !!path);
    for (const preferred of ["AlphaMaskTexture", "AlphamaskTexture", "AlphaTexture"]) {
        const entry = entries.find(([slot]) => slot.toLowerCase() === preferred.toLowerCase());
        if (entry) return { slot: entry[0], path: entry[1] };
    }

    for (const [slot, path] of entries) {
        const text = `${slot || ""} ${path || ""}`.toLowerCase();
        if (text.includes("alphamask") || text.includes("alpha_mask") || text.includes("alpha-mask")) return { slot, path };
    }
    return null;
}

function colorMaskTextureSelection(textures) {
    const entries = Object.entries(textures || {}).filter(([, path]) => !!path);
    for (const preferred of ["AddMapsTexture", "ExtensionTexture", "ExtensionsTexture", "ExtTexture"]) {
        const entry = entries.find(([slot]) => slot.toLowerCase() === preferred.toLowerCase());
        if (entry) return { slot: entry[0], path: entry[1] };
    }

    for (const [slot, path] of entries) {
        const text = `${slot || ""} ${path || ""}`.toLowerCase();
        if (text.includes("alphamask")) continue;
        if (text.includes("addmaps") || text.includes("extension") || /_(add)\./i.test(text)) return { slot, path };
    }
    return null;
}

function loadTrackedTexture(selection, textureToken) {
    const key = textureAssetKey(selection.path);
    return loadTexture(selection.path, selection.slot).then(texture => {
        recordTextureLoadStatus(key, "loaded", textureToken);
        return texture;
    }).catch(error => {
        recordTextureLoadStatus(key, error && error.isMissingLocalTexture ? "missing" : "failed", textureToken);
        throw error;
    });
}

function colorMetalTextureSelectionHasMetalness(selection) {
    const text = `${selection && selection.slot || ""} ${selection && selection.path || ""}`.toLowerCase();
    return text.includes("colormetal") || /_cm\./i.test(text);
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

function applySpaceEngineersColorMasking(material, metalnessColorable, colorMaskable = true) {
    material.userData.seColorMaskUniforms = {
        seColorMaskMap: { value: fallbackWhiteTexture() },
        seUseColorMaskMap: { value: false },
        seColorMaskRedChannel: { value: 0 },
        seBlockColorMask: { value: new THREE.Vector3(0, -1, 0) },
        seApplyColorMask: { value: !!colorMaskable },
        seMetalnessColorable: { value: !!metalnessColorable },
        seUseColorMetalAlpha: { value: false },
        seUseNormalGlossAlpha: { value: false },
        seAlphaMaskMap: { value: fallbackWhiteTexture() },
        seUseAlphaMaskMap: { value: false },
        seAlphaMaskRedChannel: { value: 0 },
        seAlphaMaskCutoff: { value: 0.5 },
    };
    material.onBeforeCompile = shader => {
        Object.assign(shader.uniforms, material.userData.seColorMaskUniforms);
        shader.fragmentShader = shader.fragmentShader.replace("#include <color_pars_fragment>", `#include <color_pars_fragment>
uniform sampler2D seColorMaskMap;
uniform bool seUseColorMaskMap;
uniform float seColorMaskRedChannel;
uniform vec3 seBlockColorMask;
uniform bool seApplyColorMask;
uniform bool seMetalnessColorable;
uniform bool seUseColorMetalAlpha;
uniform bool seUseNormalGlossAlpha;
uniform sampler2D seAlphaMaskMap;
uniform bool seUseAlphaMaskMap;
uniform float seAlphaMaskRedChannel;
uniform float seAlphaMaskCutoff;

vec3 seHsvToRgb(vec3 hsv) {
  vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
  vec3 p = abs(fract(hsv.xxx + K.xyz) * 6.0 - K.www);
  return hsv.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), hsv.y);
}

vec3 seRgbToHsv(vec3 rgb) {
  vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
  vec4 p = mix(vec4(rgb.bg, K.wz), vec4(rgb.gb, K.xy), step(rgb.b, rgb.g));
  vec4 q = mix(vec4(p.xyw, rgb.r), vec4(rgb.r, p.yzx), step(p.x, rgb.r));
  float d = q.x - min(q.w, q.y);
  float e = 1.0e-10;
  return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 seRgbToSrgb(vec3 rgb) {
  return mix(rgb * 12.92, pow(abs(rgb), vec3(1.0 / 2.4)) * 1.055 - 0.055, step(vec3(0.0031308), rgb));
}

vec3 seSrgbToRgb(vec3 srgb) {
  return mix(srgb / 12.92, pow((abs(srgb) + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), srgb));
}

vec3 seColorizeGray(vec3 texel, vec3 hsvMask, float coloringFactor) {
  if (coloringFactor <= 0.0) return texel;
  hsvMask += vec3(0.0, 0.8, -0.1);
  vec3 hsv = seRgbToHsv(seRgbToSrgb(max(texel, vec3(0.0))));
  hsv.xy = vec2(0.0);
  vec3 finalHsv = clamp(hsv + hsvMask, 0.0, 1.0);
  return mix(texel, seSrgbToRgb(seHsvToRgb(finalHsv)), clamp(coloringFactor, 0.0, 1.0));
}

float seFallbackColoringFactor(vec3 texel) {
  vec3 srgb = seRgbToSrgb(max(texel, vec3(0.0)));
  vec3 hsv = seRgbToHsv(srgb);
  float gray = 1.0 - smoothstep(0.08, 0.28, hsv.y);
  float dark = smoothstep(0.03, 0.18, hsv.z);
  return gray * dark;
}

float seRemoveMetalnessFromColoring(float metalness, float coloring) {
  float threshold = 0.4;
  float thresholdMultiply = 0.5;
  return coloring * clamp(1.0 - clamp(metalness - threshold, 0.0, 1.0) / ((1.0 - threshold) * thresholdMultiply), 0.0, 1.0);
}`);
        shader.fragmentShader = shader.fragmentShader.replace("#include <map_fragment>", `#include <map_fragment>
#ifdef USE_MAP
  if (seUseColorMetalAlpha) diffuseColor.a = opacity;
#endif`);
        shader.fragmentShader = shader.fragmentShader.replace("#include <alphatest_fragment>", `#ifdef USE_ALPHAMAP
  if (seUseAlphaMaskMap) {
    vec4 seAlphaMaskTexel = texture2D(seAlphaMaskMap, vAlphaMapUv);
    float seAlphaMask = mix(seAlphaMaskTexel.a, seAlphaMaskTexel.r, seAlphaMaskRedChannel);
    if (seAlphaMask < seAlphaMaskCutoff) discard;
  }
#endif
#include <alphatest_fragment>`);
        shader.fragmentShader = shader.fragmentShader.replace("#include <roughnessmap_fragment>", `#include <roughnessmap_fragment>
#ifdef USE_NORMALMAP
  if (seUseNormalGlossAlpha) {
    float seGloss = texture2D(normalMap, vNormalMapUv).a;
    roughnessFactor = clamp(1.0 - seGloss, 0.0, 1.0);
  }
#endif`);
        shader.fragmentShader = shader.fragmentShader.replace("#include <metalnessmap_fragment>", `#include <metalnessmap_fragment>
#ifdef USE_MAP
  if (seUseColorMetalAlpha) metalnessFactor = clamp(sampledDiffuseColor.a, 0.0, 1.0);
#endif`);
        shader.fragmentShader = shader.fragmentShader.replace("#include <color_fragment>", `if (seApplyColorMask) {
    vec3 sePaintMask = seBlockColorMask;
#if defined( USE_COLOR ) || defined( USE_INSTANCING_COLOR ) || defined( USE_BATCHING_COLOR )
    sePaintMask = vColor.rgb;
#endif
#ifdef USE_MAP
    vec4 seMaskTexel = texture2D(seColorMaskMap, vMapUv);
    float seMaskFactor = mix(seMaskTexel.a, seMaskTexel.r, seColorMaskRedChannel);
    float seColoringFactor = seUseColorMaskMap ? seMaskFactor : seFallbackColoringFactor(diffuseColor.rgb);
    float seMetalness = seUseColorMetalAlpha ? sampledDiffuseColor.a : 0.0;
    if (!seMetalnessColorable) seColoringFactor = seRemoveMetalnessFromColoring(seMetalness, seColoringFactor);
    diffuseColor.rgb = seColorizeGray(diffuseColor.rgb, sePaintMask, seColoringFactor);
#else
    diffuseColor.rgb = seColorizeGray(diffuseColor.rgb, sePaintMask, 1.0);
#endif
}`);
    };
    material.customProgramCacheKey = () => "se-grid-viewer-color-mask-v4";
}

function applyDefaultBlockColorMaskUniforms(renderer, scene, camera, geometry, material) {
    const uniforms = material && material.userData && material.userData.seColorMaskUniforms;
    if (!uniforms) return;
    uniforms.seBlockColorMask.value.set(0, -1, 0);
}

function setSpaceEngineersColorMetalTexture(material, enabled) {
    const uniforms = material.userData.seColorMaskUniforms;
    if (uniforms) uniforms.seUseColorMetalAlpha.value = !!enabled;
}

function setSpaceEngineersNormalGlossTexture(material, enabled) {
    const uniforms = material.userData.seColorMaskUniforms;
    if (uniforms) uniforms.seUseNormalGlossAlpha.value = !!enabled;
}

function setSpaceEngineersColorMaskTexture(material, texture) {
    const uniforms = material.userData.seColorMaskUniforms;
    if (!uniforms) return;
    uniforms.seColorMaskMap.value = texture;
    uniforms.seUseColorMaskMap.value = true;
    uniforms.seColorMaskRedChannel.value = textureUsesRedChannel(texture) ? 1 : 0;
}

function setSpaceEngineersAlphaMaskTexture(material, texture) {
    const uniforms = material.userData.seColorMaskUniforms;
    if (!uniforms) return;
    uniforms.seAlphaMaskMap.value = texture;
    uniforms.seUseAlphaMaskMap.value = true;
    uniforms.seAlphaMaskRedChannel.value = alphaMaskTextureUsesRedChannel(texture) ? 1 : 0;
}

function textureUsesRedChannel(texture) {
    return texture && (texture.format === THREE.RED_RGTC1_Format || texture.format === THREE.SIGNED_RED_RGTC1_Format || texture.userData && texture.userData.seColorMaskChannel === "r");
}

function alphaMaskTextureUsesRedChannel(texture) {
    if (!texture) return false;
    if (texture.userData && texture.userData.seColorMaskChannel) return texture.userData.seColorMaskChannel === "r";
    if (texture.format === THREE.RED_RGTC1_Format || texture.format === THREE.SIGNED_RED_RGTC1_Format) return true;
    return !texture.isCompressedTexture;
}

function fallbackWhiteTexture() {
    const key = "generated:white-1x1";
    if (state.textureCache.has(key)) return state.textureCache.get(key);
    const texture = new THREE.DataTexture(new Uint8Array([255, 255, 255, 255]), 1, 1, THREE.RGBAFormat);
    texture.needsUpdate = true;
    state.textureCache.set(key, texture);
    return texture;
}

function colorMaskForBlock(block) {
    const hsv = block && (block.colourMaskHsv || block.colorMaskHsv || block.ColourMaskHsv || block.ColorMaskHsv);
    if (hsv && Number.isFinite(Number(hsv.x ?? hsv.X))) {
        return {
            x: num(hsv.x ?? hsv.X, 0),
            y: num(hsv.y ?? hsv.Y, -1),
            z: num(hsv.z ?? hsv.Z, 0),
        };
    }
    return { x: 0, y: -1, z: 0 };
}

function displayColorForBlock(block) {
    const hsv = colorMaskForBlock(block);
    return hsvToRgbColor(positiveModulo(hsv.x, 1), clamp(hsv.y + 0.8, 0, 1), clamp(hsv.z + 0.45, 0, 1));
}

function hsvToRgbColor(hue, saturation, value) {
    const chroma = value * saturation;
    const segment = hue * 6;
    const x = chroma * (1 - Math.abs(segment % 2 - 1));
    let r = 0;
    let g = 0;
    let b = 0;

    if (segment < 1) {
        r = chroma;
        g = x;
    } else if (segment < 2) {
        r = x;
        g = chroma;
    } else if (segment < 3) {
        g = chroma;
        b = x;
    } else if (segment < 4) {
        g = x;
        b = chroma;
    } else if (segment < 5) {
        r = x;
        b = chroma;
    } else {
        r = chroma;
        b = x;
    }

    const m = value - chroma;
    return new THREE.Color(r + m, g + m, b + m);
}

function positiveModulo(value, divisor) {
    return ((value % divisor) + divisor) % divisor;
}

function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}

function queueBlockProxy(proxyBatches, block, definition, box) {
    const opacity = proxyOpacity(definition);
    const key = String(opacity);
    let batch = proxyBatches.get(key);
    if (!batch) {
        batch = { opacity, instances: [] };
        proxyBatches.set(key, batch);
    }

    const size = new THREE.Vector3();
    const center = new THREE.Vector3();
    box.getSize(size);
    box.getCenter(center);
    batch.instances.push({
        block,
        center,
        size: new THREE.Vector3(Math.max(size.x, 0.05), Math.max(size.y, 0.05), Math.max(size.z, 0.05)),
        color: displayColorForBlock(block),
    });
}

function flushProxyBatches(layer, proxyBatches) {
    if (!proxyBatches.size) return;
    const geometry = new THREE.BoxGeometry(1, 1, 1);
    const matrix = new THREE.Matrix4();
    const rotation = new THREE.Quaternion();

    for (const batch of proxyBatches.values()) {
        const solid = createProxyBatchMesh(geometry, batch);
        const edges = createProxyEdgeBatch(batch);
        layer.add(solid, edges);

        for (let i = 0; i < batch.instances.length; i++) {
            const instance = batch.instances[i];
            matrix.compose(instance.center, rotation, instance.size);
            solid.setMatrixAt(i, matrix);
            solid.setColorAt(i, instance.color);
            solid.userData.blocks.push(instance.block);
        }

        solid.instanceMatrix.needsUpdate = true;
        if (solid.instanceColor) solid.instanceColor.needsUpdate = true;
        if (typeof solid.computeBoundingBox === "function") solid.computeBoundingBox();
        if (typeof solid.computeBoundingSphere === "function") solid.computeBoundingSphere();
    }
}

function createProxyBatchMesh(geometry, batch) {
    const material = new THREE.MeshStandardMaterial({
        color: 0xffffff,
        roughness: 0.78,
        metalness: 0.12,
        transparent: batch.opacity < 1,
        opacity: batch.opacity,
    });
    const mesh = new THREE.InstancedMesh(geometry, material, batch.instances.length);
    mesh.name = `ProxyBatch:${batch.opacity}`;
    mesh.matrixAutoUpdate = false;
    mesh.instanceMatrix.setUsage(THREE.StaticDrawUsage);
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    mesh.userData.blocks = [];
    return mesh;
}

function createProxyEdgeBatch(batch) {
    const geometry = createProxyEdgeGeometry(batch.instances);
    const material = new THREE.LineBasicMaterial({ color: 0x93c5fd, transparent: true, opacity: 0.75 });
    const edges = new THREE.LineSegments(geometry, material);
    edges.name = `ProxyEdgeBatch:${batch.opacity}`;
    edges.matrixAutoUpdate = false;
    return edges;
}

function createProxyEdgeGeometry(instances) {
    const positions = new Float32Array(instances.length * 12 * 2 * 3);
    let offset = 0;
    for (const instance of instances) offset = writeProxyEdges(positions, offset, instance.center, instance.size);

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    geometry.computeBoundingSphere();
    return geometry;
}

function writeProxyEdges(positions, offset, center, size) {
    const hx = size.x / 2;
    const hy = size.y / 2;
    const hz = size.z / 2;
    const x0 = center.x - hx;
    const x1 = center.x + hx;
    const y0 = center.y - hy;
    const y1 = center.y + hy;
    const z0 = center.z - hz;
    const z1 = center.z + hz;

    offset = writeProxyEdge(positions, offset, x0, y0, z0, x1, y0, z0);
    offset = writeProxyEdge(positions, offset, x1, y0, z0, x1, y1, z0);
    offset = writeProxyEdge(positions, offset, x1, y1, z0, x0, y1, z0);
    offset = writeProxyEdge(positions, offset, x0, y1, z0, x0, y0, z0);
    offset = writeProxyEdge(positions, offset, x0, y0, z1, x1, y0, z1);
    offset = writeProxyEdge(positions, offset, x1, y0, z1, x1, y1, z1);
    offset = writeProxyEdge(positions, offset, x1, y1, z1, x0, y1, z1);
    offset = writeProxyEdge(positions, offset, x0, y1, z1, x0, y0, z1);
    offset = writeProxyEdge(positions, offset, x0, y0, z0, x0, y0, z1);
    offset = writeProxyEdge(positions, offset, x1, y0, z0, x1, y0, z1);
    offset = writeProxyEdge(positions, offset, x1, y1, z0, x1, y1, z1);
    return writeProxyEdge(positions, offset, x0, y1, z0, x0, y1, z1);
}

function writeProxyEdge(positions, offset, x0, y0, z0, x1, y1, z1) {
    positions[offset++] = x0;
    positions[offset++] = y0;
    positions[offset++] = z0;
    positions[offset++] = x1;
    positions[offset++] = y1;
    positions[offset++] = z1;
    return offset;
}

function proxyOpacity(definition) {
    return definition && definition.visibilityClass === "transparent" ? 0.36 : 0.72;
}

async function resolveReferencedModelsProgressively(scene, modelAssets, stats, progress, renderToken) {
    if (!state.contentFolder) {
        log("No local Content folder selected; all models render as proxies.", true);
        stats.missing = (scene.modelAssets || []).length;
        updateModelStats(stats, progress.lastRenderStats, modelAssets.size);
        return stats;
    }

    await runWithConcurrency([...modelAssets.values()], MAX_CONCURRENT_MODEL_RESOLVES, async asset => {
        const result = await resolveModelAsset(asset);
        if (renderToken !== modelRenderToken) return;

        state.modelResolution.set(asset.assetId, result);
        if (result.status === "missing") {
            stats.missing++;
            log(result.message, true);
        } else {
            stats.found++;
            if (result.status === "parsed") stats.parsed++;
            if (result.status === "proxy") log(result.message, true);
        }

        updateModelStats(stats, progress.lastRenderStats, modelAssets.size);
        progress.scheduleRebuild();
    });
    return stats;
}

async function runWithConcurrency(items, limit, operation) {
    const results = new Array(items.length);
    let nextIndex = 0;

    async function worker() {
        while (nextIndex < items.length) {
            const index = nextIndex++;
            results[index] = await operation(items[index], index);
        }
    }

    const workers = [];
    for (let i = 0; i < Math.min(limit, items.length); i++) workers.push(worker());
    await Promise.all(workers);
    return results;
}

function collectReferencedTextureAssets(scene) {
    const assets = new Map();
    for (const asset of scene.textureAssets || []) {
        addTextureAsset(assets, asset.logicalPath, asset.usage || asset.Usage || "metadata");
    }

    for (const resolved of state.modelResolution.values()) {
        const model = resolved && resolved.status === "parsed" ? resolved.model : null;
        if (!model) continue;

        for (const group of model.groups || []) {
            for (const [slot, path] of Object.entries(group.textures || {})) {
                addTextureAsset(assets, path, slot || "material");
            }
        }
    }
    return assets;
}

function countLcdSurfaces(scene) {
    let count = 0;
    for (const block of scene.blockInstances || []) count += (block.lcdSurfaces || []).length;
    return count;
}

function addTextureAsset(assets, logicalPath, usage) {
    const key = textureAssetKey(logicalPath);
    if (!key || assets.has(key)) return;
    assets.set(key, {
        assetId: `texture:${key}`,
        logicalPath: String(logicalPath || "").trim().replaceAll("\\", "/"),
        usage: usage || "unknown",
    });
}

function initializeTextureStats(textureAssets) {
    state.textureResolution.clear();
    state.textureStats = { listed: textureAssets.size, found: 0, loaded: 0, missing: 0, failed: 0 };
    for (const [key, asset] of textureAssets) {
        state.textureResolution.set(key, { asset, localStatus: state.contentFolder ? "pending" : "missing", loadStatus: "pending" });
    }
    if (!state.contentFolder) state.textureStats.missing = textureAssets.size;
    return state.textureStats;
}

function recordTextureLoadStatus(key, loadStatus, textureToken) {
    if (textureToken !== textureStatsToken) return;

    let entry = state.textureResolution.get(key);
    if (!entry) {
        entry = { asset: { logicalPath: key }, localStatus: "found", loadStatus: "pending" };
        state.textureResolution.set(key, entry);
        state.textureStats.listed++;
        state.textureStats.found++;
    }

    if (entry.loadStatus === "loaded") return;
    if (entry.loadStatus === "failed") state.textureStats.failed = Math.max(0, state.textureStats.failed - 1);
    if (entry.localStatus === "missing") state.textureStats.missing = Math.max(0, state.textureStats.missing - 1);
    if (entry.localStatus !== "found" && loadStatus !== "missing") {
        entry.localStatus = "found";
        state.textureStats.found++;
    }

    if (loadStatus === "loaded") {
        state.textureStats.loaded++;
    } else if (loadStatus === "missing") {
        entry.localStatus = "missing";
        state.textureStats.missing++;
    } else {
        state.textureStats.failed++;
    }

    entry.loadStatus = loadStatus;
    updateTextureStats();
}

function updateTextureStats() {
    state.stats["Textures listed"] = state.textureStats.listed;
    state.stats["Textures found locally"] = state.textureStats.found;
    state.stats["Textures loaded"] = state.textureStats.loaded;
    state.stats["Textures missing"] = state.textureStats.missing;
    state.stats["Textures failed"] = state.textureStats.failed;
    updateTimingStats();
}

function updateModelStats(resolutionStats, renderStats, listed) {
    state.stats.Blocks = (state.lastScene && state.lastScene.blockInstances || []).length;
    updateModelRenderStats(renderStats);
    state.stats["Models listed"] = listed;
    state.stats["Models found locally"] = resolutionStats.found;
    state.stats["Models parsed"] = resolutionStats.parsed;
    state.stats["Models missing"] = resolutionStats.missing;
    updateTimingStats();
}

function updateModelRenderStats(renderStats) {
    state.stats["Model meshes"] = renderStats.modelMeshes;
    state.stats["Proxy meshes"] = renderStats.proxyMeshes;
    state.stats["Proxy batches"] = renderStats.proxyBatches;
    state.stats["Model batches"] = renderStats.modelBatches;
    state.stats["Shared geometries"] = renderStats.sharedGeometries;
    state.stats["Shared materials"] = renderStats.sharedMaterials;
}

function updateGridLightStats(lightSources) {
    const sources = lightSources || [];
    state.stats["Grid light sources"] = sources.length;
    state.stats["Active grid lights"] = sources.filter(source => source && source.enabled !== false && num(source.intensity, 0) > 0).length;
    state.stats["Viewer grid lights"] = state.gridLights.length;
    state.stats["Point lights"] = state.gridLights.filter(light => light.isPointLight).length;
    state.stats["Spot lights"] = state.gridLights.filter(light => light.isSpotLight).length;
    state.stats["Capped grid lights"] = Math.max(0, sources.length - state.gridLights.length);
}

function updateSummaryModelStats(resolutionStats) {
    const entries = els.sceneSummary && Array.from(els.sceneSummary.children) || [];
    for (let i = 0; i < entries.length - 1; i += 2) {
        const label = entries[i].textContent;
        if (label === "Found") entries[i + 1].textContent = resolutionStats.found.toLocaleString();
        else if (label === "Parsed") entries[i + 1].textContent = resolutionStats.parsed.toLocaleString();
        else if (label === "Missing") entries[i + 1].textContent = resolutionStats.missing.toLocaleString();
    }
}

function updateTimingStats() {
    for (const [key, metric] of Object.entries(state.timings || {})) {
        const label = timingLabel(key);
        state.stats[`${label} total ms`] = Math.round(metric.totalMs);
        state.stats[`${label} max ms`] = Math.round(metric.maxMs);
        state.stats[`${label} count`] = metric.count;
    }
}

function addTiming(key, durationMs) {
    const metric = state.timings[key] || { count: 0, totalMs: 0, maxMs: 0 };
    metric.count++;
    metric.totalMs += durationMs;
    metric.maxMs = Math.max(metric.maxMs, durationMs);
    state.timings[key] = metric;
}

function timingLabel(key) {
    return key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, value => value.toUpperCase());
}

function textureAssetKey(logicalPath) {
    const value = String(logicalPath || "").trim().replaceAll("\\", "/");
    return value ? value.toLowerCase() : "";
}

function renderSummary(scene, resolutionStats, textureStats) {
    els.sceneSummary.innerHTML = "";
    addSummary("Grid", scene.grid && scene.grid.displayName);
    addSummary("Entity", scene.grid && scene.grid.id);
    addSummary("Blocks", (scene.blockInstances || []).length.toLocaleString());
    addSummary("Models", (scene.modelAssets || []).length.toLocaleString());
    addSummary("Found", resolutionStats.found.toLocaleString());
    addSummary("Parsed", resolutionStats.parsed.toLocaleString());
    addSummary("Missing", resolutionStats.missing.toLocaleString());
    addSummary("Textures", textureStats.listed.toLocaleString());
    addSummary("LCDs", countLcdSurfaces(scene).toLocaleString());
    addSummary("Voxels", (scene.voxels || []).length.toLocaleString());
    if (scene.environment && scene.environment.sunDirection) {
        const direction = scene.environment.sunDirection;
        addSummary("Sun", `${formatNumber(direction.x)}, ${formatNumber(direction.y)}, ${formatNumber(direction.z)}`);
    }
    if (scene.warnings && scene.warnings.length) {
        for (const warning of scene.warnings) log(warning, true);
    }
}

function formatNumber(value) {
    return Number(value || 0).toFixed(2);
}

function addSummary(label, value) {
    const dt = document.createElement("dt");
    const dd = document.createElement("dd");
    dt.textContent = label;
    dd.textContent = value || "-";
    els.sceneSummary.append(dt, dd);
}
