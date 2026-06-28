import * as THREE from "three";
import { els, state } from "./state.js";
import { blockBox, boundsToBox3 } from "./geometry.js";
import { colorFromHash, matrixDtoToThree, num, vec3 } from "./math.js";
import { ASTEROID_GRID_CUBE_SIZE, LARGE_GRID_CUBE_SIZE, disposeObjectTree, fitCameraToScene, floorGridLayout, updateLighting, updateSceneBounds, updateSunLightPosition } from "./scene.js";
import { resolveModelAsset } from "./mwm-loader.js";
import { loadTexture, textureToCanvas } from "./texture-loader.js";
import { log } from "./logging.js";
import { getContentFolderCacheGeneration, resolveContentFile, setSceneModRoots } from "./content-folder.js";
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
const MAX_PROJECTED_LIGHT_SHADOWS = 12;
const PROJECTED_LIGHT_SHADOW_MAP_SIZE = 1024;
const PROJECTED_LIGHT_SHADOW_NEAR = 0.05;
const PROJECTED_LIGHT_SHADOW_BIAS = -0.0001;
const PROJECTED_LIGHT_SHADOW_NORMAL_BIAS = 0.025;

export async function renderGridScene(scene) {
    const renderToken = ++modelRenderToken;
    state.lastScene = scene;
    state.stats = {};
    await setSceneModRoots(scene.mods || scene.Mods || []);
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
    group.name = "QuasarGrids";
    group.matrixAutoUpdate = false;
    group.matrix.identity();
    state.gridGroup = group;
    state.scene.add(group);
    const gridGroups = buildGridGroups(scene, group);
    buildGridLightGroups(scene, gridGroups);

    const definitions = new Map((scene.blockDefinitions || []).map(definition => [definition.id, definition]));
    const modelAssets = new Map((scene.modelAssets || []).map(asset => [asset.assetId, asset]));
    const renderTextureToken = ++textureStatsToken;
    state.modelResolution.clear();
    const textureStats = initializeTextureStats(collectReferencedTextureAssets(scene));
    const resolutionStats = { found: 0, parsed: 0, missing: 0, listed: (scene.modelAssets || []).length };
    const progress = createProgressiveModelRender(scene, definitions, gridGroups, renderTextureToken, renderToken);

    state.contextBounds = contextRelativeBounds(scene);
    state.contextGridIds = new Set(sceneGrids(scene).filter(grid => grid && grid.isContext).map(grid => String(grid.id || "")));
    state.primaryGridId = primaryGrid(scene)?.id || "";
    const bounds = state.contextBounds ? state.contextBounds.clone() : primaryGridRelativeBounds(scene);
    if (!state.contextBounds && bounds.isEmpty()) {
        const voxelBounds = standaloneVoxelViewBounds(scene);
        if (voxelBounds) bounds.copy(voxelBounds);
    }
    state.currentBounds = bounds;
    state.currentFloorGridAlignment = floorGridAlignment(scene);
    state.currentGridSize = floorGridMajorStep(scene);
    renderVoxelBodies(scene.voxels || [], scene.voxelDeformations || [], scene.voxelMaterials || []);
    progress.rebuild();
    updateSceneBounds(false);
    updateSunLightPosition();
    fitCameraToScene();
    updateModelStats(resolutionStats, progress.lastRenderStats, modelAssets.size);
    updateTextureStats();
    state.stats["Voxel bodies"] = (scene.voxels || []).length;
    state.stats["Voxel proxies"] = state.sceneRenderCounts.voxelProxies;
    state.stats["Voxel data chunks"] = state.sceneRenderCounts.voxelMeshChunks;
    state.stats["Voxel mesh parts"] = state.sceneRenderCounts.voxelMeshParts;
    state.stats["Voxel mesh vertices"] = state.sceneRenderCounts.voxelMeshVertices;
    state.stats["Voxel mesh triangles"] = state.sceneRenderCounts.voxelMeshTriangles;
    state.stats["Voxel chunks clipped"] = state.sceneRenderCounts.voxelClippedChunks;
    state.stats["Voxel polygons clipped"] = state.sceneRenderCounts.voxelClippedPolygons;
    state.stats["Voxel materials"] = (scene.voxelMaterials || []).length;
    state.stats["Context mode"] = scene.context && scene.context.enabled ? "on" : "off";
    state.stats["Context grids"] = scene.context && scene.context.enabled ? num(scene.context.gridCount, gridGroups.size) : 0;
    state.stats["Context clipped grids"] = scene.context && scene.context.enabled ? num(scene.context.clippedGridCount, 0) : 0;
    state.stats["Context blocks"] = state.contextGridIds.size ? (scene.blockInstances || []).filter(block => state.contextGridIds.has(String(block.gridId || ""))).length : 0;
    state.stats["Context voxels"] = scene.context && scene.context.enabled ? num(scene.context.voxelBodyCount, (scene.voxels || []).length) : 0;
    state.stats["Scene mods"] = state.modRoots.size;
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

function createProgressiveModelRender(scene, definitions, gridGroups, textureToken, renderToken) {
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
            const nextLayer = buildModelLayer(scene, definitions, renderContext, gridGroups);
            const previousLayer = modelLayer;
            modelLayer = nextLayer.layer;
            for (const gridGroup of gridGroups.values()) {
                const child = modelLayer.children.find(candidate => candidate.userData.gridId === gridGroup.userData.gridId);
                if (child) gridGroup.add(child);
            }
            if (previousLayer) {
                for (const gridGroup of gridGroups.values()) {
                    const oldChild = gridGroup.children.find(child => child.userData.modelLayerToken === previousLayer.userData.modelLayerToken);
                    if (oldChild) {
                        gridGroup.remove(oldChild);
                        disposeObjectTree(oldChild);
                    }
                }
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
    if (standaloneVoxelBody(scene)) {
        return { offsetX: 0, offsetZ: 0, cellCountX: 0, cellCountZ: 0, minorStep: LARGE_GRID_CUBE_SIZE };
    }

    const primary = primaryGrid(scene) || scene.grid || {};
    const primaryId = String(primary.id || "");
    const gridSize = primary.gridSize || LARGE_GRID_CUBE_SIZE;
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;

    for (const block of scene.blockInstances || []) {
        if (primaryId && block.gridId && String(block.gridId) !== primaryId) continue;
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

function floorGridMajorStep(scene) {
    return standaloneVoxelBody(scene) ? ASTEROID_GRID_CUBE_SIZE : primaryGrid(scene)?.gridSize || scene.grid && scene.grid.gridSize || LARGE_GRID_CUBE_SIZE;
}

function floorAxisOffset(cellCount, gridSize) {
    return Math.abs(cellCount % 2) === 1 ? gridSize * 0.5 : 0;
}

function buildModelLayer(scene, definitions, renderContext, gridGroups) {
    const layer = new THREE.Group();
    layer.name = "QuasarGridModels";
    layer.userData.modelLayerToken = `models:${Date.now()}:${Math.random()}`;
    let modelMeshes = 0;
    let proxyMeshes = 0;
    const stats = { proxyBatches: 0, modelBatches: 0 };
    const blocksByGrid = new Map();
    for (const block of scene.blockInstances || []) {
        const gridId = String(block.gridId || primaryGrid(scene)?.id || "");
        let blocks = blocksByGrid.get(gridId);
        if (!blocks) {
            blocks = [];
            blocksByGrid.set(gridId, blocks);
        }
        blocks.push(block);
    }

    for (const [gridId, blocks] of blocksByGrid) {
        const grid = gridById(scene, gridId) || primaryGrid(scene) || scene.grid || {};
        const gridLayer = new THREE.Group();
        gridLayer.name = `GridModels:${gridId || "primary"}`;
        gridLayer.userData.gridId = gridId;
        gridLayer.userData.modelLayerToken = layer.userData.modelLayerToken;
        const gridRenderContext = { ...renderContext, batches: new Map() };
        const proxyBatches = new Map();
        for (const block of blocks) {
            const definition = definitions.get(block.blockTypeId);
            const box = blockBox(block, grid.gridSize || LARGE_GRID_CUBE_SIZE);
            const blockMeshes = createBlockMeshes(block, definition, gridRenderContext);
            if (blockMeshes.length) {
                for (const mesh of blockMeshes) queueModelBatch(mesh, gridRenderContext);
                modelMeshes += blockMeshes.length;
            } else {
                queueBlockProxy(proxyBatches, block, definition, box);
                proxyMeshes++;
            }
        }
        flushProxyBatches(gridLayer, proxyBatches);
        stats.proxyBatches += proxyBatches.size;
        stats.modelBatches += flushModelBatches(gridLayer, gridRenderContext);
        if (gridLayer.children.length) layer.add(gridLayer);
    }

    return {
        layer,
        stats: {
            modelMeshes,
            proxyMeshes,
            proxyBatches: stats.proxyBatches,
            modelBatches: stats.modelBatches,
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
    const anchorGrid = primaryGrid(scene) || scene.grid || {};
    const worldMatrix = matrixDtoToThree(anchorGrid.worldMatrix);
    const voxel = standaloneVoxelBody(scene);
    const voxelBounds = voxel && boundsToBox3(voxel.worldAabb);
    const center = voxelBounds && !voxelBounds.isEmpty() ? voxelBounds.getCenter(new THREE.Vector3()) : gridCenterWorld(scene, worldMatrix, anchorGrid);
    const inverseRotation = new THREE.Matrix4().extractRotation(worldMatrix).invert();
    state.viewRotation = inverseRotation;
    state.viewTransform = inverseRotation.clone().multiply(new THREE.Matrix4().makeTranslation(-center.x, -center.y, -center.z));
}

function buildGridGroups(scene, root) {
    const groups = new Map();
    for (const grid of sceneGrids(scene)) {
        const gridId = String(grid.id || "");
        if (!gridId || groups.has(gridId)) continue;
        const group = new THREE.Group();
        group.name = `${grid.isPrimary ? "Primary" : "Context"}Grid:${grid.displayName || gridId}`;
        group.matrixAutoUpdate = false;
        group.matrix.copy(gridRelativeMatrix(grid));
        group.userData.gridId = gridId;
        group.userData.grid = grid;
        root.add(group);
        groups.set(gridId, group);
    }
    if (!groups.size && scene.grid) {
        const gridId = String(scene.grid.id || "");
        const group = new THREE.Group();
        group.name = `PrimaryGrid:${scene.grid.displayName || gridId || "grid"}`;
        group.matrixAutoUpdate = false;
        group.matrix.copy(gridRelativeMatrix(scene.grid));
        group.userData.gridId = gridId;
        group.userData.grid = scene.grid;
        root.add(group);
        groups.set(gridId, group);
    }
    return groups;
}

function sceneGrids(scene) {
    const grids = [];
    const seen = new Set();
    const add = grid => {
        if (!grid) return;
        const id = String(grid.id || "");
        if (id && seen.has(id)) return;
        if (id) seen.add(id);
        grids.push(grid);
    };
    for (const grid of scene.grids || []) add(grid);
    add(scene.grid);
    return grids;
}

function primaryGrid(scene) {
    const contextPrimaryId = scene.context && scene.context.primaryGridId ? String(scene.context.primaryGridId) : "";
    return sceneGrids(scene).find(grid => contextPrimaryId && String(grid.id || "") === contextPrimaryId)
        || sceneGrids(scene).find(grid => grid && grid.isPrimary)
        || scene.grid
        || sceneGrids(scene)[0]
        || null;
}

function gridById(scene, gridId) {
    const id = String(gridId || "");
    return sceneGrids(scene).find(grid => String(grid.id || "") === id) || null;
}

function gridRelativeMatrix(grid) {
    return (state.viewTransform || new THREE.Matrix4()).clone().multiply(matrixDtoToThree(grid && grid.worldMatrix));
}

function contextRelativeBounds(scene) {
    const context = scene.context || {};
    if (!context.enabled) return null;
    const relativeBounds = boundsToBox3(context.relativeAabb);
    if (relativeBounds && !relativeBounds.isEmpty()) return relativeBounds;
    const worldBounds = boundsToBox3(context.worldAabb);
    if (!worldBounds || worldBounds.isEmpty()) return null;
    return transformBounds(worldBounds, state.viewTransform || new THREE.Matrix4());
}

function primaryGridRelativeBounds(scene) {
    const bounds = gridLocalBounds(scene);
    const primary = primaryGrid(scene) || scene.grid;
    if (!bounds || bounds.isEmpty() || !primary) return new THREE.Box3();
    return transformBounds(bounds, gridRelativeMatrix(primary)) || new THREE.Box3();
}

function transformBounds(bounds, matrix) {
    const transformed = new THREE.Box3();
    for (const point of [
        new THREE.Vector3(bounds.min.x, bounds.min.y, bounds.min.z),
        new THREE.Vector3(bounds.min.x, bounds.min.y, bounds.max.z),
        new THREE.Vector3(bounds.min.x, bounds.max.y, bounds.min.z),
        new THREE.Vector3(bounds.min.x, bounds.max.y, bounds.max.z),
        new THREE.Vector3(bounds.max.x, bounds.min.y, bounds.min.z),
        new THREE.Vector3(bounds.max.x, bounds.min.y, bounds.max.z),
        new THREE.Vector3(bounds.max.x, bounds.max.y, bounds.min.z),
        new THREE.Vector3(bounds.max.x, bounds.max.y, bounds.max.z),
    ]) transformed.expandByPoint(point.applyMatrix4(matrix));
    return transformed.isEmpty() ? null : transformed;
}

function standaloneVoxelBody(scene) {
    if ((scene.blockInstances || []).length) return null;
    return (scene.voxels || [])[0] || null;
}

function standaloneVoxelViewBounds(scene) {
    const voxel = standaloneVoxelBody(scene);
    const bounds = voxel && boundsToBox3(voxel.worldAabb);
    if (!bounds || bounds.isEmpty()) return null;
    return bounds.applyMatrix4(state.viewTransform || new THREE.Matrix4());
}

function configureEnvironment(scene) {
    state.sunDirection = vec3(scene.environment && scene.environment.sunDirection).normalize();
    if (state.sunDirection.lengthSq() === 0) state.sunDirection.set(0.33946735, 0.70979536, -0.61721337).normalize();
    state.sunIntensity = Math.max(0.15, num(scene.environment && scene.environment.sunIntensity, 1));
    updateLighting();
}

function buildGridLightGroups(scene, gridGroups) {
    state.gridLights = [];
    const sources = collectSceneLightSources(scene);
    const lightGroup = new THREE.Group();
    lightGroup.name = "QuasarGridLights";
    state.gridGroup.add(lightGroup);
    state.gridLightGroup = lightGroup;

    const center = gridLocalCenter(scene);
    const selectedSources = sources
        .slice()
        .sort((a, b) => lightSourceSortKey(b, center) - lightSourceSortKey(a, center))
        .slice(0, MAX_VIEWER_LIGHTS);
    const shadowSourceIds = selectProjectedShadowSourceIds(selectedSources, center);

    for (const source of selectedSources) {
        const sourceGridGroup = gridGroups.get(String(source.gridId || "")) || gridGroups.get(String(primaryGrid(scene)?.id || ""));
        const lightParent = sourceGridGroup || lightGroup;
        const light = createGridLight(source, lightParent, shadowSourceIds.has(lightSourceIdentity(source)));
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
        const key = lightSourceIdentity(source);
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

function lightSourceIdentity(source) {
    if (!source) return "";
    return source.id ? String(source.id) : `${source.blockId || ""}:${source.kind || ""}:${formatViewerVector(source.position)}`;
}

function formatViewerVector(vector) {
    return `${num(vector && vector.x, 0).toFixed(3)}, ${num(vector && vector.y, 0).toFixed(3)}, ${num(vector && vector.z, 0).toFixed(3)}`;
}

function createGridLight(source, lightGroup, castsProjectedShadow = false) {
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
        light.castShadow = castsProjectedShadow;
        if (castsProjectedShadow) configureProjectedLightShadow(light, source, reflectorRadius || radius || 1);

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

function selectProjectedShadowSourceIds(sources, center) {
    return new Set(sources
        .filter(isProjectedShadowCandidate)
        .sort((a, b) => {
            const scoreDelta = projectedShadowScore(b, center) - projectedShadowScore(a, center);
            if (scoreDelta !== 0) return scoreDelta;
            return lightSourceIdentity(a).localeCompare(lightSourceIdentity(b));
        })
        .slice(0, MAX_PROJECTED_LIGHT_SHADOWS)
        .map(lightSourceIdentity));
}

function isProjectedShadowCandidate(source) {
    if (!source || source.enabled === false) return false;
    if (String(source.kind || "point").toLowerCase() !== "spot") return false;
    if (num(source.intensity, 0) <= 0) return false;
    return Math.max(num(source.reflectorRadius, 0), num(source.radius, 0), 0) > 0;
}

function projectedShadowScore(source, center) {
    const position = vec3(source && source.position);
    const distancePenalty = Math.min(1_000_000, position.distanceTo(center));
    const reach = Math.max(num(source && source.reflectorRadius, 0), num(source && source.radius, 0), 1);
    return Math.max(0, num(source && source.intensity, 0)) * reach * 1000 - distancePenalty;
}

function configureProjectedLightShadow(light, source, distance) {
    light.shadow.mapSize.set(PROJECTED_LIGHT_SHADOW_MAP_SIZE, PROJECTED_LIGHT_SHADOW_MAP_SIZE);
    light.shadow.camera.near = PROJECTED_LIGHT_SHADOW_NEAR;
    light.shadow.camera.far = Math.max(distance, 1);
    light.shadow.camera.fov = clamp(num(source && source.coneDegrees, 52), 1, 150);
    light.shadow.bias = PROJECTED_LIGHT_SHADOW_BIAS;
    light.shadow.normalBias = PROJECTED_LIGHT_SHADOW_NORMAL_BIAS;
    light.shadow.camera.updateProjectionMatrix();
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

function gridCenterWorld(scene, worldMatrix, grid = scene.grid) {
    const worldBounds = boundsToBox3(grid && grid.bounds);
    if (worldBounds && !worldBounds.isEmpty()) return worldBounds.getCenter(new THREE.Vector3());

    const localBounds = gridLocalBounds(scene);
    if (localBounds && !localBounds.isEmpty()) return localBounds.getCenter(new THREE.Vector3()).applyMatrix4(worldMatrix);

    return new THREE.Vector3().setFromMatrixPosition(worldMatrix);
}

function gridLocalBounds(scene) {
    const bounds = new THREE.Box3();
    let hasBounds = false;
    const primaryId = String(primaryGrid(scene)?.id || "");
    for (const chunk of scene.chunks || []) {
        if (primaryId && chunk.gridId && String(chunk.gridId) !== primaryId) continue;
        const min = vec3(chunk.localAabbMin);
        const max = vec3(chunk.localAabbMax);
        bounds.union(new THREE.Box3(min, max));
        hasBounds = true;
    }
    if (hasBounds) return bounds;

    const gridSize = primaryGrid(scene)?.gridSize || scene.grid && scene.grid.gridSize || LARGE_GRID_CUBE_SIZE;
    for (const block of scene.blockInstances || []) {
        if (primaryId && block.gridId && String(block.gridId) !== primaryId) continue;
        bounds.union(blockBox(block, gridSize));
        hasBounds = true;
    }
    return hasBounds ? bounds : null;
}

function renderVoxelBodies(voxels, voxelChunks, voxelMaterials) {
    state.sceneRenderCounts.voxelProxies = 0;
    state.sceneRenderCounts.voxelMeshChunks = 0;
    state.sceneRenderCounts.voxelMeshParts = 0;
    state.sceneRenderCounts.voxelMeshVertices = 0;
    state.sceneRenderCounts.voxelMeshTriangles = 0;
    state.sceneRenderCounts.voxelClippedChunks = 0;
    state.sceneRenderCounts.voxelClippedPolygons = 0;
    if (!voxels.length && !voxelChunks.length) return;

    const group = new THREE.Group();
    group.name = "QuasarVoxels";
    group.matrixAutoUpdate = false;
    group.matrix.identity();
    group.visible = !els.showVoxels || els.showVoxels.checked;
    state.voxelGroup = group;
    state.scene.add(group);

    const meshedBodyIds = new Set();
    const voxelBodiesById = new Map(voxels.map(voxel => [String(voxel.id || ""), voxel]));
    const voxelMaterialsByIndex = new Map((voxelMaterials || []).map(material => [Number(material.index), material]));
    let failedVoxelChunks = 0;
    for (const chunk of voxelChunks) {
        const voxel = voxelBodiesById.get(String(chunk.voxelBodyId || ""));
        const diagnostics = { clippedChunk: false, surfacePolygons: 0, clippedPolygons: 0, emittedPolygons: 0 };
        const mesh = createVoxelDataChunkMesh(chunk, voxel, voxelMaterialsByIndex, diagnostics);
        state.sceneRenderCounts.voxelClippedPolygons += diagnostics.clippedPolygons;
        if (!mesh) {
            if (diagnostics.clippedChunk || diagnostics.surfacePolygons > 0 && diagnostics.emittedPolygons === 0 && diagnostics.clippedPolygons > 0) {
                state.sceneRenderCounts.voxelClippedChunks++;
                continue;
            }
            failedVoxelChunks++;
            if (failedVoxelChunks <= 5) log(`Voxel chunk ${chunk.chunkId || "chunk"} for ${chunk.voxelBodyId || "unknown"} produced no mesh: ${describeVoxelDataChunk(chunk, voxel)}.`, true);
            continue;
        }
        group.add(mesh);
        state.voxelMeshes.push(mesh);
        meshedBodyIds.add(String(chunk.voxelBodyId || ""));
        state.sceneRenderCounts.voxelMeshChunks++;
    }

    if (failedVoxelChunks > 0 && state.sceneRenderCounts.voxelMeshChunks === 0) {
        log(`Received ${failedVoxelChunks} voxel data chunk(s), but none produced renderable terrain.`, true);
    } else if (failedVoxelChunks > 0) {
        log(`Skipped ${failedVoxelChunks} voxel data chunk(s) that produced no terrain triangles.`, true);
    }

    const proxyGroup = new THREE.Group();
    proxyGroup.name = "QuasarVoxelProxies";
    proxyGroup.matrixAutoUpdate = false;
    proxyGroup.matrix.copy(state.viewTransform || new THREE.Matrix4());

    for (const voxel of voxels) {
        if (meshedBodyIds.has(String(voxel.id || ""))) continue;
        const mesh = createVoxelProxy(voxel);
        if (!mesh) continue;
        proxyGroup.add(mesh);
        state.voxelMeshes.push(mesh);
        state.sceneRenderCounts.voxelProxies++;
    }

    if (proxyGroup.children.length) group.add(proxyGroup);
}

function createVoxelDataChunkMesh(chunk, voxel, voxelMaterialsByIndex, diagnostics) {
    const content = voxelByteArray(chunk.content);
    const materials = voxelByteArray(chunk.materials);
    const size = chunk.size || {};
    const sx = Math.max(0, Math.floor(num(size.x, 0)));
    const sy = Math.max(0, Math.floor(num(size.y, 0)));
    const sz = Math.max(0, Math.floor(num(size.z, 0)));
    const lodScale = Math.max(1, 2 ** Math.max(0, Math.floor(num(chunk.lod, 0))));
    const expectedSamples = sx * sy * sz;
    if (sx < 2 || sy < 2 || sz < 2) {
        if (diagnostics && content.length >= expectedSamples) diagnostics.clippedChunk = true;
        return null;
    }
    if (content.length < expectedSamples) return null;

    const storageMin = chunk.storageMin || {};
    const positionLeftBottomCorner = vec3(voxel && voxel.positionLeftBottomCorner);
    const worldOrigin = new THREE.Vector3(
        positionLeftBottomCorner.x + num(storageMin.x, 0) * lodScale,
        positionLeftBottomCorner.y + num(storageMin.y, 0) * lodScale,
        positionLeftBottomCorner.z + num(storageMin.z, 0) * lodScale);
    const viewTransform = state.viewTransform || new THREE.Matrix4();
    const positions = [];
    const normals = [];
    const uvs = [];
    const indicesByVoxelPart = new Map();
    const cube = createVoxelCubeScratch();
    const clipBounds = voxelFloorClipBounds();

    for (let z = 0; z < sz - 1; z++) {
        for (let y = 0; y < sy - 1; y++) {
            for (let x = 0; x < sx - 1; x++) {
                fillVoxelCubeScratch(cube, x, y, z, sx, sy, sz, content, materials, worldOrigin, lodScale, viewTransform);
                polygonizeVoxelCube(cube, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, clipBounds, diagnostics);
            }
        }
    }

    const vertexCount = positions.length / 3;
    if (vertexCount <= 0 || !indicesByVoxelPart.size) return null;

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
    geometry.setAttribute("normal", new THREE.Float32BufferAttribute(normals, 3));
    geometry.setAttribute("uv", new THREE.Float32BufferAttribute(uvs, 2));
    geometry.setAttribute("uv2", new THREE.Float32BufferAttribute(uvs, 2));
    const meshMaterials = [];
    const allIndices = [];
    for (const [partKey, partIndices] of [...indicesByVoxelPart.entries()].sort(compareVoxelPartEntries)) {
        const part = parseVoxelPartKey(partKey);
        const start = allIndices.length;
        appendArray(allIndices, partIndices);
        geometry.addGroup(start, partIndices.length, meshMaterials.length);
        meshMaterials.push(createVoxelMeshMaterial(part.materialIndex, part.projection, voxelMaterialsByIndex && voxelMaterialsByIndex.get(part.materialIndex)));
        state.sceneRenderCounts.voxelMeshParts++;
        state.sceneRenderCounts.voxelMeshTriangles += Math.floor(partIndices.length / 3);
    }
    geometry.setIndex(allIndices);
    geometry.computeBoundingSphere();

    const mesh = new THREE.Mesh(geometry, meshMaterials);
    mesh.name = `VoxelData:${chunk.voxelBodyId || "unknown"}:${chunk.chunkId || "chunk"}`;
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    mesh.userData.voxel = {
        id: chunk.voxelBodyId || "",
        kind: "voxelData",
        displayName: chunk.chunkId || "voxel data chunk",
        chunk,
    };
    state.sceneRenderCounts.voxelMeshVertices += vertexCount;
    return mesh;
}

function describeVoxelDataChunk(chunk, voxel) {
    const content = voxelByteArray(chunk && chunk.content);
    const materials = voxelByteArray(chunk && chunk.materials);
    const size = chunk && chunk.size || {};
    let min = 255;
    let max = 0;
    let belowIso = false;
    let aboveIso = false;
    for (const value of content) {
        min = Math.min(min, value);
        max = Math.max(max, value);
        if (value < VOXEL_ISO_LEVEL) belowIso = true;
        else aboveIso = true;
    }
    const expected = Math.max(0, Math.floor(num(size.x, 0))) * Math.max(0, Math.floor(num(size.y, 0))) * Math.max(0, Math.floor(num(size.z, 0)));
    return `size=${Math.floor(num(size.x, 0))}x${Math.floor(num(size.y, 0))}x${Math.floor(num(size.z, 0))}, content=${content.length}/${expected}, materials=${materials.length}, range=${content.length ? `${min}-${max}` : "empty"}, crossesIso=${belowIso && aboveIso}, hasVoxelMetadata=${!!voxel}`;
}

const VOXEL_ISO_LEVEL = 127.5;
const VOXEL_TETRAHEDRA = [
    [0, 5, 1, 6], [0, 1, 2, 6], [0, 2, 3, 6],
    [0, 3, 7, 6], [0, 7, 4, 6], [0, 4, 5, 6],
];
const VOXEL_CUBE_OFFSETS = [
    [0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0],
    [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1],
];

function createVoxelCubeScratch() {
    return Array.from({ length: 8 }, () => ({ position: new THREE.Vector3(), normal: new THREE.Vector3(), value: 0, material: 0 }));
}

function fillVoxelCubeScratch(cube, x, y, z, sx, sy, sz, content, materials, worldOrigin, lodScale, viewTransform) {
    for (let i = 0; i < VOXEL_CUBE_OFFSETS.length; i++) {
        const offset = VOXEL_CUBE_OFFSETS[i];
        const px = x + offset[0];
        const py = y + offset[1];
        const pz = z + offset[2];
        const index = px + sx * (py + sy * pz);
        cube[i].position.set(worldOrigin.x + px * lodScale, worldOrigin.y + py * lodScale, worldOrigin.z + pz * lodScale).applyMatrix4(viewTransform);
        setVoxelSampleNormal(cube[i].normal, px, py, pz, sx, sy, sz, content).transformDirection(viewTransform);
        cube[i].value = num(content[index], 0);
        cube[i].material = num(materials[index], 0);
    }
}

function setVoxelSampleNormal(target, x, y, z, sx, sy, sz, content) {
    const gx = sampleVoxelContent(content, x + 1, y, z, sx, sy, sz) - sampleVoxelContent(content, x - 1, y, z, sx, sy, sz);
    const gy = sampleVoxelContent(content, x, y + 1, z, sx, sy, sz) - sampleVoxelContent(content, x, y - 1, z, sx, sy, sz);
    const gz = sampleVoxelContent(content, x, y, z + 1, sx, sy, sz) - sampleVoxelContent(content, x, y, z - 1, sx, sy, sz);
    target.set(-gx, -gy, -gz);
    if (target.lengthSq() < 0.000001) target.set(0, 1, 0);
    else target.normalize();
    return target;
}

function sampleVoxelContent(content, x, y, z, sx, sy, sz) {
    const px = clamp(Math.floor(x), 0, sx - 1);
    const py = clamp(Math.floor(y), 0, sy - 1);
    const pz = clamp(Math.floor(z), 0, sz - 1);
    return num(content[px + sx * (py + sy * pz)], 0);
}

function polygonizeVoxelCube(cube, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, clipBounds, diagnostics) {
    for (const tetra of VOXEL_TETRAHEDRA) {
        const vertices = tetra.map(index => cube[index]);
        polygonizeVoxelTetra(vertices, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, clipBounds, diagnostics);
    }
}

function polygonizeVoxelTetra(vertices, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, clipBounds, diagnostics) {
    const inside = [];
    const outside = [];
    for (const vertex of vertices) {
        if (vertex.value >= VOXEL_ISO_LEVEL) inside.push(vertex);
        else outside.push(vertex);
    }

    const insideCount = inside.length;
    if (insideCount === 0 || insideCount === 4) return;

    const material = dominantVoxelMaterial(inside);
    if (insideCount === 1) {
        const a = interpolateVoxelEdge(inside[0], outside[0]);
        const b = interpolateVoxelEdge(inside[0], outside[1]);
        const c = interpolateVoxelEdge(inside[0], outside[2]);
        addVoxelTriangle(a, b, c, material, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, true, clipBounds, diagnostics);
    } else if (insideCount === 3) {
        const a = interpolateVoxelEdge(outside[0], inside[0]);
        const b = interpolateVoxelEdge(outside[0], inside[1]);
        const c = interpolateVoxelEdge(outside[0], inside[2]);
        addVoxelTriangle(a, b, c, material, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, false, clipBounds, diagnostics);
    } else {
        const a = interpolateVoxelEdge(inside[0], outside[0]);
        const b = interpolateVoxelEdge(inside[1], outside[0]);
        const c = interpolateVoxelEdge(inside[1], outside[1]);
        const d = interpolateVoxelEdge(inside[0], outside[1]);
        addVoxelTriangle(a, b, c, material, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, false, clipBounds, diagnostics);
        addVoxelTriangle(a, c, d, material, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, false, clipBounds, diagnostics);
    }
}

function interpolateVoxelEdge(a, b) {
    const delta = b.value - a.value;
    const t = Math.abs(delta) > 0.0001 ? (VOXEL_ISO_LEVEL - a.value) / delta : 0.5;
    return createVoxelSurfaceVertex(a.position, b.position, a.normal, b.normal, clamp(t, 0, 1));
}

function createVoxelSurfaceVertex(aPosition, bPosition, aNormal, bNormal, t) {
    const vertex = new THREE.Vector3().lerpVectors(aPosition, bPosition, t);
    vertex.normal = new THREE.Vector3().lerpVectors(aNormal, bNormal, t);
    if (vertex.normal.lengthSq() < 0.000001) vertex.normal.set(0, 1, 0);
    else vertex.normal.normalize();
    return vertex;
}

function addVoxelTriangle(a, b, c, material, positions, normals, uvs, indicesByVoxelPart, voxelMaterialsByIndex, reverse, clipBounds, diagnostics) {
    const polygon = reverse ? [a, c, b] : [a, b, c];
    const clipped = orientVoxelPolygon(clipVoxelPolygonToFloor(polygon, clipBounds));
    if (diagnostics) diagnostics.surfacePolygons++;
    if (clipped.length < 3) {
        if (diagnostics) diagnostics.clippedPolygons++;
        return;
    }
    if (diagnostics) diagnostics.emittedPolygons++;

    const projection = voxelUvProjection(clipped);
    const partKey = voxelPartKey(material, projection);
    let indices = indicesByVoxelPart.get(partKey);
    if (!indices) {
        indices = [];
        indicesByVoxelPart.set(partKey, indices);
    }
    const base = positions.length / 3;
    const uvScale = voxelMaterialUvScale(voxelMaterialsByIndex && voxelMaterialsByIndex.get(Number(material)));
    const fallbackNormal = voxelPolygonNormal(clipped);
    for (const vertex of clipped) {
        positions.push(vertex.x, vertex.y, vertex.z);
        appendVoxelNormal(normals, vertex, fallbackNormal);
        appendVoxelUv(uvs, vertex, projection, uvScale);
    }
    for (let i = 1; i < clipped.length - 1; i++) {
        indices.push(base, base + i, base + i + 1);
    }
}

function orientVoxelPolygon(polygon) {
    if (polygon.length < 3) return polygon;

    const faceNormal = voxelPolygonNormal(polygon);
    const surfaceNormal = new THREE.Vector3();
    for (const vertex of polygon) {
        if (vertex.normal && vertex.normal.lengthSq() >= 0.000001) surfaceNormal.add(vertex.normal);
    }

    if (surfaceNormal.lengthSq() < 0.000001) return polygon;
    return faceNormal.dot(surfaceNormal) < 0 ? polygon.slice().reverse() : polygon;
}

function voxelPolygonNormal(polygon) {
    const a = polygon[0];
    const b = polygon[1];
    const c = polygon[2];
    const normal = new THREE.Vector3().subVectors(b, a).cross(new THREE.Vector3().subVectors(c, a));
    if (normal.lengthSq() < 0.000001) return new THREE.Vector3(0, 1, 0);
    return normal.normalize();
}

function appendVoxelNormal(normals, vertex, fallbackNormal) {
    const normal = vertex.normal && vertex.normal.lengthSq() >= 0.000001 ? vertex.normal : fallbackNormal;
    const direction = normal.dot(fallbackNormal) < 0 ? -1 : 1;
    normals.push(normal.x * direction, normal.y * direction, normal.z * direction);
}

function voxelUvProjection(polygon) {
    const a = polygon[0];
    const b = polygon[1];
    const c = polygon[2];
    const normal = new THREE.Vector3().subVectors(b, a).cross(new THREE.Vector3().subVectors(c, a));
    const ax = Math.abs(normal.x);
    const ay = Math.abs(normal.y);
    const az = Math.abs(normal.z);
    if (ay >= ax && ay >= az) return "xz";
    if (ax >= az) return "zy";
    return "xy";
}

function appendVoxelUv(uvs, vertex, projection, scale) {
    if (projection === "xz") uvs.push(vertex.x * scale, vertex.z * scale);
    else if (projection === "zy") uvs.push(vertex.z * scale, vertex.y * scale);
    else uvs.push(vertex.x * scale, vertex.y * scale);
}

function voxelMaterialUvScale(definition) {
    const tilingScale = num(definition && definition.tilingScale, 0);
    return Number.isFinite(tilingScale) && tilingScale > 0 ? 1 / tilingScale : 1 / 8;
}

function voxelPartKey(materialIndex, projection) {
    return `${Number(materialIndex) || 0}|${projection || "xz"}`;
}

function parseVoxelPartKey(key) {
    const separator = String(key).indexOf("|");
    if (separator < 0) return { materialIndex: Number(key) || 0, projection: "xz" };
    return {
        materialIndex: Number(String(key).slice(0, separator)) || 0,
        projection: String(key).slice(separator + 1) || "xz",
    };
}

function compareVoxelPartEntries(a, b) {
    const left = parseVoxelPartKey(a[0]);
    const right = parseVoxelPartKey(b[0]);
    if (left.materialIndex !== right.materialIndex) return left.materialIndex - right.materialIndex;
    return voxelProjectionSortOrder(left.projection) - voxelProjectionSortOrder(right.projection);
}

function voxelProjectionSortOrder(projection) {
    if (projection === "xz") return 0;
    if (projection === "zy") return 1;
    return 2;
}

function clipVoxelPolygonToFloor(polygon, bounds) {
    if (!bounds) return polygon;
    let clipped = clipVoxelPolygonAxis(polygon, "x", bounds.minX, true);
    clipped = clipVoxelPolygonAxis(clipped, "x", bounds.maxX, false);
    clipped = clipVoxelPolygonAxis(clipped, "z", bounds.minZ, true);
    return clipVoxelPolygonAxis(clipped, "z", bounds.maxZ, false);
}

function clipVoxelPolygonAxis(polygon, axis, limit, keepGreater) {
    if (polygon.length === 0) return polygon;
    const result = [];
    let previous = polygon[polygon.length - 1];
    let previousInside = keepGreater ? previous[axis] >= limit : previous[axis] <= limit;
    for (const current of polygon) {
        const currentInside = keepGreater ? current[axis] >= limit : current[axis] <= limit;
        if (currentInside !== previousInside) result.push(intersectVoxelClipEdge(previous, current, axis, limit));
        if (currentInside) result.push(current);
        previous = current;
        previousInside = currentInside;
    }
    return result;
}

function intersectVoxelClipEdge(a, b, axis, limit) {
    const delta = b[axis] - a[axis];
    const t = Math.abs(delta) > 0.000001 ? (limit - a[axis]) / delta : 0;
    return createVoxelSurfaceVertex(a, b, a.normal || new THREE.Vector3(0, 1, 0), b.normal || new THREE.Vector3(0, 1, 0), clamp(t, 0, 1));
}

function voxelFloorClipBounds() {
    if (state.lastScene && !(state.lastScene.blockInstances || []).length) return null;
    const bounds = state.currentBounds && state.currentBounds.clone();
    if (!bounds || bounds.isEmpty()) return null;
    if (state.gridGroup) {
        state.gridGroup.updateMatrixWorld(true);
        bounds.applyMatrix4(state.gridGroup.matrixWorld);
    }
    const layout = floorGridLayout(bounds, state.currentGridSize, state.currentFloorGridAlignment);
    return {
        minX: layout.offsetX + layout.startXCell * layout.minorStep,
        maxX: layout.offsetX + layout.endXCell * layout.minorStep,
        minZ: layout.offsetZ + layout.startZCell * layout.minorStep,
        maxZ: layout.offsetZ + layout.endZCell * layout.minorStep,
    };
}

function appendArray(target, source) {
    for (let i = 0; i < source.length; i++) target.push(source[i]);
}

function dominantVoxelMaterial(vertices) {
    const counts = new Map();
    for (const vertex of vertices) counts.set(vertex.material, (counts.get(vertex.material) || 0) + 1);
    let bestMaterial = 0;
    let bestCount = -1;
    for (const [material, count] of counts) {
        if (count > bestCount) {
            bestMaterial = material;
            bestCount = count;
        }
    }
    return bestMaterial;
}

function createVoxelMeshMaterial(materialIndex, projection, definition) {
    const color = colorFromHash(`voxel:${materialIndex}`, 0x5b6f54);
    const material = new THREE.MeshStandardMaterial({
        color,
        roughness: 1,
        metalness: 0,
        flatShading: false,
        side: THREE.DoubleSide,
    });
    applySpaceEngineersColorMasking(material, false, false);
    applyVoxelTexturesAsync(material, materialIndex, projection, definition);
    return material;
}

function applyVoxelTexturesAsync(material, materialIndex, projection, definition) {
    const useYTexture = projection === "xz";
    const colorTexture = normalizeLogicalTexturePath(definition && (useYTexture
        ? definition.colorMetalY || definition.colorMetalXZnY
        : definition.colorMetalXZnY || definition.colorMetalY));
    const normalTexture = normalizeLogicalTexturePath(definition && (useYTexture
        ? definition.normalGlossY || definition.normalGlossXZnY
        : definition.normalGlossXZnY || definition.normalGlossY));

    if (colorTexture) {
        loadTexture(colorTexture, "ColorMetalTexture")
            .then(texture => {
                if (!texture) return;
                material.map = texture;
                material.color.set(0xffffff);
                setSpaceEngineersColorMetalTexture(material, true);
                material.needsUpdate = true;
                state.stats["Voxel material textures loaded"] = (state.stats["Voxel material textures loaded"] || 0) + 1;
            })
            .catch(error => {
                if (error && !error.isMissingLocalTexture) log(`Failed to load voxel material ${materialIndex} color texture ${colorTexture}: ${error.message}`, true);
            });
    }

    if (normalTexture) {
        loadTexture(normalTexture, "NormalGlossTexture")
            .then(texture => {
                if (!texture) return;
                material.normalMap = texture;
                material.normalScale.set(-1, 1);
                setSpaceEngineersNormalGlossTexture(material, true);
                material.needsUpdate = true;
                state.stats["Voxel material textures loaded"] = (state.stats["Voxel material textures loaded"] || 0) + 1;
            })
            .catch(error => {
                if (error && !error.isMissingLocalTexture) log(`Failed to load voxel material ${materialIndex} normal/gloss texture ${normalTexture}: ${error.message}`, true);
            });
    }
}

function voxelByteArray(value) {
    if (!value) return new Uint8Array();
    if (value instanceof Uint8Array) return value;
    if (Array.isArray(value)) return Uint8Array.from(value);
    if (typeof value !== "string") return new Uint8Array();

    const binary = atob(value);
    const result = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) result[i] = binary.charCodeAt(i);
    return result;
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
    mesh.castShadow = false;
    mesh.receiveShadow = false;
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
    const key = `${model.rootId || ""}|${model.geometryLogicalPath || model.logicalPath}|${patternOffsetKey(patternOffset)}|${layer}|${modelGeometryGroupsKey(groups)}`;
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
    const key = `${model.rootId || ""}|${model.logicalPath}|${group.materialIndex}|${group.materialName}|${group.technique}|${stableTextureKey(textures)}|glass=${transparentMaterialKey(transparentMaterial)}|metalnessColorable=${skin && skin.metalnessColorable ? 1 : 0}`;
    if (renderContext.materials.has(key)) return renderContext.materials.get(key);

    const transparentParameters = transparentMaterial ? spaceEngineersTransparentMaterialParameters(transparentMaterial, technique) : null;
    const color = transparentParameters?.color || colorFromHash(`${model.logicalPath}|${group.materialName || group.materialIndex}`);
    const transparent = renderMode.blended || (renderMode.decal && !renderMode.cutout);
    const material = new THREE.MeshStandardMaterial({
        color,
        roughness: transparentParameters ? transparentParameters.roughness : 0.72,
        metalness: transparentParameters ? transparentParameters.metalness : 0.22,
        transparent,
        opacity: transparentParameters ? transparentParameters.opacity : technique.includes("GLASS") ? 0.38 : renderMode.blended ? 0.7 : 1,
        depthWrite: !renderMode.blended && !renderMode.decal,
        premultipliedAlpha: !!transparentParameters,
        envMapIntensity: transparentParameters ? transparentParameters.envMapIntensity : 1,
        side: modelMaterialSide(technique, renderMode),
    });
    if (transparentParameters) material.userData.seTransparentMaterialColor = color.clone();
    if (renderMode.decal) {
        material.polygonOffset = true;
        material.polygonOffsetFactor = -1;
        material.polygonOffsetUnits = -1;
    }
    if (transparent) material.forceSinglePass = true;
    material.userData.renderCacheKey = key;
    material.userData.seRenderMode = modelMaterialRenderModeName(renderMode);
    applySpaceEngineersColorMasking(material, skin && skin.metalnessColorable, colorMaskable, transparentParameters);
    applyModelTextures(material, model, { ...group, textures }, technique, renderMode, renderContext.textureToken, colorMaskable);
    renderContext.materials.set(key, material);
    return material;
}

function sharedLcdMaterial(model, group, block, lcdSurface, renderContext, technique) {
    const key = `${model.rootId || ""}|${model.logicalPath}|${group.materialIndex}|${group.materialName}|lcd=${block.id || ""}:${lcdSurface.index || 0}:${lcdSurfaceKey(lcdSurface)}`;
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
            colorAdd: parseVector4(directChild(node, "ColorAdd"), { r: 0, g: 0, b: 0, a: 0 }),
            shadowMultiplier: parseVector4(directChild(node, "ShadowMultiplier"), { r: 0, g: 0, b: 0, a: 1 }),
            lightMultiplier: parseVector4(directChild(node, "LightMultiplier"), { r: 1, g: 1, b: 1, a: 1 }),
            reflectivity: directChildNumber(node, "Reflectivity", 0.04),
            fresnel: directChildNumber(node, "Fresnel", 0.25),
            reflectionShadow: directChildNumber(node, "ReflectionShadow", 0.4),
            gloss: directChildNumber(node, "Gloss", 0.4),
            glossTextureAdd: directChildNumber(node, "GlossTextureAdd", 0),
            specularColorFactor: directChildNumber(node, "SpecularColorFactor", 1),
            alphaSaturation: directChildNumber(node, "AlphaSaturation", 1),
            canBeAffectedByOtherLights: directChildText(node, "CanBeAffectedByOtherLights").toLowerCase() !== "false",
        });
    }
    return definitions;
}

function spaceEngineersTransparentMaterialParameters(definition, technique) {
    const sourceColor = definition.color || { r: 1, g: 1, b: 1, a: 0.38 };
    const alpha = clamp(sourceColor.a, 0, 1);
    const subtype = String(definition.subtype || "").toLowerCase();
    const glassLike = subtype.includes("glass") || String(technique || "").toUpperCase().includes("GLASS");
    const outsideGlass = glassLike && subtype.includes("outside");
    const insideGlass = glassLike && subtype.includes("inside");
    const opacity = outsideGlass
        ? clamp(0.76 + definition.reflectivity * 0.22 + definition.colorAdd.a * 0.08, 0.76, 0.92)
        : insideGlass
            ? clamp(0.34 + (1 - alpha) * 0.2, 0.34, 0.55)
            : glassLike ? clamp(0.35 + alpha * 0.65, 0.35, 0.9) : alpha;
    const colorScale = outsideGlass
        ? 0.34
        : insideGlass ? 0.72 : glassLike ? clamp(0.55 + alpha * 0.45, 0.55, 1) : 1;
    const light = definition.lightMultiplier || { r: 1, g: 1, b: 1, a: 1 };
    const lightIntensity = Math.max(light.r, light.g, light.b, light.a);
    const shadow = definition.shadowMultiplier || { a: 1 };
    const lightFactor = glassLike
        ? clamp(0.58 + lightIntensity * 2.2, 0.58, 1) / clamp(1 + Math.max(0, shadow.a - 1) * 0.08, 1, 1.5)
        : 1;

    return {
        color: new THREE.Color(
            clamp(sourceColor.r * colorScale, 0, 1),
            clamp(sourceColor.g * colorScale, 0, 1),
            clamp(sourceColor.b * colorScale, 0, 1)),
        opacity,
        roughness: clamp(1 - (definition.gloss + definition.glossTextureAdd * 0.35), 0.04, 1),
        metalness: clamp(definition.reflectivity * 3, 0, 0.35),
        envMapIntensity: clamp(0.45 + definition.reflectivity * 9 + definition.fresnel * 0.8, 0.45, 1.6),
        uniforms: {
            seUseTransparentMaterial: { value: true },
            seTransparentColorAdd: { value: new THREE.Vector4(
                clamp(definition.colorAdd?.r, 0, 4),
                clamp(definition.colorAdd?.g, 0, 4),
                clamp(definition.colorAdd?.b, 0, 4),
                clamp(definition.colorAdd?.a, 0, 4)) },
            seTransparentLightFactor: { value: clamp(lightFactor, 0.25, 1.25) },
            seTransparentAlphaSaturation: { value: clamp(definition.alphaSaturation, 0, 1) },
            seTransparentOpacity: { value: opacity },
            seTransparentGlossTextureAdd: { value: clamp(definition.glossTextureAdd, 0, 1) },
            seTransparentSpecularFactor: { value: clamp(definition.specularColorFactor, 0, outsideGlass ? 2 : 8) },
        },
    };
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
    const add = definition.colorAdd || {};
    const light = definition.lightMultiplier || {};
    const shadow = definition.shadowMultiplier || {};
    return [
        definition.subtype,
        definition.texture,
        definition.glossTexture,
        color.r,
        color.g,
        color.b,
        color.a,
        add.r,
        add.g,
        add.b,
        add.a,
        light.r,
        light.g,
        light.b,
        light.a,
        shadow.a,
        definition.reflectivity,
        definition.fresnel,
        definition.reflectionShadow,
        definition.gloss,
        definition.glossTextureAdd,
        definition.specularColorFactor,
        definition.alphaSaturation,
    ].join("|");
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

function directChildNumber(parent, name, fallback) {
    const text = directChildText(parent, name);
    return text === "" ? fallback : num(text, fallback);
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

function applyModelTextures(material, model, group, technique, renderMode, textureToken, colorMaskable = true) {
    const textureOptions = { rootId: model.rootId || "" };
    const usesTransparentMaterialTexture = technique.includes("GLASS") || !!group.textures?.GlassTexture || !!group.textures?.TransparentTexture;
    const base = textureSelection(group.textures, usesTransparentMaterialTexture
        ? ["GlassTexture", "TransparentTexture", "ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]
        : ["ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]);
    if (base) {
        loadTrackedTexture(base, textureToken, textureOptions).then(texture => {
            material.map = texture;
            material.color.copy(material.userData.seTransparentMaterialColor || new THREE.Color(0xffffff));
            setSpaceEngineersColorMetalTexture(material, colorMetalTextureSelectionHasMetalness(base));
            material.needsUpdate = true;
        }).catch(error => log(`Texture fallback retained for ${base.path}: ${error.message}`, true));
    }

    const alphaMask = (renderMode.cutout || renderMode.decal) ? alphaMaskTextureSelection(group.textures) : null;
    if (alphaMask) {
        loadTrackedTexture(alphaMask, textureToken, textureOptions).then(texture => {
            material.alphaMap = texture;
            setSpaceEngineersAlphaMaskTexture(material, texture);
            material.needsUpdate = true;
        }).catch(error => log(`Alpha-mask texture fallback retained for ${alphaMask.path}: ${error.message}`, true));
    }

    const colorMask = colorMaskable ? colorMaskTextureSelection(group.textures) : null;
    if (colorMask) {
        loadTrackedTexture(colorMask, textureToken, textureOptions).then(texture => {
            setSpaceEngineersColorMaskTexture(material, texture);
        }).catch(error => log(`Paint mask texture fallback retained for ${colorMask.path}: ${error.message}`, true));
    }

    const normalSlots = usesTransparentMaterialTexture
        ? ["GlassGlossTexture", "NormalGlossTexture", "NormalTexture", "NormalMapTexture"]
        : ["NormalGlossTexture", "NormalTexture", "NormalMapTexture"];
    const normal = textureSelection(group.textures, normalSlots);
    if (normal) {
        loadTrackedTexture(normal, textureToken, textureOptions).then(texture => {
            if (normal.slot === "GlassGlossTexture") {
                material.roughnessMap = texture;
                setSpaceEngineersTransparentGlossTexture(material, true);
            } else {
                material.normalMap = texture;
                material.normalScale.set(-1, 1);
                setSpaceEngineersNormalGlossTexture(material, true);
            }
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

function loadTrackedTexture(selection, textureToken, options = {}) {
    const key = textureAssetKey(selection.path, options.rootId || "");
    return loadTexture(selection.path, selection.slot, options).then(texture => {
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

function applySpaceEngineersColorMasking(material, metalnessColorable, colorMaskable = true, transparentParameters = null) {
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
        seUseTransparentMaterial: { value: false },
        seUseTransparentGlossTexture: { value: false },
        seTransparentColorAdd: { value: new THREE.Vector4(0, 0, 0, 0) },
        seTransparentLightFactor: { value: 1 },
        seTransparentAlphaSaturation: { value: 1 },
        seTransparentOpacity: { value: material.opacity },
        seTransparentGlossTextureAdd: { value: 0 },
        seTransparentSpecularFactor: { value: 1 },
    };
    if (transparentParameters) Object.assign(material.userData.seColorMaskUniforms, transparentParameters.uniforms);
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
uniform bool seUseTransparentMaterial;
uniform bool seUseTransparentGlossTexture;
uniform vec4 seTransparentColorAdd;
uniform float seTransparentLightFactor;
uniform float seTransparentAlphaSaturation;
uniform float seTransparentOpacity;
uniform float seTransparentGlossTextureAdd;
uniform float seTransparentSpecularFactor;

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
  if (seUseTransparentMaterial) {
    float seTextureAlpha = max(sampledDiffuseColor.a, 0.65);
    float seAlphaRemap = mix(1.0, seTextureAlpha, seTransparentAlphaSaturation);
    diffuseColor.a = clamp(seTransparentOpacity * seAlphaRemap, 0.0, 1.0);
  }
#else
  if (seUseTransparentMaterial) diffuseColor.a = clamp(seTransparentOpacity, 0.0, 1.0);
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
#ifdef USE_ROUGHNESSMAP
  if (seUseTransparentGlossTexture) {
    float seGloss = clamp(texture2D(roughnessMap, vRoughnessMapUv).a + seTransparentGlossTextureAdd, 0.0, 1.0);
    roughnessFactor = clamp(1.0 - seGloss, 0.0, 1.0);
  }
#endif
#ifdef USE_NORMALMAP
  if (seUseNormalGlossAlpha && !seUseTransparentGlossTexture) {
    float seGloss = clamp(texture2D(normalMap, vNormalMapUv).a + (seUseTransparentMaterial ? seTransparentGlossTextureAdd : 0.0), 0.0, 1.0);
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
}
if (seUseTransparentMaterial) {
    diffuseColor.rgb = diffuseColor.rgb * seTransparentLightFactor + seTransparentColorAdd.rgb * seTransparentColorAdd.a * seTransparentSpecularFactor * 0.04;
}`);
    };
    material.customProgramCacheKey = () => transparentParameters ? "se-grid-viewer-color-mask-transparent-v1" : "se-grid-viewer-color-mask-v5";
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

function setSpaceEngineersTransparentGlossTexture(material, enabled) {
    const uniforms = material.userData.seColorMaskUniforms;
    if (uniforms) uniforms.seUseTransparentGlossTexture.value = !!enabled;
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
    if (!state.contentFolder && !state.modsFolder) {
        log("No local Content or Mods folder selected; all models render as proxies.", true);
        stats.missing = (scene.modelAssets || []).length;
        updateModelStats(stats, progress.lastRenderStats, modelAssets.size);
        return stats;
    }

    if (!state.contentFolder) log("No local Content folder selected; vanilla fallback assets may render as proxies.", true);
    if (!state.modsFolder && [...modelAssets.values()].some(asset => asset.rootId || asset.RootId || String(asset.sourceKind || asset.SourceKind || "").toLowerCase() === "mod")) {
        log("No local Mods folder selected; selecting the global Mods folder may resolve modded assets.", true);
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
        addTextureAsset(assets, asset.logicalPath, asset.usage || asset.Usage || "metadata", asset.rootId || asset.RootId || "");
    }

    for (const resolved of state.modelResolution.values()) {
        const model = resolved && resolved.status === "parsed" ? resolved.model : null;
        if (!model) continue;

        for (const group of model.groups || []) {
            for (const [slot, path] of Object.entries(group.textures || {})) {
                addTextureAsset(assets, path, slot || "material", model.rootId || "");
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

function addTextureAsset(assets, logicalPath, usage, rootId = "") {
    const key = textureAssetKey(logicalPath, rootId);
    if (!key || assets.has(key)) return;
    assets.set(key, {
        assetId: `texture:${key}`,
        logicalPath: String(logicalPath || "").trim().replaceAll("\\", "/"),
        rootId,
        usage: usage || "unknown",
    });
}

function initializeTextureStats(textureAssets) {
    state.textureResolution.clear();
    state.textureStats = { listed: textureAssets.size, found: 0, loaded: 0, missing: 0, failed: 0 };
    for (const [key, asset] of textureAssets) {
        state.textureResolution.set(key, { asset, localStatus: state.contentFolder || state.modsFolder ? "pending" : "missing", loadStatus: "pending" });
    }
    if (!state.contentFolder && !state.modsFolder) state.textureStats.missing = textureAssets.size;
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
    const shadowedSpotLights = state.gridLights.filter(light => light.isSpotLight && light.castShadow).length;
    const eligibleProjectedShadows = sources.filter(isProjectedShadowCandidate).length;
    state.stats["Grid light sources"] = sources.length;
    state.stats["Active grid lights"] = sources.filter(source => source && source.enabled !== false && num(source.intensity, 0) > 0).length;
    state.stats["Viewer grid lights"] = state.gridLights.length;
    state.stats["Point lights"] = state.gridLights.filter(light => light.isPointLight).length;
    state.stats["Spot lights"] = state.gridLights.filter(light => light.isSpotLight).length;
    state.stats["Projected light shadows"] = shadowedSpotLights;
    state.stats["Capped projected shadows"] = Math.max(0, eligibleProjectedShadows - shadowedSpotLights);
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

function textureAssetKey(logicalPath, rootId = "") {
    const value = String(logicalPath || "").trim().replaceAll("\\", "/");
    return value ? `${rootId || ""}|${value.toLowerCase()}` : "";
}

function renderSummary(scene, resolutionStats, textureStats) {
    els.sceneSummary.innerHTML = "";
    addSummary("Grid", scene.grid && scene.grid.displayName);
    addSummary("Entity", scene.grid && scene.grid.id);
    addSummary("Blocks", (scene.blockInstances || []).length.toLocaleString());
    addSummary("Context", scene.context && scene.context.enabled ? "on" : "off");
    if (scene.context && scene.context.enabled) {
        addSummary("Context grids", num(scene.context.gridCount, sceneGrids(scene).length).toLocaleString());
        addSummary("Clipped grids", num(scene.context.clippedGridCount, 0).toLocaleString());
    }
    addSummary("Models", (scene.modelAssets || []).length.toLocaleString());
    addSummary("Found", resolutionStats.found.toLocaleString());
    addSummary("Parsed", resolutionStats.parsed.toLocaleString());
    addSummary("Missing", resolutionStats.missing.toLocaleString());
    addSummary("Textures", textureStats.listed.toLocaleString());
    addSummary("LCDs", countLcdSurfaces(scene).toLocaleString());
    addSummary("Voxels", (scene.voxels || []).length.toLocaleString());
    addSummary("Voxel chunks", (scene.voxelDeformations || []).length.toLocaleString());
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
