// One core client per page, using the same public SDK as plugins.
(() => {
    let loading;
    const client = () => loading ||= import(new URL("_quasar/sdk/v1/client.js", document.baseURI).href)
        .then(module => module.createClient({ baseUrl: document.baseURI }));
    window.quasarSdk = Object.freeze({
        async request(...args) { return (await client()).request(...args); },
        async download(...args) { return (await client()).download(...args); },
    });
})();
