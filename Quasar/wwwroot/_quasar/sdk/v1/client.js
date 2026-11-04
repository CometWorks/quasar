export class QuasarSdkError extends Error {
    constructor(status, detail) {
        super(detail);
        this.name = "QuasarSdkError";
        this.status = status;
    }
}

export function createClient({ baseUrl }) {
    const base = new URL(baseUrl, globalThis.location?.href);
    if (base.origin !== globalThis.location?.origin || !base.pathname.endsWith("/") || base.search || base.hash || base.username || base.password) {
        throw new TypeError("An explicit same-origin panel base URL ending in / is required.");
    }
    const root = new URL("_quasar/sdk/v1/", base);
    const identifier = /^[a-z0-9]+(?:[._-][a-z0-9]+)*$/;

    async function responseJson(response) {
        if (!response.ok) {
            const error = await response.json().catch(() => ({}));
            throw new QuasarSdkError(response.status, error.detail || error.title || "Quasar SDK request failed.");
        }
        return response.json();
    }

    return Object.freeze({
        async request(pluginId, operationId, payload = {}, { signal } = {}) {
            if (![pluginId, operationId].every(value => typeof value === "string" && value.length <= 128 && identifier.test(value))) {
                throw new TypeError("Invalid plugin or operation identifier.");
            }
            const body = JSON.stringify(payload);
            if (body === undefined || new TextEncoder().encode(body).length > 64 * 1024) {
                throw new TypeError("SDK control requests must contain at most 64 KiB of JSON.");
            }
            const session = await responseJson(await fetch(new URL("session", root), {
                credentials: "same-origin", redirect: "error", cache: "no-store", signal,
            }));
            if (session.version !== 1 || !session.headerName || !session.requestToken) {
                throw new QuasarSdkError(426, "Incompatible Quasar SDK session.");
            }
            return responseJson(await fetch(new URL(`operations/${pluginId}/${operationId}`, root), {
                method: "POST", credentials: "same-origin", redirect: "error", cache: "no-store", signal,
                headers: { "Content-Type": "application/json", [session.headerName]: session.requestToken }, body,
            }));
        },
    });
}
