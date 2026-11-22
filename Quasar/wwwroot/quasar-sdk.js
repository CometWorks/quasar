// One core client per page, using the same public SDK as plugins.
(() => {
    let loading;
    const client = () => loading ||= import(new URL("_quasar/sdk/v1/client.js", document.baseURI).href)
        .then(module => module.createClient({ baseUrl: document.baseURI }));
    window.quasarSdk = Object.freeze({
        async request(...args) { return (await client()).request(...args); },
        async uploadFile(plugin, operation, input, name, size) {
            const file = input.files[0];
            if (!file || file.name !== name || file.size !== size)
                throw new Error("The selected backup changed. Select it again.");
            const handle = await (await client()).openWrite(plugin, operation);
            try { return await handle.write(file); }
            finally { await handle.dispose(); }
        },
        async download(...args) { return (await client()).download(...args); },
    });
})();
