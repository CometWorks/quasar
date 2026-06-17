import * as THREE from "three";
import { vec3 } from "./math.js";

export function boundsToBox3(bounds) {
    if (!bounds) return new THREE.Box3();
    return new THREE.Box3(vec3(bounds.min), vec3(bounds.max));
}

export function blockBox(instance, gridSize) {
    const min = instance.min || instance.cell || { x: 0, y: 0, z: 0 };
    const max = instance.max || instance.cell || min;
    const half = gridSize * 0.5;
    return new THREE.Box3(
        new THREE.Vector3(min.x * gridSize - half, min.y * gridSize - half, min.z * gridSize - half),
        new THREE.Vector3(max.x * gridSize + half, max.y * gridSize + half, max.z * gridSize + half)
    );
}

export function createBoxMesh(box, material) {
    const size = new THREE.Vector3();
    const center = new THREE.Vector3();
    box.getSize(size);
    box.getCenter(center);
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(Math.max(size.x, 0.05), Math.max(size.y, 0.05), Math.max(size.z, 0.05)), material);
    mesh.position.copy(center);
    return mesh;
}
