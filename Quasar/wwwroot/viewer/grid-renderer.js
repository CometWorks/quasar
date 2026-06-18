import * as THREE from "three";
import { els, state } from "./state.js";
import { blockBox, boundsToBox3 } from "./geometry.js";
import { wireMaterial } from "./materials.js";
import { colorFromHash, matrixDtoToThree, num, vec3 } from "./math.js";
import { disposeObjectTree, fitCameraToScene, updateLighting, updateSceneBounds, updateSunLightPosition } from "./scene.js";
import { resolveModelAsset } from "./mwm-loader.js";
import { loadTexture } from "./texture-loader.js";
import { log } from "./logging.js";

let textureStatsToken = 0;

export async function renderGridScene(scene) {
    state.lastScene = scene;
    state.stats = {};
    if (state.gridGroup) {
        state.scene.remove(state.gridGroup);
        disposeObjectTree(state.gridGroup);
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

    const definitions = new Map((scene.blockDefinitions || []).map(definition => [definition.id, definition]));
    const modelAssets = new Map((scene.modelAssets || []).map(asset => [asset.assetId, asset]));
    const resolutionStats = await timed("modelResolutionTotal", () => resolveReferencedModels(scene, modelAssets));
    const renderTextureToken = ++textureStatsToken;
    const renderContext = createRenderContext(renderTextureToken);
    const textureStats = initializeTextureStats(collectReferencedTextureAssets(scene));
    updateTextureStats();

    const bounds = new THREE.Box3();
    let modelMeshes = 0;
    let proxyMeshes = 0;
    for (const block of scene.blockInstances || []) {
        const definition = definitions.get(block.blockTypeId);
        const box = blockBox(block, scene.grid.gridSize || 2.5);
        const blockMeshes = createBlockMeshes(block, definition, renderContext);
        if (blockMeshes.length) {
            for (const mesh of blockMeshes) queueModelBatch(mesh, renderContext);
            modelMeshes += blockMeshes.length;
        } else {
            const mesh = createBlockProxy(block, definition, box);
            group.add(mesh);
            proxyMeshes++;
        }
        bounds.union(box);
    }
    const modelBatches = flushModelBatches(group, renderContext);

    state.sceneRenderCounts.modelMeshes = modelMeshes;
    state.sceneRenderCounts.proxyMeshes = proxyMeshes;

    state.currentBounds = bounds;
    state.currentGridSize = scene.grid.gridSize || 2.5;
    renderVoxelBodies(scene.voxels || []);
    updateSceneBounds(false);
    updateSunLightPosition();
    fitCameraToScene();

    state.stats.Blocks = (scene.blockInstances || []).length;
    state.stats["Model meshes"] = modelMeshes;
    state.stats["Proxy meshes"] = proxyMeshes;
    state.stats["Model batches"] = modelBatches;
    state.stats["Shared geometries"] = renderContext.geometries.size;
    state.stats["Shared materials"] = renderContext.materials.size;
    state.stats["Models listed"] = (scene.modelAssets || []).length;
    state.stats["Models found locally"] = resolutionStats.found;
    state.stats["Models parsed"] = resolutionStats.parsed;
    state.stats["Models missing"] = resolutionStats.missing;
    state.stats["Textures listed"] = textureStats.listed;
    state.stats["Textures found locally"] = textureStats.found;
    state.stats["Textures loaded"] = textureStats.loaded;
    state.stats["Textures missing"] = textureStats.missing;
    state.stats["Textures failed"] = textureStats.failed;
    state.stats["Voxel bodies"] = (scene.voxels || []).length;
    state.stats["Voxel proxies"] = state.sceneRenderCounts.voxelProxies;
    updateTimingStats();
    renderSummary(scene, resolutionStats, textureStats);
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
            const mesh = createModelMesh(part.modelAssetId, block, matrixDtoToThree(part.localMatrix), part.patternOffset, renderContext);
            if (mesh) meshes.push(mesh);
        }
    } else {
        const assetId = block.currentModelAssetId || (definition && definition.modelAssetId) || "";
        const matrix = matrixDtoToThree(block.rotation);
        if (definition && definition.modelOffset) matrix.multiply(new THREE.Matrix4().makeTranslation(
            Number(definition.modelOffset.x) || 0,
            Number(definition.modelOffset.y) || 0,
            Number(definition.modelOffset.z) || 0));
        const mesh = createModelMesh(assetId, block, matrix, null, renderContext);
        if (mesh) meshes.push(mesh);
    }

    for (const subpart of block.subparts || []) {
        const mesh = createModelMesh(subpart.modelAssetId, block, matrixDtoToThree(subpart.localMatrix), null, renderContext);
        if (mesh) meshes.push(mesh);
    }

    return meshes;
}

function createModelMesh(assetId, block, matrix, patternOffset = null, renderContext = createRenderContext(textureStatsToken)) {
    const resolved = assetId ? state.modelResolution.get(assetId) : null;
    const model = resolved && resolved.status === "parsed" ? resolved.model : null;
    if (!model) return null;

    const geometry = sharedModelGeometry(model, patternOffset, renderContext);
    const materials = model.groups.map(group => sharedModelMaterial(model, group, renderContext));
    return {
        geometry,
        materials,
        matrix,
        block,
        colorMask: colorMaskForBlock(block),
        batchKey: `${geometry.userData.renderCacheKey}|${materials.map(material => material.userData.renderCacheKey).join("|")}`,
    };
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
        mesh.userData.blocks = [];

        for (let i = 0; i < batch.instances.length; i++) {
            const instance = batch.instances[i];
            mesh.setMatrixAt(i, instance.matrix);
            color.r = instance.colorMask.x;
            color.g = instance.colorMask.y;
            color.b = instance.colorMask.z;
            mesh.setColorAt(i, color);
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

function sharedModelGeometry(model, patternOffset, renderContext) {
    const key = `${model.geometryLogicalPath || model.logicalPath}|${patternOffsetKey(patternOffset)}`;
    if (renderContext.geometries.has(key)) return renderContext.geometries.get(key);

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(model.positions, 3));
    if (model.normals) geometry.setAttribute("normal", new THREE.BufferAttribute(model.normals, 3));
    if (model.uvs) geometry.setAttribute("uv", new THREE.BufferAttribute(transformPatternUvs(model.uvs, patternOffset), 2));
    geometry.setIndex(new THREE.BufferAttribute(model.indices, 1));
    for (const group of model.groups) geometry.addGroup(group.start, group.count, group.materialIndex);
    if (!model.normals) geometry.computeVertexNormals();
    geometry.computeBoundingSphere();
    geometry.userData.renderCacheKey = key;
    renderContext.geometries.set(key, geometry);
    return geometry;
}

function patternOffsetKey(patternOffset) {
    if (!patternOffset) return "default";
    const x = Number(patternOffset.x ?? patternOffset.X);
    const y = Number(patternOffset.y ?? patternOffset.Y);
    const z = Number(patternOffset.z ?? patternOffset.Z);
    const w = Number(patternOffset.w ?? patternOffset.W);
    return [x, y, z, w].map(value => Number.isFinite(value) ? value.toFixed(6) : "n").join(",");
}

function transformPatternUvs(uvs, patternOffset) {
    if (!uvs || !patternOffset) return uvs;
    const patternU = Number(patternOffset.z ?? patternOffset.Z);
    const patternV = Number(patternOffset.w ?? patternOffset.W);
    if (!Number.isFinite(patternU) || !Number.isFinite(patternV) || patternU === 0 || patternV === 0) return uvs;

    const offsetU = Number(patternOffset.x ?? patternOffset.X) / patternU;
    const offsetV = Number(patternOffset.y ?? patternOffset.Y) / patternV;
    if (!Number.isFinite(offsetU) || !Number.isFinite(offsetV)) return uvs;

    const transformed = new Float32Array(uvs.length);
    for (let i = 0; i < uvs.length; i += 2) {
        transformed[i] = uvs[i] + offsetU;
        transformed[i + 1] = uvs[i + 1] + offsetV;
    }
    return transformed;
}

function sharedModelMaterial(model, group, renderContext) {
    const key = `${model.logicalPath}|${group.materialIndex}|${group.materialName}|${group.technique}|${stableTextureKey(group.textures)}`;
    if (renderContext.materials.has(key)) return renderContext.materials.get(key);

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
    material.userData.renderCacheKey = key;
    applySpaceEngineersColorMasking(material, false);
    applyModelTextures(material, group, technique, renderContext.textureToken);
    renderContext.materials.set(key, material);
    return material;
}

function stableTextureKey(textures) {
    return Object.entries(textures || {})
        .filter(([, path]) => !!path)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([slot, path]) => `${slot}=${path}`)
        .join(";");
}

function applyModelTextures(material, group, technique, textureToken) {
    const base = textureSelection(group.textures, technique.includes("GLASS")
        ? ["GlassTexture", "TransparentTexture", "ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]
        : ["ColorMetalTexture", "DiffuseTexture", "BaseColorTexture"]);
    if (base) {
        loadTrackedTexture(base, textureToken).then(texture => {
            material.map = texture;
            material.color.set(0xffffff);
            setSpaceEngineersColorMetalTexture(material, colorMetalTextureSelectionHasMetalness(base));
            material.needsUpdate = true;
        }).catch(error => log(`Texture fallback retained for ${base.path}: ${error.message}`, true));
    }

    const colorMask = colorMaskTextureSelection(group.textures);
    if (colorMask) {
        loadTrackedTexture(colorMask, textureToken).then(texture => {
            setSpaceEngineersColorMaskTexture(material, texture);
        }).catch(error => log(`Paint mask texture fallback retained for ${colorMask.path}: ${error.message}`, true));
    }

    const normal = textureSelection(group.textures, ["NormalGlossTexture", "NormalTexture", "NormalMapTexture"]);
    if (normal) {
        loadTrackedTexture(normal, textureToken).then(texture => {
            material.normalMap = texture;
            material.normalScale.set(-1, 1);
            setSpaceEngineersNormalGlossTexture(material, true);
            material.needsUpdate = true;
        }).catch(error => log(`Normal texture fallback retained for ${normal.path}: ${error.message}`, true));
    }
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

function applySpaceEngineersColorMasking(material, metalnessColorable) {
    material.userData.seColorMaskUniforms = {
        seColorMaskMap: { value: fallbackWhiteTexture() },
        seUseColorMaskMap: { value: false },
        seColorMaskRedChannel: { value: 0 },
        seBlockColorMask: { value: new THREE.Vector3(0, -1, 0) },
        seMetalnessColorable: { value: !!metalnessColorable },
        seUseColorMetalAlpha: { value: false },
        seUseNormalGlossAlpha: { value: false },
    };
    material.onBeforeCompile = shader => {
        Object.assign(shader.uniforms, material.userData.seColorMaskUniforms);
        shader.fragmentShader = shader.fragmentShader.replace("#include <color_pars_fragment>", `#include <color_pars_fragment>
uniform sampler2D seColorMaskMap;
uniform bool seUseColorMaskMap;
uniform float seColorMaskRedChannel;
uniform vec3 seBlockColorMask;
uniform bool seMetalnessColorable;
uniform bool seUseColorMetalAlpha;
uniform bool seUseNormalGlossAlpha;

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
        shader.fragmentShader = shader.fragmentShader.replace("#include <color_fragment>", `vec3 sePaintMask = seBlockColorMask;
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
#endif`);
    };
    material.customProgramCacheKey = () => "se-grid-viewer-color-mask-v2";
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
    uniforms.seColorMaskRedChannel.value = colorMaskTextureUsesRedChannel(texture) ? 1 : 0;
}

function colorMaskTextureUsesRedChannel(texture) {
    return texture && (texture.format === THREE.RED_RGTC1_Format || texture.format === THREE.SIGNED_RED_RGTC1_Format || texture.userData && texture.userData.seColorMaskChannel === "r");
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

function createBlockProxy(block, definition, box) {
    const opacity = proxyOpacity(definition);
    const material = new THREE.MeshStandardMaterial({
        color: displayColorForBlock(block),
        roughness: 0.78,
        metalness: 0.12,
        transparent: opacity < 1,
        opacity,
    });
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

function updateTimingStats() {
    for (const [key, metric] of Object.entries(state.timings || {})) {
        const label = timingLabel(key);
        state.stats[`${label} total ms`] = Math.round(metric.totalMs);
        state.stats[`${label} max ms`] = Math.round(metric.maxMs);
        state.stats[`${label} count`] = metric.count;
    }
}

function timingLabel(key) {
    return key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, value => value.toUpperCase());
}

async function timed(key, operation) {
    const start = performance.now();
    try {
        return await operation();
    } finally {
        addTiming(key, performance.now() - start);
    }
}

function addTiming(key, durationMs) {
    const metric = state.timings[key] || { count: 0, totalMs: 0, maxMs: 0 };
    metric.count++;
    metric.totalMs += durationMs;
    metric.maxMs = Math.max(metric.maxMs, durationMs);
    state.timings[key] = metric;
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
