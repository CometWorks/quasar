import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { els, state } from "./state.js";
import { boundsToBox3 } from "./geometry.js";

export const SMALL_GRID_CUBE_SIZE = 0.5;
export const LARGE_GRID_CUBE_SIZE = SMALL_GRID_CUBE_SIZE * 5;
export const ASTEROID_GRID_CUBE_SIZE = LARGE_GRID_CUBE_SIZE * 5;
const FLOOR_GRID_DEFAULT_SIZE = 240;
const FLOOR_GRID_PADDING_SUPERSQUARES = 2;
const DEFAULT_FOG_DENSITY = 0.0025;
const FOG_OBSCURE_FLOOR_GRID_SPANS = 6;
const FOG_OBSCURED_TRANSMITTANCE = 0.03;
const FLY_MOUSE_SENSITIVITY = 0.0022;
const FLY_BASE_SPEED = 18;
const FLY_FAST_MULTIPLIER = 3;
const FLY_PITCH_LIMIT = Math.PI / 2 - 0.01;
const AMBIENT_WITH_LIGHTING = 0.16;
const AMBIENT_WITHOUT_LIGHTING = 1.0;
const SUN_LIGHT_INTENSITY_SCALE = 2.7;
const SUN_SHADOW_MAP_SIZE = 4096;
const SUN_SHADOW_PADDING_SCALE = 0.08;
const SUN_SHADOW_MIN_NORMAL_BIAS = 0.02;
const SUN_SHADOW_TEXEL_NORMAL_BIAS = 2.0;

export function initScene() {
    state.scene = new THREE.Scene();
    state.scene.background = new THREE.Color(0x070b12);
    state.scene.fog = new THREE.FogExp2(0x070b12, DEFAULT_FOG_DENSITY);

    state.camera = new THREE.PerspectiveCamera(55, 1, 0.05, 200000);
    state.camera.position.set(28, 24, 32);

    state.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
    state.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.75));
    state.renderer.outputColorSpace = THREE.SRGBColorSpace;
    state.renderer.shadowMap.enabled = true;
    state.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    els.viewport.appendChild(state.renderer.domElement);

    state.controls = new OrbitControls(state.camera, state.renderer.domElement);
    state.controls.enableDamping = true;
    state.controls.dampingFactor = 0.08;

    state.ambientLight = new THREE.AmbientLight(0xffffff, AMBIENT_WITH_LIGHTING);
    state.scene.add(state.ambientLight);
    state.sunLightTarget = new THREE.Object3D();
    state.sunLightTarget.name = "SunLightTarget";
    state.scene.add(state.sunLightTarget);
    state.sunLight = new THREE.DirectionalLight(0xffffff, 1.9);
    state.sunLight.position.set(40, 70, 35);
    state.sunLight.target = state.sunLightTarget;
    state.sunLight.castShadow = true;
    state.sunLight.shadow.mapSize.set(SUN_SHADOW_MAP_SIZE, SUN_SHADOW_MAP_SIZE);
    state.sunLight.shadow.bias = 0;
    state.sunLight.shadow.normalBias = SUN_SHADOW_MIN_NORMAL_BIAS;
    state.scene.add(state.sunLight);

    state.sunMarker = createSunMarker();
    state.sunMarkerLine = createSunMarkerLine();
    state.scene.add(state.sunMarker);
    state.scene.add(state.sunMarkerLine);
    updateLighting();
    replaceFloorGrid(null, LARGE_GRID_CUBE_SIZE);

    state.raycaster = new THREE.Raycaster();
    state.pointer = new THREE.Vector2();
    state.renderer.domElement.addEventListener("pointermove", onPointerMove);
    state.renderer.domElement.addEventListener("pointermove", onFlyPointerMove);
    state.renderer.domElement.addEventListener("click", onViewportClick);
    document.addEventListener("pointerlockchange", updateCameraHint);

    state.resizeObserver = new ResizeObserver(resize);
    state.resizeObserver.observe(els.viewport);
    resize();
}

export function animate(time) {
    requestAnimationFrame(animate);
    const now = time || performance.now();
    const delta = state.lastFrameTime ? Math.min(0.1, (now - state.lastFrameTime) / 1000) : 0;
    state.lastFrameTime = now;
    if (state.cameraMode === "fly") updateFlyMovement(delta);
    else state.controls.update();
    state.renderer.render(state.scene, state.camera);
    updateRenderStats();
}

export function replaceFloorGrid(bounds, gridSize, alignment = null) {
    const visible = state.floorGrid ? state.floorGrid.visible : true;
    if (state.floorGrid) {
        state.scene.remove(state.floorGrid);
        disposeObjectTree(state.floorGrid);
    }

    state.floorGrid = createFloorGrid(bounds, gridSize, alignment);
    state.floorGrid.visible = visible && (!els.showGridHelper || els.showGridHelper.checked);
    state.scene.add(state.floorGrid);
    updateFogForFloorGrid(state.floorGrid.userData && state.floorGrid.userData.layout);
}

function createFloorGrid(bounds, gridSize, alignment) {
    const layout = floorGridLayout(bounds, gridSize, alignment);
    const positions = [];
    const colors = [];
    const minorColor = colorComponents(0x1e293b);
    const majorColor = colorComponents(0x2563eb);
    const majorEveryCells = Math.max(1, Math.round(layout.majorStep / layout.minorStep));

    appendFloorGridLines({
        positions,
        colors,
        startCell: layout.startXCell,
        endCell: layout.endXCell,
        rangeStart: layout.offsetZ + layout.startZCell * layout.minorStep - layout.originZ,
        rangeEnd: layout.offsetZ + layout.endZCell * layout.minorStep - layout.originZ,
        origin: layout.originX,
        offset: layout.offsetX,
        axis: "x",
        minorStep: layout.minorStep,
        majorEveryCells,
        minorColor,
        majorColor,
    });
    appendFloorGridLines({
        positions,
        colors,
        startCell: layout.startZCell,
        endCell: layout.endZCell,
        rangeStart: layout.offsetX + layout.startXCell * layout.minorStep - layout.originX,
        rangeEnd: layout.offsetX + layout.endXCell * layout.minorStep - layout.originX,
        origin: layout.originZ,
        offset: layout.offsetZ,
        axis: "z",
        minorStep: layout.minorStep,
        majorEveryCells,
        minorColor,
        majorColor,
    });

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
    geometry.setAttribute("color", new THREE.Float32BufferAttribute(colors, 3));
    geometry.computeBoundingSphere();

    const material = new THREE.LineBasicMaterial({
        vertexColors: true,
        transparent: true,
        opacity: 0.9,
        depthWrite: false,
    });
    const grid = new THREE.LineSegments(geometry, material);
    grid.name = "FloorGrid";
    grid.position.set(layout.originX, layout.y, layout.originZ);
    grid.frustumCulled = false;
    grid.userData.layout = layout;
    return grid;
}

export function floorGridLayout(bounds, gridSize, alignment) {
    const minorStep = Math.max(SMALL_GRID_CUBE_SIZE, Number(alignment && alignment.minorStep) || SMALL_GRID_CUBE_SIZE);
    const majorStep = Math.max(minorStep, Number(gridSize) || LARGE_GRID_CUBE_SIZE);
    if (!bounds || bounds.isEmpty()) {
        const halfSize = FLOOR_GRID_DEFAULT_SIZE * 0.5;
        return floorGridCells(-halfSize, halfSize, -halfSize, halfSize, -0.02, majorStep, 0, 0, minorStep);
    }

    const padding = majorStep * FLOOR_GRID_PADDING_SUPERSQUARES;
    const y = bounds.min.y - Math.max(0.02, majorStep * 0.02);
    const offsetX = Number(alignment && alignment.offsetX) || 0;
    const offsetZ = Number(alignment && alignment.offsetZ) || 0;
    return floorGridCells(bounds.min.x - padding, bounds.max.x + padding, bounds.min.z - padding, bounds.max.z + padding, y, majorStep, offsetX, offsetZ, minorStep);
}

function floorGridCells(minX, maxX, minZ, maxZ, y, majorStep, offsetX = 0, offsetZ = 0, minorStep = SMALL_GRID_CUBE_SIZE) {
    const startXCell = Math.floor((minX - offsetX) / minorStep);
    const endXCell = Math.ceil((maxX - offsetX) / minorStep);
    const startZCell = Math.floor((minZ - offsetZ) / minorStep);
    const endZCell = Math.ceil((maxZ - offsetZ) / minorStep);
    return {
        startXCell,
        endXCell,
        startZCell,
        endZCell,
        originX: offsetX + (startXCell + endXCell) * minorStep * 0.5,
        originZ: offsetZ + (startZCell + endZCell) * minorStep * 0.5,
        offsetX,
        offsetZ,
        y,
        majorStep,
        minorStep,
    };
}

function appendFloorGridLines(options) {
    for (let cell = options.startCell; cell <= options.endCell; cell++) {
        const worldCoordinate = options.offset + cell * options.minorStep;
        const coordinate = worldCoordinate - options.origin;
        const isMajor = cell % options.majorEveryCells === 0;
        const color = isMajor ? options.majorColor : options.minorColor;
        if (options.axis === "x") {
            appendFloorGridLine(options.positions, options.colors, coordinate, options.rangeStart, coordinate, options.rangeEnd, color);
        } else {
            appendFloorGridLine(options.positions, options.colors, options.rangeStart, coordinate, options.rangeEnd, coordinate, color);
        }
    }
}

function updateFogForFloorGrid(layout) {
    if (!state.scene || !state.scene.fog || !layout) return;

    const width = Math.max(1, (layout.endXCell - layout.startXCell) * layout.minorStep);
    const depth = Math.max(1, (layout.endZCell - layout.startZCell) * layout.minorStep);
    const span = Math.max(width, depth, FLOOR_GRID_DEFAULT_SIZE);
    const obscureDistance = span * FOG_OBSCURE_FLOOR_GRID_SPANS;
    const targetDensity = Math.sqrt(-Math.log(FOG_OBSCURED_TRANSMITTANCE)) / obscureDistance;
    state.scene.fog.density = Math.min(DEFAULT_FOG_DENSITY, targetDensity);
}

function appendFloorGridLine(positions, colors, x1, z1, x2, z2, color) {
    positions.push(x1, 0, z1, x2, 0, z2);
    colors.push(color[0], color[1], color[2], color[0], color[1], color[2]);
}

function colorComponents(hex) {
    const color = new THREE.Color(hex);
    return [color.r, color.g, color.b];
}

export function fitCameraToScene() {
    const bounds = state.currentBounds && !state.currentBounds.isEmpty()
        ? state.currentBounds
        : boundsToBox3(state.lastScene && state.lastScene.grid && state.lastScene.grid.bounds);
    if (!bounds || bounds.isEmpty()) return;
    const sphere = new THREE.Sphere();
    bounds.getBoundingSphere(sphere);
    const radius = Math.max(sphere.radius, 4);
    const direction = new THREE.Vector3(1, 0.72, 1).normalize();
    state.camera.position.copy(sphere.center).addScaledVector(direction, radius * 2.2);
    state.camera.near = Math.max(0.05, radius / 1000);
    state.camera.far = Math.max(2000, radius * 50);
    state.camera.updateProjectionMatrix();
    state.controls.target.copy(sphere.center);
    state.controls.update();
    if (state.cameraMode === "fly") syncFlyAnglesFromCamera();
    updateSunLightPosition();
}

export function updateSceneBounds(refit = false) {
    const contentBounds = sceneContentBounds();
    const fallbackBounds = transformedGridBounds() || state.currentBounds;
    const cameraBounds = unionBounds(contentBounds, state.contextBounds) || fallbackBounds;
    const floorBounds = floorGridBounds(contentBounds, fallbackBounds);
    state.currentBounds = cameraBounds;
    replaceFloorGrid(floorBounds, state.currentGridSize, state.currentFloorGridAlignment);
    updateSunLightPosition();
    if (refit) fitCameraToScene();
}

function floorGridBounds(contentBounds, fallbackBounds) {
    const bounds = state.contextBounds || contentBounds || fallbackBounds;
    if (!bounds || bounds.isEmpty() || !state.contextBounds) return bounds;

    const targetBounds = selectedGridContentBounds() || fallbackBounds;
    if (!targetBounds || targetBounds.isEmpty()) return bounds;

    const floorBounds = bounds.clone();
    floorBounds.min.y = targetBounds.min.y;
    floorBounds.max.y = targetBounds.max.y;
    return floorBounds;
}

export function updateLighting() {
    const lightingEnabled = !els.showLighting || els.showLighting.checked;
    if (state.ambientLight) state.ambientLight.intensity = lightingEnabled ? AMBIENT_WITH_LIGHTING : AMBIENT_WITHOUT_LIGHTING;
    if (state.sunLight) {
        state.sunLight.visible = lightingEnabled;
        state.sunLight.intensity = lightingEnabled ? Math.max(0.15, state.sunIntensity || 1) * SUN_LIGHT_INTENSITY_SCALE : 0;
    }
    if (state.gridLightGroup) state.gridLightGroup.visible = lightingEnabled;
    for (const light of state.gridLights || []) {
        light.visible = lightingEnabled;
        if (light.target) light.target.visible = lightingEnabled;
    }
    if (state.sunMarker) state.sunMarker.visible = lightingEnabled;
    if (state.sunMarkerLine) state.sunMarkerLine.visible = lightingEnabled;
}

export function updateSunLightPosition() {
    if (!state.sunLight) return;
    const bounds = sceneContentBounds() || state.currentBounds;
    const target = bounds ? bounds.getCenter(new THREE.Vector3()) : new THREE.Vector3();
    const direction = currentRelativeSunDirection();
    const markerDistance = bounds ? sunMarkerDistance(bounds) : 90;
    const lightDistance = bounds ? sunDirectionalLightDistance(bounds) : 1000;
    const markerPosition = target.clone().addScaledVector(direction, markerDistance);
    const lightPosition = target.clone().addScaledVector(direction, lightDistance);

    state.sunLight.position.copy(lightPosition);
    if (state.sunLightTarget) {
        state.sunLightTarget.position.copy(target);
        state.sunLightTarget.updateMatrixWorld();
    }
    configureSunShadow(bounds, lightPosition, target);
    state.sunLight.updateMatrixWorld();
    updateSunMarker(markerPosition, target);
}

export function disposeObjectTree(root) {
    root.traverse(object => {
        if (object.geometry) object.geometry.dispose();
        if (object.material) {
            const materials = Array.isArray(object.material) ? object.material : [object.material];
            for (const material of materials) material.dispose();
        }
    });
}

export function setCameraMode(mode) {
    const nextMode = mode === "fly" ? "fly" : "orbit";
    if (els.cameraMode) els.cameraMode.value = nextMode;
    if (state.cameraMode === nextMode) {
        updateCameraHint();
        return;
    }

    state.cameraMode = nextMode;
    state.flyKeys.clear();
    state.lastFrameTime = 0;
    state.controls.enabled = nextMode === "orbit";
    if (nextMode === "fly") {
        syncFlyAnglesFromCamera();
        applyFlyCameraRotation();
    } else {
        if (document.pointerLockElement === state.renderer.domElement) document.exitPointerLock();
        syncOrbitTargetFromCamera();
        state.controls.update();
    }
    updateCameraHint();
}

function updateCameraHint() {
    if (!els.cameraHint) return;
    if (state.cameraMode === "fly") {
        const locked = document.pointerLockElement === state.renderer.domElement;
        els.cameraHint.textContent = locked ? "Free fly: WASD to move, mouse to look, Esc to release" : "Free fly: click viewport to capture mouse, WASD to move";
        els.cameraHint.classList.toggle("is-active", locked);
        return;
    }

    els.cameraHint.textContent = "Orbit mode";
    els.cameraHint.classList.remove("is-active");
}

function resize() {
    const rect = els.viewport.getBoundingClientRect();
    const width = Math.max(1, Math.floor(rect.width));
    const height = Math.max(1, Math.floor(rect.height));
    state.camera.aspect = width / height;
    state.camera.updateProjectionMatrix();
    state.renderer.setSize(width, height, false);
}

function createSunMarker() {
    const geometry = new THREE.SphereGeometry(1, 24, 24);
    const material = new THREE.MeshBasicMaterial({ color: 0xfacc15, depthTest: false, depthWrite: false });
    const marker = new THREE.Mesh(geometry, material);
    marker.name = "SunPositionMarker";
    marker.frustumCulled = false;
    marker.renderOrder = 20;
    marker.position.copy(state.sunLight.position);
    return marker;
}

function createSunMarkerLine() {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute([0, 0, 0, 0, 0, 0], 3));
    const material = new THREE.LineBasicMaterial({ color: 0xfacc15, transparent: true, opacity: 0.38, depthTest: false, depthWrite: false });
    const line = new THREE.Line(geometry, material);
    line.name = "SunDirectionLine";
    line.frustumCulled = false;
    line.renderOrder = 19;
    return line;
}

function currentRelativeSunDirection() {
    const source = state.sunDirection && state.sunDirection.lengthSq() > 0
        ? state.sunDirection.clone().normalize()
        : new THREE.Vector3(0.33946735, 0.70979536, -0.61721337).normalize();
    if (state.viewRotation) source.applyMatrix4(state.viewRotation).normalize();
    return source;
}

function sunMarkerDistance(bounds) {
    const size = bounds.getSize(new THREE.Vector3());
    return Math.max(size.x, size.y, size.z, state.currentGridSize * 8, 10);
}

function sunDirectionalLightDistance(bounds) {
    const size = bounds.getSize(new THREE.Vector3());
    return Math.max(size.length() * 2, 200);
}

function configureSunShadow(bounds, lightPosition, target) {
    if (!state.sunLight || !state.sunLight.shadow || !state.sunLight.shadow.camera) return;
    const camera = state.sunLight.shadow.camera;
    const shadowBounds = bounds && !bounds.isEmpty()
        ? bounds
        : new THREE.Box3().setFromCenterAndSize(target, new THREE.Vector3(100, 100, 100));
    const up = shadowCameraUp(lightPosition, target);
    const lightView = new THREE.Matrix4().lookAt(lightPosition, target, up);
    lightView.setPosition(lightPosition);
    lightView.invert();
    const points = boxCorners(shadowBounds);
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;
    let minDistance = Infinity;
    let maxDistance = -Infinity;

    for (const point of points) {
        point.applyMatrix4(lightView);
        minX = Math.min(minX, point.x);
        maxX = Math.max(maxX, point.x);
        minY = Math.min(minY, point.y);
        maxY = Math.max(maxY, point.y);
        const distance = -point.z;
        minDistance = Math.min(minDistance, distance);
        maxDistance = Math.max(maxDistance, distance);
    }

    const size = shadowBounds.getSize(new THREE.Vector3()).length();
    const padding = Math.max(state.currentGridSize || LARGE_GRID_CUBE_SIZE, size * SUN_SHADOW_PADDING_SCALE);
    camera.up.copy(up);
    camera.left = minX - padding;
    camera.right = maxX + padding;
    camera.bottom = minY - padding;
    camera.top = maxY + padding;
    camera.near = Math.max(0.5, minDistance - padding);
    camera.far = Math.max(camera.near + 1, maxDistance + padding);
    configureSunShadowBias(camera);
    camera.updateProjectionMatrix();
}

function configureSunShadowBias(camera) {
    const shadowWidth = Math.max(camera.right - camera.left, camera.top - camera.bottom);
    const texelSize = shadowWidth / SUN_SHADOW_MAP_SIZE;
    const maxNormalBias = Math.max(SUN_SHADOW_MIN_NORMAL_BIAS, (state.currentGridSize || LARGE_GRID_CUBE_SIZE) * 0.25);
    state.sunLight.shadow.bias = 0;
    state.sunLight.shadow.normalBias = clamp(texelSize * SUN_SHADOW_TEXEL_NORMAL_BIAS, SUN_SHADOW_MIN_NORMAL_BIAS, maxNormalBias);
}

function shadowCameraUp(lightPosition, target) {
    const direction = target.clone().sub(lightPosition).normalize();
    const up = Math.abs(direction.y) > 0.92 ? new THREE.Vector3(0, 0, 1) : new THREE.Vector3(0, 1, 0);
    return up;
}

function boxCorners(box) {
    return [
        new THREE.Vector3(box.min.x, box.min.y, box.min.z),
        new THREE.Vector3(box.min.x, box.min.y, box.max.z),
        new THREE.Vector3(box.min.x, box.max.y, box.min.z),
        new THREE.Vector3(box.min.x, box.max.y, box.max.z),
        new THREE.Vector3(box.max.x, box.min.y, box.min.z),
        new THREE.Vector3(box.max.x, box.min.y, box.max.z),
        new THREE.Vector3(box.max.x, box.max.y, box.min.z),
        new THREE.Vector3(box.max.x, box.max.y, box.max.z),
    ];
}

function updateSunMarker(position, target) {
    const distance = position.distanceTo(target);
    const markerSize = Math.min(16, Math.max(2.5, distance * 0.05));
    if (state.sunMarker) {
        state.sunMarker.position.copy(position);
        state.sunMarker.scale.setScalar(markerSize);
        state.sunMarker.updateMatrixWorld();
    }
    if (state.sunMarkerLine) {
        const positions = state.sunMarkerLine.geometry.getAttribute("position");
        positions.setXYZ(0, position.x, position.y, position.z);
        positions.setXYZ(1, target.x, target.y, target.z);
        positions.needsUpdate = true;
        state.sunMarkerLine.geometry.computeBoundingSphere();
    }
}

function objectWorldBounds(object) {
    if (!object || object.visible === false) return null;
    object.updateMatrixWorld(true);
    const bounds = new THREE.Box3().setFromObject(object);
    return bounds.isEmpty() ? null : bounds;
}

function sceneContentBounds() {
    const bounds = new THREE.Box3();
    let hasBounds = false;
    const objects = state.lastScene && !(state.lastScene.blockInstances || []).length
        ? [state.voxelGroup]
        : [state.gridGroup];
    for (const object of objects) {
        const objectBounds = objectWorldBounds(object);
        if (!objectBounds) continue;
        bounds.union(objectBounds);
        hasBounds = true;
    }
    return hasBounds ? bounds : null;
}

function selectedGridContentBounds() {
    if (!state.gridGroup) return null;
    const primaryId = String(state.primaryGridId || "");
    const children = state.gridGroup.children || [];
    const selected = children.find(child => primaryId && String(child.userData && child.userData.gridId || "") === primaryId)
        || children.find(child => child.name && child.name.startsWith("PrimaryGrid:"))
        || (children.length === 1 ? children[0] : null);
    return objectWorldBounds(selected);
}

function unionBounds(...items) {
    const bounds = new THREE.Box3();
    let hasBounds = false;
    for (const item of items) {
        if (!item || item.isEmpty()) continue;
        bounds.union(item);
        hasBounds = true;
    }
    return hasBounds ? bounds : null;
}

function transformedGridBounds() {
    const bounds = boundsToBox3(state.lastScene && state.lastScene.grid && state.lastScene.grid.bounds);
    if (!bounds || bounds.isEmpty()) return null;
    return bounds.applyMatrix4(state.viewTransform || new THREE.Matrix4());
}

function onPointerMove(event) {
    if (state.cameraMode === "fly" && document.pointerLockElement === state.renderer.domElement) return;
    const rect = state.renderer.domElement.getBoundingClientRect();
    state.pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
    state.pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
    state.raycaster.setFromCamera(state.pointer, state.camera);
    const targets = [];
    if (state.gridGroup) targets.push(state.gridGroup);
    if (state.voxelGroup && state.voxelGroup.visible) targets.push(state.voxelGroup);
    const hits = state.raycaster.intersectObjects(targets, true);
    const logisticsHit = state.logisticsGroup && state.logisticsGroup.visible ? hits.find(item => logisticsFromIntersection(item)) : null;
    if (logisticsHit) {
        const logistics = logisticsFromIntersection(logisticsHit);
        updateLogisticsFocus(logisticsFocus(logistics));
        updateDamagedFocus(null);
        els.hoverReadout.textContent = describeLogistics(logistics);
        return;
    }
    updateLogisticsFocus(null);
    const damagedHit = state.damagedGroup && state.damagedGroup.visible ? hits.find(item => damagedItemFromIntersection(item)) : null;
    if (damagedHit) {
        const item = damagedItemFromIntersection(damagedHit);
        updateDamagedFocus(damagedFocus(item));
        els.hoverReadout.textContent = describeDamagedItem(item);
        return;
    }
    updateDamagedFocus(null);
    const hit = hits.find(item => blockFromIntersection(item));
    if (hit) {
        els.hoverReadout.textContent = describeBlock(blockFromIntersection(hit));
        return;
    }
    const voxelHit = hits.find(item => item.object.userData && item.object.userData.voxel);
    els.hoverReadout.textContent = voxelHit ? describeVoxel(voxelHit.object.userData.voxel) : "No block or voxel selected";
}

function damagedBlockFromIntersection(hit) {
    let object = hit && hit.object;
    while (object) {
        const userData = object.userData;
        if (userData && userData.damagedBlock) return userData.damagedBlock;
        object = object.parent;
    }
    const block = blockFromIntersection(hit);
    return isProjectorDamagedBlock(block) ? block : null;
}

function damagedItemFromIntersection(hit) {
    let object = hit && hit.object;
    while (object) {
        const userData = object.userData;
        if (userData && userData.damagedVoxel) return { kind: "voxel", value: userData.damagedVoxel };
        if (userData && userData.damagedBlock) return { kind: "block", value: userData.damagedBlock };
        object = object.parent;
    }
    const block = blockFromIntersection(hit);
    return isProjectorDamagedBlock(block) ? { kind: "block", value: block } : null;
}

function blockFromIntersection(hit) {
    const userData = hit && hit.object && hit.object.userData;
    if (!userData) return null;
    if (userData.block) return userData.block;
    if (userData.blocks && hit.instanceId != null) return userData.blocks[hit.instanceId] || null;
    return null;
}

function logisticsFromIntersection(hit) {
    let object = hit && hit.object;
    while (object) {
        const userData = object.userData;
        if (userData && userData.logisticsNode) return { kind: "node", value: userData.logisticsNode };
        if (userData && userData.logisticsEdge) return { kind: "edge", value: userData.logisticsEdge };
        object = object.parent;
    }
    return null;
}

function describeBlock(block) {
    return `${block.blockTypeId || "Block"} | ${block.id || "no id"} | ${block.cell ? `${block.cell.x},${block.cell.y},${block.cell.z}` : "no cell"}`;
}

function describeDamagedBlock(block) {
    const percent = Math.round(damageRatio(block) * 100);
    return `Damaged ${block && block.blockTypeId || "Block"} | ${percent}% damage | ${block && block.id || "no id"}`;
}

function describeDamagedItem(item) {
    if (item && item.kind === "voxel") {
        const voxel = item.value || {};
        return `Voxel deformation | ${voxel.displayName || voxel.id || "no id"}`;
    }
    return describeDamagedBlock(item && item.value);
}

function damageRatio(block) {
    const max = Number(block && block.maxIntegrity) || 0;
    if (max <= 0) return 0;
    const buildLevel = Number(block && block.buildLevel);
    const buildIntegrity = clamp((Number.isFinite(buildLevel) ? buildLevel : 1) * max, 0, max);
    const integrity = Number(block && block.integrity);
    const current = Number.isFinite(integrity) ? integrity : buildIntegrity;
    const currentDamage = Math.max(0, buildIntegrity - current);
    const accumulatedDamage = Math.max(0, Number(block && block.accumulatedDamage) || 0);
    const unfinishedDamage = isProjectorUnfinishedBlock(block) ? Math.max(0, max - buildIntegrity) : 0;
    return clamp(Math.max(currentDamage, accumulatedDamage, unfinishedDamage) / max, 0, 1);
}

function isProjectorDamagedBlock(block) {
    return isProjectorUnfinishedBlock(block)
        || (Number(block && block.accumulatedDamage) || 0) > 0
        || currentDamage(block) > 0;
}

function isProjectorUnfinishedBlock(block) {
    const buildLevel = Number(block && block.buildLevel);
    const level = Number.isFinite(buildLevel) ? buildLevel : 1;
    return level > 0 && level < 1;
}

function currentDamage(block) {
    const max = Number(block && block.maxIntegrity) || 0;
    if (max <= 0) return 0;
    const buildLevel = Number(block && block.buildLevel);
    const buildIntegrity = clamp((Number.isFinite(buildLevel) ? buildLevel : 1) * max, 0, max);
    const integrity = Number(block && block.integrity);
    const current = Number.isFinite(integrity) ? integrity : buildIntegrity;
    return Math.max(0, buildIntegrity - current);
}

function describeVoxel(voxel) {
    return `${voxel.kind || "voxel"} | ${voxel.displayName || voxel.id || "no id"}`;
}

function describeLogistics(item) {
    const value = item && item.value || {};
    if (item && item.kind === "edge") {
        const kind = value.isDangling ? "Open conveyor path" : "Logistics edge";
        return `${kind} | system ${value.systemId ?? "?"} | ${value.lineType || "unknown"}${value.isWorking === false ? " | offline" : ""}`;
    }
    return `Logistics ${value.role || "node"} | system ${value.systemId ?? "?"} | ${value.blockTypeId || value.blockId || "no block"}${value.isWorking === false ? " | offline" : ""}`;
}

function logisticsFocus(item) {
    if (!item || !item.value) return null;
    if (item.kind === "node" && !(Number(item.value.conveyorPortCount) > 0)) {
        const gridId = String(item.value.gridId || "");
        return gridId ? { kind: "conveyorless-grid", gridId, key: `conveyorless-grid:${gridId}` } : null;
    }
    const id = Number(item.value.systemId);
    return Number.isFinite(id) && id >= 0 ? { kind: "system", systemId: id, key: `system:${id}` } : null;
}

function updateLogisticsFocus(focus) {
    const focusKey = focus && focus.key || "";
    if (!state.logisticsGroup || state.logisticsGroup.userData.focusKey === focusKey) return;
    state.logisticsGroup.userData.focusKey = focusKey;
    state.logisticsGroup.traverse(object => {
        const focused = isLogisticsFocusMatch(object, focus);
        const factor = !focus ? 0.72 : focused ? 1 : 0.16;
        applyLogisticsOpacityFactor(object.material, factor);
    });
}

function updateDamagedFocus(focus) {
    const focusKey = focus && focus.key || "";
    if (!state.damagedGroup || state.damagedGroup.userData.focusKey === focusKey) return;
    state.damagedGroup.userData.focusKey = focusKey;
    state.damagedGroup.traverse(object => {
        const focused = isDamagedFocusMatch(object, focus);
        const factor = !focus ? 0.72 : focused ? 1 : 0.16;
        applyDamagedOpacityFactor(object.material, factor);
    });
}

function damagedFocus(item) {
    if (!item || !item.value) return null;
    if (item.kind === "voxel") {
        const bodyId = String(item.value.id || "");
        return bodyId ? { kind: "voxel", bodyId, key: `voxel:${bodyId}` } : null;
    }
    const gridId = String(item.value.gridId || "");
    return gridId ? { kind: "grid", gridId, key: `grid:${gridId}` } : null;
}

function isDamagedFocusMatch(object, focus) {
    if (!focus) return false;
    const userData = object.userData || {};
    if (focus.kind === "voxel") return String(userData.damagedVoxelBodyId || "") === focus.bodyId;
    if (focus.kind === "grid") return String(userData.damagedGridId || "") === focus.gridId;
    return false;
}

function applyDamagedOpacityFactor(material, factor) {
    if (!material) return;
    if (Array.isArray(material)) {
        for (const item of material) applyDamagedOpacityFactor(item, factor);
        return;
    }
    const baseOpacity = material.userData && material.userData.damagedBaseOpacity;
    if (!Number.isFinite(baseOpacity)) return;
    material.opacity = baseOpacity * factor;
    material.needsUpdate = true;
}

function isLogisticsFocusMatch(object, focus) {
    if (!focus) return false;
    const userData = object.userData || {};
    if (focus.kind === "system") {
        const objectSystemId = Number(userData.logisticsSystemId);
        return Number.isFinite(objectSystemId) && objectSystemId === focus.systemId;
    }
    if (focus.kind === "conveyorless-grid") {
        const node = userData.logisticsNode;
        return !!node
            && !(Number(node.conveyorPortCount) > 0)
            && String(userData.logisticsGridId || node.gridId || "") === focus.gridId;
    }
    return false;
}

function applyLogisticsOpacityFactor(material, factor) {
    if (!material) return;
    if (Array.isArray(material)) {
        for (const item of material) applyLogisticsOpacityFactor(item, factor);
        return;
    }
    const baseOpacity = material.userData && material.userData.logisticsBaseOpacity;
    if (!Number.isFinite(baseOpacity)) return;
    material.opacity = baseOpacity * factor;
    material.needsUpdate = true;
}

function onViewportClick() {
    if (state.cameraMode !== "fly" || document.pointerLockElement === state.renderer.domElement) return;
    const request = state.renderer.domElement.requestPointerLock && state.renderer.domElement.requestPointerLock();
    if (request && typeof request.catch === "function") request.catch(error => {
        if (els.cameraHint) els.cameraHint.textContent = `Pointer lock unavailable: ${error.message}`;
    });
}

function onFlyPointerMove(event) {
    if (state.cameraMode !== "fly" || document.pointerLockElement !== state.renderer.domElement) return;
    state.flyYaw -= event.movementX * FLY_MOUSE_SENSITIVITY;
    state.flyPitch = clamp(state.flyPitch - event.movementY * FLY_MOUSE_SENSITIVITY, -FLY_PITCH_LIMIT, FLY_PITCH_LIMIT);
    applyFlyCameraRotation();
}

function syncFlyAnglesFromCamera() {
    const forward = new THREE.Vector3();
    state.camera.getWorldDirection(forward);
    state.flyPitch = Math.asin(clamp(forward.y, -1, 1));
    state.flyYaw = Math.atan2(-forward.x, -forward.z);
}

function applyFlyCameraRotation() {
    state.camera.rotation.order = "YXZ";
    state.camera.rotation.set(state.flyPitch, state.flyYaw, 0, "YXZ");
}

function syncOrbitTargetFromCamera() {
    const forward = new THREE.Vector3();
    state.camera.getWorldDirection(forward);
    const currentDistance = state.camera.position.distanceTo(state.controls.target);
    const distance = Number.isFinite(currentDistance) && currentDistance > 0.001 ? currentDistance : 25;
    state.controls.target.copy(state.camera.position).addScaledVector(forward, distance);
}

function updateFlyMovement(delta) {
    if (!delta || !state.flyKeys.size) return;
    const direction = new THREE.Vector3();
    const forward = new THREE.Vector3();
    const right = new THREE.Vector3();
    state.camera.getWorldDirection(forward);
    right.setFromMatrixColumn(state.camera.matrixWorld, 0).normalize();
    if (state.flyKeys.has("KeyW")) direction.add(forward);
    if (state.flyKeys.has("KeyS")) direction.sub(forward);
    if (state.flyKeys.has("KeyD")) direction.add(right);
    if (state.flyKeys.has("KeyA")) direction.sub(right);
    if (direction.lengthSq() > 0) {
        const fast = state.flyKeys.has("ShiftLeft") || state.flyKeys.has("ShiftRight");
        state.camera.position.addScaledVector(direction.normalize(), FLY_BASE_SPEED * (fast ? FLY_FAST_MULTIPLIER : 1) * delta);
    }
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function updateRenderStats() {
    const info = state.renderer.info;
    state.stats["Draw calls"] = info.render.calls;
    state.stats.Triangles = info.render.triangles;
    state.stats.Lines = info.render.lines;
    state.stats.Points = info.render.points;
    state.stats.Geometries = info.memory.geometries;
    state.stats["GPU textures"] = info.memory.textures;
    state.stats.Programs = info.programs ? info.programs.length : 0;
    Object.assign(state.stats, collectVisibilityStats());
    renderStats();
}

function collectVisibilityStats() {
    const frustum = new THREE.Frustum();
    const projection = new THREE.Matrix4().multiplyMatrices(state.camera.projectionMatrix, state.camera.matrixWorldInverse);
    frustum.setFromProjectionMatrix(projection);

    const stats = { Renderables: 0, Visible: 0, Culled: 0, Meshes: 0, Sprites: 0, Lights: 0 };
    state.scene.updateMatrixWorld(true);
    traverseVisible(state.scene, true, object => {
        if (object.isLight) stats.Lights++;
        const renderable = object.isMesh || object.isLine || object.isPoints || object.isSprite;
        if (!renderable) return;
        stats.Renderables++;
        if (object.isMesh) stats.Meshes++;
        if (object.isSprite) stats.Sprites++;
        if (isObjectCulled(object, frustum)) stats.Culled++;
        else stats.Visible++;
    });
    return stats;
}

function traverseVisible(object, parentVisible, visitor) {
    const visible = parentVisible && object.visible !== false;
    if (visible) visitor(object);
    for (const child of object.children) traverseVisible(child, visible, visitor);
}

function isObjectCulled(object, frustum) {
    if (!object.frustumCulled) return false;
    if (object.isSprite) return !frustum.containsPoint(object.getWorldPosition(new THREE.Vector3()));
    if (object.boundingSphere) {
        const sphere = object.boundingSphere.clone().applyMatrix4(object.matrixWorld);
        return !frustum.intersectsSphere(sphere);
    }
    const geometry = object.geometry;
    if (!geometry) return false;
    if (!geometry.boundingSphere) geometry.computeBoundingSphere();
    if (!geometry.boundingSphere) return false;
    const sphere = geometry.boundingSphere.clone().applyMatrix4(object.matrixWorld);
    return !frustum.intersectsSphere(sphere);
}

function renderStats() {
    els.stats.innerHTML = "";
    for (const [key, value] of Object.entries(state.stats)) {
        const dt = document.createElement("dt");
        const dd = document.createElement("dd");
        dt.textContent = key;
        dd.textContent = typeof value === "number" ? value.toLocaleString() : String(value);
        els.stats.append(dt, dd);
    }
}
