window.quasarConfigs = window.quasarConfigs || {
    getSystemDarkMode() {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    },
    getViewportWidth() {
        return Math.max(320, Math.floor(window.innerWidth || document.documentElement.clientWidth || document.body.clientWidth || 1280));
    },
    focusElement(id) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        element.scrollIntoView({
            behavior: "smooth",
            block: "center",
            inline: "nearest"
        });

        element.classList.add("config-option-focus");

        if (typeof element.focus === "function") {
            element.focus({ preventScroll: true });
        }

        window.setTimeout(() => {
            element.classList.remove("config-option-focus");
        }, 1800);
    },
    scrollToBottom(id) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }
        element.scrollTop = element.scrollHeight;
    },
    isScrolledNearBottom(id, threshold) {
        const element = document.getElementById(id);
        if (!element) {
            return true;
        }
        const slack = typeof threshold === "number" ? threshold : 32;
        return element.scrollHeight - element.scrollTop - element.clientHeight <= slack;
    },
    // Used when the Quasar worker is being restarted: the Blazor circuit drops, so we
    // poll the (anonymous) health endpoint from the browser and navigate to the target
    // page once the new worker answers. Falls back to a plain reload after a timeout.
    reloadWhenHealthy(targetUrl, options) {
        const url = targetUrl || "/";
        const opts = options || {};
        const pollIntervalMs = opts.pollIntervalMs || 1000;
        const maxWaitMs = opts.maxWaitMs || 120000;
        const initialDelayMs = opts.initialDelayMs || 1500;
        const startedAt = Date.now();

        const scheduleNext = () => {
            if (Date.now() - startedAt >= maxWaitMs) {
                window.location.href = url;
                return;
            }
            window.setTimeout(check, pollIntervalMs);
        };

        const check = () => {
            fetch("/api/health", { cache: "no-store" })
                .then((response) => {
                    if (response.ok) {
                        window.location.href = url;
                    } else {
                        scheduleNext();
                    }
                })
                .catch(scheduleNext);
        };

        window.setTimeout(check, initialDelayMs);
    },
    async copyText(text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch (e) { /* fall through to legacy path */ }
        // Fallback for non-secure contexts (HTTP LAN access)
        try {
            const ta = document.createElement("textarea");
            ta.value = text;
            ta.style.position = "fixed";
            ta.style.opacity = "0";
            document.body.appendChild(ta);
            ta.focus();
            ta.select();
            const ok = document.execCommand("copy");
            document.body.removeChild(ta);
            return ok;
        } catch (e) {
            return false;
        }
    }
};
