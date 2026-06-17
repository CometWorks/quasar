export function getViewerParams() {
    const params = new URLSearchParams(window.location.search);
    const agentId = params.get("agentId") || "";
    const entityId = params.get("entityId") || "";
    if (!agentId || !entityId) throw new Error("Viewer URL must include agentId and entityId.");
    return { agentId, entityId };
}

export async function fetchEntityScene() {
    const { agentId, entityId } = getViewerParams();
    const response = await fetch(`/api/viewer/entities/${encodeURIComponent(agentId)}/${encodeURIComponent(entityId)}/scene`, {
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
