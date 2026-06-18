import * as THREE from "three";
import { els, state } from "./state.js";
import { blockBox } from "./geometry.js";
import { wireMaterial } from "./materials.js";
import { colorFromHash, matrixDtoToThree, num } from "./math.js";
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
            const mesh = createModelMesh(part.modelAssetId, block, matrixDtoToThree(part.localMatrix), part.patternOffset);
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

function createModelMesh(assetId, block, matrix, patternOffset = null) {
    const resolved = assetId ? state.modelResolution.get(assetId) : null;
    const model = resolved && resolved.status === "parsed" ? resolved.model : null;
    if (!model) return null;

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(model.positions, 3));
    if (model.normals) geometry.setAttribute("normal", new THREE.BufferAttribute(model.normals, 3));
    if (model.uvs) geometry.setAttribute("uv", new THREE.BufferAttribute(transformPatternUvs(model.uvs, patternOffset), 2));
    geometry.setAttribute("color", blockColorMaskAttribute(block, model.positions.length / 3));
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

function createModelMaterial(model, group) {
    const technique = String(group.technique || "MESH").toUpperCase();
    const transparent = technique.includes("GLASS") || technique.includes("ALPHA") || technique.includes("HOLO") || technique.includes("SHIELD");
    const material = new THREE.MeshStandardMaterial({
        color: colorFromHash(`${model.logicalPath}|${group.materialName || group.materialIndex}`),
        roughness: 0.72,
        metalness: 0.22,
        vertexColors: true,
        transparent,
        opacity: technique.includes("GLASS") ? 0.38 : transparent ? 0.7 : 1,
        side: technique.includes("SINGLE_SIDED") ? THREE.FrontSide : THREE.DoubleSide,
    });
    applySpaceEngineersColorMasking(material, false);
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
            setSpaceEngineersColorMetalTexture(material, colorMetalTextureSelectionHasMetalness(base));
            material.needsUpdate = true;
        }).catch(error => log(`Texture fallback retained for ${base.path}: ${error.message}`, true));
    }

    const colorMask = colorMaskTextureSelection(group.textures);
    if (colorMask) {
        loadTexture(colorMask.path, colorMask.slot).then(texture => {
            setSpaceEngineersColorMaskTexture(material, texture);
        }).catch(error => log(`Paint mask texture fallback retained for ${colorMask.path}: ${error.message}`, true));
    }

    const normal = textureSelection(group.textures, ["NormalGlossTexture", "NormalTexture", "NormalMapTexture"]);
    if (normal) {
        loadTexture(normal.path, normal.slot).then(texture => {
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
        shader.fragmentShader = shader.fragmentShader.replace("#include <color_fragment>", `#if defined( USE_COLOR ) || defined( USE_COLOR_ALPHA )
  #ifdef USE_MAP
    vec4 seMaskTexel = texture2D(seColorMaskMap, vMapUv);
    float seMaskFactor = mix(seMaskTexel.a, seMaskTexel.r, seColorMaskRedChannel);
    float seColoringFactor = seUseColorMaskMap ? seMaskFactor : seFallbackColoringFactor(diffuseColor.rgb);
    float seMetalness = seUseColorMetalAlpha ? sampledDiffuseColor.a : 0.0;
    if (!seMetalnessColorable) seColoringFactor = seRemoveMetalnessFromColoring(seMetalness, seColoringFactor);
    diffuseColor.rgb = seColorizeGray(diffuseColor.rgb, vColor.rgb, seColoringFactor);
  #else
    diffuseColor.rgb = seColorizeGray(diffuseColor.rgb, vColor.rgb, 1.0);
  #endif
#endif`);
    };
    material.customProgramCacheKey = () => "se-grid-viewer-color-mask-v1";
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

function blockColorMaskAttribute(block, vertexCount) {
    const color = colorMaskForBlock(block);
    const values = new Float32Array(vertexCount * 3);
    for (let i = 0; i < vertexCount; i++) {
        const target = i * 3;
        values[target] = color.x;
        values[target + 1] = color.y;
        values[target + 2] = color.z;
    }
    return new THREE.BufferAttribute(values, 3);
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
