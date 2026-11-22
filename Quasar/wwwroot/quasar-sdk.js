// One core client per page, using the same public SDK as plugins.
(() => {
    let loading;
    const client = () => loading ||= import(new URL("_quasar/sdk/v1/client.js", document.baseURI).href)
        .then(module => module.createClient({ baseUrl: document.baseURI }));
    window.quasarSdk = Object.freeze({
        getClient: client,
        async prepare() { return (await client()).prepare(); },
        async transportStatus() {
            const sdk = await client();
            return sdk.connecting ? "connecting" : sdk.transport.path;
        },
        dashboardNavigated() {
            document.querySelector(".quasar-shell")?.classList.remove("quasar-nav-open");
            for (const link of document.querySelectorAll(".quasar-nav-link")) {
                const active = new URL(link.href).pathname === location.pathname;
                link.classList.toggle("active", active);
                if (active) link.setAttribute("aria-current", "page"); else link.removeAttribute("aria-current");
            }
        },
        dashboardReady() { window.dispatchEvent(new Event("quasar-wasm-ready")); },
        darkMode() { return document.querySelector(".quasar-shell")?.dataset.theme !== "light"; },
        async request(...args) { return (await client()).request(...args); },
        async readText(plugin, operation, payload) {
            const handle = await (await client()).openRead(plugin, operation, payload);
            try { return await new Response(handle.body).text(); }
            finally { await handle.dispose(); }
        },
        async readJson(plugin, operation, payload) {
            const handle = await (await client()).openRead(plugin, operation, payload);
            try { return await new Response(handle.body).json(); }
            finally { await handle.dispose(); }
        },
        async uploadJson(plugin, operation, payload, document) {
            const handle = await (await client()).openWrite(plugin, operation, payload);
            try { return await handle.write(new Blob([document], { type: "application/json" })); }
            finally { await handle.dispose(); }
        },
        applyBranding(branding) {
            const shell = document.querySelector("#quasar-wasm-shell");
            if (!shell) return;
            shell.dataset.darkPalette = branding.darkPalette;
            shell.dataset.lightPalette = branding.lightPalette;
            const dark = shell.dataset.theme !== "light";
            shell.style.cssText = dark ? branding.darkPalette : branding.lightPalette;
            const logo = shell.querySelector(".quasar-brand-mark");
            logo.dataset.darkLogo = branding.darkLogo;
            logo.dataset.lightLogo = branding.lightLogo;
            logo.src = dark ? branding.darkLogo : branding.lightLogo;
            shell.querySelector(".quasar-brand strong").textContent = branding.appName;
            shell.querySelector(".quasar-brand span").textContent = branding.appSubtitle;
            const footer = shell.querySelector(".quasar-sidebar-footer strong");
            if (footer?.firstChild?.nodeType === Node.TEXT_NODE) footer.firstChild.textContent = branding.appName + " ";
            const icon = document.querySelector("link[rel~='icon']");
            if (icon) icon.href = branding.favicon;
        },
        async uploadText(plugin, operation, text) {
            const handle = await (await client()).openWrite(plugin, operation);
            try { return await handle.write(new Blob([text], { type: "application/json" })); }
            finally { await handle.dispose(); }
        },
        async uploadFile(plugin, operation, input, name, size, payload = {}) {
            const file = input.files[0];
            if (!file || file.name !== name || file.size !== size)
                throw new Error("The selected file changed. Select it again.");
            const handle = await (await client()).openWrite(plugin, operation, payload);
            try { return await handle.write(file); }
            finally { await handle.dispose(); }
        },
        async download(...args) { return (await client()).download(...args); },
    });
})();
