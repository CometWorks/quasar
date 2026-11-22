const transfers = new Map();

// Leave a File backed by browser storage. The SDK streams it on the selected route.
export async function write(id, operation, target, document, input, name, size) {
    const controller = new AbortController();
    if (transfers.has(id)) throw new Error("This world transfer is already running.");
    transfers.set(id, controller);
    let handle;
    try {
        let body;
        if (input) {
            const file = input.files[0];
            if (!file || file.name !== name || file.size !== size) throw new Error("The selected world ZIP changed. Select it again.");
            body = file;
        } else {
            body = new Blob([JSON.stringify(document)], { type: "application/json" });
        }
        const sdk = await window.quasarSdk.getClient();
        handle = await sdk.openWrite("quasar.core", operation, target, { signal: controller.signal });
        return await handle.write(body);
    } finally {
        transfers.delete(id);
        if (handle) await handle.dispose();
    }
}

export function cancel(id) { transfers.get(id)?.abort(); }
