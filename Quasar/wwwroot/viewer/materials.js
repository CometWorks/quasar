import * as THREE from "three";
import { colorFromHash } from "./math.js";

const materialCache = new Map();

export function blockMaterial(key, opacity = 0.72) {
    const cacheKey = `${key}|${opacity}`;
    if (materialCache.has(cacheKey)) return materialCache.get(cacheKey);
    const color = colorFromHash(key || "block");
    const material = new THREE.MeshStandardMaterial({
        color,
        roughness: 0.78,
        metalness: 0.12,
        transparent: opacity < 1,
        opacity,
    });
    materialCache.set(cacheKey, material);
    return material;
}

export function wireMaterial(color = 0x6ee7f9) {
    const key = `wire|${color}`;
    if (materialCache.has(key)) return materialCache.get(key);
    const material = new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.75 });
    materialCache.set(key, material);
    return material;
}

export function disposeMaterialCache() {
    for (const material of materialCache.values()) material.dispose();
    materialCache.clear();
}
