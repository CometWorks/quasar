// Static shell controls work while the WASM runtime is downloading.
function bindShell(shell) {
    const menu = shell.querySelector("#quasar-wasm-menu");
    const narrow = matchMedia("(max-width: 900px)");
    const updateMenu = () => menu.setAttribute("aria-expanded", String(narrow.matches
        ? shell.classList.contains("quasar-nav-open") : !shell.classList.contains("quasar-nav-collapsed")));
    menu.addEventListener("click", () => {
        shell.classList.toggle(narrow.matches ? "quasar-nav-open" : "quasar-nav-collapsed");
        updateMenu();
    });
    shell.addEventListener("keydown", event => {
        if (event.key === "Escape" && shell.classList.contains("quasar-nav-open")) {
            shell.classList.remove("quasar-nav-open");
            menu.focus();
            updateMenu();
        }
    });
    narrow.addEventListener("change", updateMenu);
    updateMenu();

    const theme = shell.querySelector("#quasar-wasm-theme");
    const system = matchMedia("(prefers-color-scheme: dark)");
    const logo = shell.querySelector(".quasar-brand-mark");
    const storedMode = quasarConfigs.getLocalStorage("quasar.theme.mode");
    // Earlier panels stored the choice as a JSON string.
    const mode = storedMode?.replace(/^"(light|dark|system)"$/, "$1");
    theme.value = mode === "light" || mode === "dark" ? mode : "system";
    function apply() {
        const dark = theme.value === "dark" || (theme.value !== "light" && system.matches);
        shell.style.cssText = dark ? shell.dataset.darkPalette : shell.dataset.lightPalette;
        shell.dataset.theme = dark ? "dark" : "light";
        logo.src = dark ? logo.dataset.darkLogo : logo.dataset.lightLogo;
        quasarSdk.themeModeChanged();
    }
    theme.addEventListener("change", () => { quasarConfigs.setLocalStorage("quasar.theme.mode", theme.value); apply(); });
    system.addEventListener("change", apply);
    apply();
}

// The .NET loader imports JavaScript modules by URL. Only fetch resource kinds
// for which it accepts a Response carrying the manifest's integrity check.
export function releaseResourceLoader(release, baseUri, manifest) {
    if (!/^[a-f0-9]{64}$/.test(release ?? "")) return undefined;
    const framework = new URL("_framework/", baseUri);
    return (type, name, original, integrity) => {
        const expected = manifest?.[name];
        if (integrity && integrity !== expected) return;
        integrity = expected;
        if (!["assembly", "dotnetwasm", "globalization"].includes(type)
            || !/^[A-Za-z0-9_.-]+\.[a-z0-9]{10}\.(?:wasm|dat)$/.test(name)
            || !/^sha256-[A-Za-z0-9+/]{43}=$/.test(integrity)) return;
        const url = new URL(original, baseUri);
        if (url.href !== new URL(name, framework).href) return;
        const shared = new URL(`/_nebula/releases/${release}/_framework/${name}`, framework);
        return (async () => {
            try {
                const response = await fetch(shared, { credentials: "omit", integrity, redirect: "error" });
                if (response.ok) return response;
            } catch { /* Integrity failure or an unavailable gateway. Try the panel's copy. */ }
            return fetch(url, { credentials: "same-origin", integrity, redirect: "error" });
        })();
    };
}

export async function startDashboard({ hosted, release, frameworkIntegrity }, showFailure) {
    const shell = document.getElementById("quasar-wasm-shell");
    bindShell(shell);
    const loader = shell.querySelector("#quasar-wasm-loading");
    const label = loader.querySelector("#quasar-wasm-loading-label");
    const bar = loader.querySelector("progress");
    bar.max = 100;
    function progress() {
        const value = parseFloat(document.documentElement.style.getPropertyValue("--blazor-load-percentage"));
        if (Number.isFinite(value)) {
            bar.value = value;
            if (value >= 100) label.textContent = "Starting dashboard…";
        }
    }
    const observer = new MutationObserver(progress);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["style"] });
    progress();

    const access = quasarSdk.dashboardAccess();
    // Warm permissions alongside the runtime. The router handles errors and retries.
    access.catch(() => {});

    if (hosted) {
        const indicator = shell.querySelector("#quasar-sdk-route");
        const status = { http: "Using tunnel", connecting: "Tunnel · connecting directly…", direct: "Connected directly" };
        const refresh = async () => {
            const path = await quasarSdk.transportStatus();
            if (status[path] && indicator.textContent !== status[path]) indicator.textContent = status[path];
        };
        indicator.textContent = "Opening panel connection…";
        access.then(() => quasarSdk.prepare()).then(() => {
            refresh();
            const timer = setInterval(() => { if (!document.hidden) refresh().catch(() => {}); }, 1500);
            window.addEventListener("pagehide", event => { if (!event.persisted) clearInterval(timer); });
        }).catch(() => { indicator.textContent = "Connection unavailable"; });
    }
    const failed = () => { observer.disconnect(); showFailure(); };
    window.addEventListener("unhandledrejection", failed, { once: true });
    window.addEventListener("quasar-wasm-ready", () => {
        window.removeEventListener("unhandledrejection", failed);
        loader.hidden = true;
        observer.disconnect();
    }, { once: true });
    try {
        await Blazor.start({ webAssembly: { loadBootResource: hosted ? releaseResourceLoader(release, document.baseURI, frameworkIntegrity) : undefined } });
    } catch (error) {
        window.removeEventListener("unhandledrejection", failed);
        observer.disconnect();
        throw error;
    }
}
