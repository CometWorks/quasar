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
            if (operation === "server-import.zip") {
                const {openFile} = await import(new URL("server-import-file.js", window.document.baseURI));
                const selected = openFile(input);
                const sdk = await window.quasarSdk.getClient();
                const signal = controller.signal;
                const open = await sdk.pinUploads({signal});
                let range = await sdk.request("quasar.core", "server-import.zip.start", {
                    target, size: file.size, directoryOffset: await selected.directoryOffset()
                }, {signal});
                let sequence = 0;
                while (range?.sequence !== 0) {
                    if (!range || range.sequence !== ++sequence) throw new Error("Unexpected server ZIP range sequence.");
                    const slice = selected.readRange(range.offset, range.length);
                    handle = await open("quasar.core", "server-import.zip.range", {target, sequence});
                    const previous = handle;
                    try { range = await handle.write(slice); }
                    finally { handle = null; await previous.dispose(); }
                }
                return true;
            }
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
