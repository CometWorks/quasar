export function getViewerParams() {
    const params = new URLSearchParams(window.location.search);
    const agentId = params.get("agentId") || "";
    const entityId = params.get("entityId") || "";
    if (!agentId || !entityId) throw new Error("Viewer URL must include agentId and entityId.");
    return { agentId, entityId, voxels: parseVoxelFlag(params), context: parseContextFlag(params) };
}

export function parseVoxelFlag(params = new URLSearchParams(window.location.search)) {
    if (!params.has("voxels")) return { present: false, enabled: false };
    const value = (params.get("voxels") || "").trim().toLowerCase();
    return { present: true, enabled: value === "" || value === "1" || value === "true" || value === "yes" };
}

export function parseContextFlag(params = new URLSearchParams(window.location.search)) {
    if (!params.has("context")) return { present: false, enabled: false };
    const value = (params.get("context") || "").trim().toLowerCase();
    return { present: true, enabled: value === "" || value === "1" || value === "true" || value === "yes" };
}

export async function fetchEntityScene() {
    const { agentId, entityId, voxels, context } = getViewerParams();
    const query = new URLSearchParams({ voxels: voxels.enabled ? "1" : "0", context: context.enabled ? "1" : "0" });
    const response = await fetch(`/api/viewer/entities/${encodeURIComponent(agentId)}/${encodeURIComponent(entityId)}/scene?${query}`, {
        headers: { "Accept": "application/json" },
        credentials: "same-origin",
    });
    if (!response.ok) {
        let detail = response.statusText;
        try {
            const body = await response.json();
            detail = body.detail || body.title || detail;
        } catch {
        }
        throw new Error(`Scene request failed (${response.status}): ${detail}`);
    }
    return await response.json();
}
