import * as THREE from "three";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { els, state } from "./state.js";
import { boundsToBox3 } from "./geometry.js";

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
    state.scene.add(state.sunLight);

    state.sunMarker = createSunMarker();
    state.scene.add(state.sunMarker);
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
    const box = bounds || new THREE.Box3(new THREE.Vector3(-120, 0, -120), new THREE.Vector3(120, 0, 120));
    const helper = new THREE.GridHelper(Math.max(80, box.getSize(new THREE.Vector3()).length()), 80, 0x2563eb, 0x1e293b);
    helper.position.y = box.min.y - Math.max(0.02, gridSize * 0.02);
    helper.visible = visible && (!els.showGridHelper || els.showGridHelper.checked);
    state.floorGrid = helper;
    state.scene.add(helper);
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
    const geometry = new THREE.SphereGeometry(3, 16, 16);
    const material = new THREE.MeshBasicMaterial({ color: 0xfacc15 });
    const marker = new THREE.Mesh(geometry, material);
    marker.position.copy(state.sunLight.position);
    return marker;
}

function onPointerMove(event) {
    const rect = state.renderer.domElement.getBoundingClientRect();
    state.pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
    state.pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
    state.raycaster.setFromCamera(state.pointer, state.camera);
    const hits = state.raycaster.intersectObjects(state.gridGroup ? state.gridGroup.children : [], true);
    const hit = hits.find(item => item.object.userData && item.object.userData.block);
    els.hoverReadout.textContent = hit ? describeBlock(hit.object.userData.block) : "No block selected";
}

function describeBlock(block) {
    return `${block.blockTypeId || "Block"} | ${block.id || "no id"} | ${block.cell ? `${block.cell.x},${block.cell.y},${block.cell.z}` : "no cell"}`;
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
    renderStats();
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
