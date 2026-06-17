import { resolveContentFile } from "./content-folder.js";

export async function resolveModelAsset(asset) {
    if (!asset || !asset.logicalPath) return { status: "missing", message: "Model asset has no logical path." };
    const resolved = await resolveContentFile(asset.logicalPath);
    if (!resolved) return { status: "missing", message: `Missing local model: ${asset.logicalPath}` };

    // Browser-side MWM parsing is intentionally not implemented in this first pass.
    // The resolved file proves local availability; rendering falls back to proxy boxes.
    return {
        status: "proxy",
        logicalPath: resolved.logicalPath,
        byteLength: resolved.file.size,
        message: `Resolved ${asset.logicalPath} locally (${resolved.file.size} bytes), rendering proxy geometry until MWM parsing is added.`,
    };
}
