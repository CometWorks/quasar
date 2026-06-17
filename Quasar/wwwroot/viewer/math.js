import * as THREE from "three";

export function matrixDtoToThree(matrix) {
    const m = new THREE.Matrix4();
    matrix = matrix || {};
    m.set(
        num(matrix.m11, 1), num(matrix.m21, 0), num(matrix.m31, 0), num(matrix.m41, 0),
        num(matrix.m12, 0), num(matrix.m22, 1), num(matrix.m32, 0), num(matrix.m42, 0),
        num(matrix.m13, 0), num(matrix.m23, 0), num(matrix.m33, 1), num(matrix.m43, 0),
        num(matrix.m14, 0), num(matrix.m24, 0), num(matrix.m34, 0), num(matrix.m44, 1)
    );
    return m;
}

export function vec3(value) {
    return new THREE.Vector3(num(value && value.x, 0), num(value && value.y, 0), num(value && value.z, 0));
}

export function colorFromHash(text) {
    let hash = 2166136261;
    for (let i = 0; i < String(text).length; i++) {
        hash ^= String(text).charCodeAt(i);
        hash = Math.imul(hash, 16777619);
    }
    return new THREE.Color().setHSL(((hash >>> 0) % 360) / 360, 0.38, 0.56);
}

export function num(value, fallback) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
}
