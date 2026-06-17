import { resolveContentFile } from "./content-folder.js";

export async function resolveTextureAsset(asset) {
    if (!asset || !asset.logicalPath) return null;
    return await resolveContentFile(asset.logicalPath);
}
