import * as THREE from "three";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { els, state } from "./state.js";
import { boundsToBox3 } from "./geometry.js";

const SMALL_GRID_CUBE_SIZE = 0.5;
const LARGE_GRID_CUBE_SIZE = 2.5;
const FLOOR_GRID_DEFAULT_SIZE = 240;
const FLOOR_GRID_PADDING_SUPERSQUARES = 2;
const FLOOR_GRID_MAX_MINOR_CELLS_PER_AXIS = 20000;

export function initScene() {
    state.scene = new THREE.Scene();
    state.scene.background = new THREE.Color(0x070b12);
    state.scene.fog = new THREE.FogExp2(0x070b12, 0.0025);

    state.camera = new THREE.PerspectiveCamera(55, 1, 0.05, 200000);
    state.camera.position.set(28, 24, 32);

    state.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
    state.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.75));
    state.renderer.outputColorSpace = THREE.SRGBColorSpace;
    els.viewport.appendChild(state.renderer.domElement);

    const pmrem = new THREE.PMREMGenerator(state.renderer);
    state.scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
    state.scene.environmentIntensity = 0.45;
    pmrem.dispose();

    state.controls = new OrbitControls(state.camera, state.renderer.domElement);
    state.controls.enableDamping = true;
    state.controls.dampingFactor = 0.08;

    state.ambientLight = new THREE.HemisphereLight(0xcfe7ff, 0x172033, 0.9);
    state.scene.add(state.ambientLight);
    state.sunLight = new THREE.DirectionalLight(0xffffff, 1.9);
    state.sunLight.position.set(40, 70, 35);
    state.sunTarget = state.sunLight.target;
    state.scene.add(state.sunLight);
    state.scene.add(state.sunTarget);

    state.sunMarker = createSunMarker();
    state.sunMarkerLine = createSunMarkerLine();
    state.scene.add(state.sunMarker);
    state.scene.add(state.sunMarkerLine);
    updateLighting();
    replaceFloorGrid(null, 2.5);

    state.raycaster = new THREE.Raycaster();
    state.pointer = new THREE.Vector2();
    state.renderer.domElement.addEventListener("pointermove", onPointerMove);

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

export function replaceFloorGrid(bounds, gridSize) {
    const visible = state.floorGrid ? state.floorGrid.visible : true;
    if (state.floorGrid) {
        state.scene.remove(state.floorGrid);
        disposeObjectTree(state.floorGrid);
    }

    state.floorGrid = createFloorGrid(bounds, gridSize);
    state.floorGrid.visible = visible && (!els.showGridHelper || els.showGridHelper.checked);
    state.scene.add(state.floorGrid);
}

function createFloorGrid(bounds, gridSize) {
    const layout = floorGridLayout(bounds, gridSize);
    const positions = [];
    const colors = [];
    const minorColor = colorComponents(0x1e293b);
    const majorColor = colorComponents(0x2563eb);
    const axisColor = colorComponents(0x6ee7f9);
    const majorEveryCells = Math.max(1, Math.round(layout.majorStep / SMALL_GRID_CUBE_SIZE));
    const xCellCount = layout.endXCell - layout.startXCell;
    const zCellCount = layout.endZCell - layout.startZCell;
    const drawMinor = xCellCount <= FLOOR_GRID_MAX_MINOR_CELLS_PER_AXIS && zCellCount <= FLOOR_GRID_MAX_MINOR_CELLS_PER_AXIS;
    const fallbackEveryCells = Math.max(
        majorEveryCells,
        Math.ceil(Math.max(xCellCount, zCellCount) / FLOOR_GRID_MAX_MINOR_CELLS_PER_AXIS)
    );
    const coarseEveryCells = Math.ceil(fallbackEveryCells / majorEveryCells) * majorEveryCells;

    appendFloorGridLines({
        positions,
        colors,
        startCell: layout.startXCell,
        endCell: layout.endXCell,
        rangeStart: layout.startZCell * SMALL_GRID_CUBE_SIZE - layout.originZ,
        rangeEnd: layout.endZCell * SMALL_GRID_CUBE_SIZE - layout.originZ,
        origin: layout.originX,
        axis: "x",
        drawMinor,
        majorEveryCells,
        coarseEveryCells,
        minorColor,
        majorColor,
        axisColor,
    });
    appendFloorGridLines({
        positions,
        colors,
        startCell: layout.startZCell,
        endCell: layout.endZCell,
        rangeStart: layout.startXCell * SMALL_GRID_CUBE_SIZE - layout.originX,
        rangeEnd: layout.endXCell * SMALL_GRID_CUBE_SIZE - layout.originX,
        origin: layout.originZ,
        axis: "z",
        drawMinor,
        majorEveryCells,
        coarseEveryCells,
        minorColor,
        majorColor,
        axisColor,
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
    return grid;
}

function floorGridLayout(bounds, gridSize) {
    const majorStep = Math.max(SMALL_GRID_CUBE_SIZE, Number(gridSize) || LARGE_GRID_CUBE_SIZE);
    if (!bounds || bounds.isEmpty()) {
        const halfSize = FLOOR_GRID_DEFAULT_SIZE * 0.5;
        return floorGridCells(-halfSize, halfSize, -halfSize, halfSize, -0.02, majorStep);
    }

    const padding = majorStep * FLOOR_GRID_PADDING_SUPERSQUARES;
    const y = bounds.min.y - Math.max(0.02, majorStep * 0.02);
    return floorGridCells(bounds.min.x - padding, bounds.max.x + padding, bounds.min.z - padding, bounds.max.z + padding, y, majorStep);
}

function floorGridCells(minX, maxX, minZ, maxZ, y, majorStep) {
    const startXCell = Math.floor(minX / SMALL_GRID_CUBE_SIZE);
    const endXCell = Math.ceil(maxX / SMALL_GRID_CUBE_SIZE);
    const startZCell = Math.floor(minZ / SMALL_GRID_CUBE_SIZE);
    const endZCell = Math.ceil(maxZ / SMALL_GRID_CUBE_SIZE);
    return {
        startXCell,
        endXCell,
        startZCell,
        endZCell,
        originX: (startXCell + endXCell) * SMALL_GRID_CUBE_SIZE * 0.5,
        originZ: (startZCell + endZCell) * SMALL_GRID_CUBE_SIZE * 0.5,
        y,
        majorStep,
    };
}

function appendFloorGridLines(options) {
    const step = options.drawMinor ? 1 : options.coarseEveryCells;
    const firstCell = Math.ceil(options.startCell / step) * step;
    for (let cell = firstCell; cell <= options.endCell; cell += step) {
        const coordinate = cell * SMALL_GRID_CUBE_SIZE - options.origin;
        const isAxis = cell === 0;
        const isMajor = cell % options.majorEveryCells === 0;
        const color = isAxis ? options.axisColor : isMajor ? options.majorColor : options.minorColor;
        if (options.axis === "x") {
            appendFloorGridLine(options.positions, options.colors, coordinate, options.rangeStart, coordinate, options.rangeEnd, color);
        } else {
            appendFloorGridLine(options.positions, options.colors, options.rangeStart, coordinate, options.rangeEnd, coordinate, color);
        }
    }
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
    updateSunLightPosition();
}

export function updateSceneBounds(refit = false) {
    state.currentBounds = objectWorldBounds(state.gridGroup) || boundsToBox3(state.lastScene && state.lastScene.grid && state.lastScene.grid.bounds);
    replaceFloorGrid(state.currentBounds, state.currentGridSize);
    updateSunLightPosition();
    if (refit) fitCameraToScene();
}

export function updateLighting() {
    const sunEnabled = !els.showSun || els.showSun.checked;
    const ambientIntensity = sunEnabled ? 0.9 : 1.55;
    const environmentIntensity = sunEnabled ? 0.45 : 0.72;
    if (state.ambientLight) state.ambientLight.intensity = ambientIntensity;
    if (state.scene) state.scene.environmentIntensity = environmentIntensity;
    if (state.sunLight) {
        state.sunLight.visible = sunEnabled;
        state.sunLight.intensity = sunEnabled ? Math.max(0.15, state.sunIntensity || 1) * 1.9 : 0;
    }
    if (state.sunMarker) state.sunMarker.visible = sunEnabled;
    if (state.sunMarkerLine) state.sunMarkerLine.visible = sunEnabled;
}

export function updateSunLightPosition() {
    if (!state.sunLight || !state.sunTarget) return;
    const bounds = objectWorldBounds(state.gridGroup) || state.currentBounds;
    const target = bounds ? bounds.getCenter(new THREE.Vector3()) : new THREE.Vector3();
    const direction = currentRelativeSunDirection();
    const distance = bounds ? sunMarkerDistance(bounds) : 90;
    const sunPosition = target.clone().addScaledVector(direction, distance);

    state.sunLight.position.copy(sunPosition);
    state.sunTarget.position.copy(target);
    state.sunTarget.updateMatrixWorld();
    state.sunLight.updateMatrixWorld();
    updateSunMarker(sunPosition, target);
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
    state.cameraMode = mode === "fly" ? "fly" : "orbit";
    state.controls.enabled = state.cameraMode === "orbit";
    if (els.cameraHint) els.cameraHint.textContent = state.cameraMode === "fly" ? "Free fly: WASD to move" : "Orbit mode";
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
        positions.setXYZ(0, target.x, target.y, target.z);
        positions.setXYZ(1, position.x, position.y, position.z);
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

function onPointerMove(event) {
    const rect = state.renderer.domElement.getBoundingClientRect();
    state.pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
    state.pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
    state.raycaster.setFromCamera(state.pointer, state.camera);
    const targets = [];
    if (state.gridGroup) targets.push(state.gridGroup);
    if (state.voxelGroup && state.voxelGroup.visible) targets.push(state.voxelGroup);
    const hits = state.raycaster.intersectObjects(targets, true);
    const hit = hits.find(item => item.object.userData && item.object.userData.block);
    if (hit) {
        els.hoverReadout.textContent = describeBlock(hit.object.userData.block);
        return;
    }
    const voxelHit = hits.find(item => item.object.userData && item.object.userData.voxel);
    els.hoverReadout.textContent = voxelHit ? describeVoxel(voxelHit.object.userData.voxel) : "No block or voxel selected";
}

function describeBlock(block) {
    return `${block.blockTypeId || "Block"} | ${block.id || "no id"} | ${block.cell ? `${block.cell.x},${block.cell.y},${block.cell.z}` : "no cell"}`;
}

function describeVoxel(voxel) {
    return `${voxel.kind || "voxel"} | ${voxel.displayName || voxel.id || "no id"}`;
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
    if (direction.lengthSq() > 0) state.camera.position.addScaledVector(direction.normalize(), 18 * delta);
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
