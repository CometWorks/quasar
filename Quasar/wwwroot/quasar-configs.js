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
    scrollToRatio(id, ratio) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }
        const maxScrollTop = Math.max(0, element.scrollHeight - element.clientHeight);
        const clampedRatio = Math.max(0, Math.min(1, typeof ratio === "number" ? ratio : 0));
        element.scrollTop = Math.round(maxScrollTop * clampedRatio);
    },
    isScrolledNearBottom(id, threshold) {
        const element = document.getElementById(id);
        if (!element) {
            return true;
        }
        const slack = typeof threshold === "number" ? threshold : 32;
        return element.scrollHeight - element.scrollTop - element.clientHeight <= slack;
    },
    getScrollEdgeState(id, threshold) {
        const element = document.getElementById(id);
        if (!element) {
            return { nearTop: false, nearBottom: false };
        }
        const slack = typeof threshold === "number" ? threshold : 32;
        return {
            nearTop: element.scrollTop <= slack,
            nearBottom: element.scrollHeight - element.scrollTop - element.clientHeight <= slack
        };
    },
    attachRolloverLog(id, dotNetRef, options) {
        window.quasarLogRollovers = window.quasarLogRollovers || {};

        const existing = window.quasarLogRollovers[id];
        if (existing) {
            existing.dotNetRef = dotNetRef;
            existing.threshold = options?.threshold ?? options?.Threshold ?? existing.threshold;
            existing.canLoadOlder = !!(options?.canLoadOlder ?? options?.CanLoadOlder);
            existing.canLoadNewer = !!(options?.canLoadNewer ?? options?.CanLoadNewer);
            return;
        }

        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        const state = {
            id,
            dotNetRef,
            threshold: options?.threshold ?? options?.Threshold ?? 96,
            canLoadOlder: !!(options?.canLoadOlder ?? options?.CanLoadOlder),
            canLoadNewer: !!(options?.canLoadNewer ?? options?.CanLoadNewer),
            busy: false
        };

        const readBool = (result, camelName, pascalName) => !!(result?.[camelName] ?? result?.[pascalName]);
        const readNumber = (result, camelName, pascalName, fallback) => {
            const value = result?.[camelName] ?? result?.[pascalName];
            return typeof value === "number" ? value : fallback;
        };

        const updateCapabilities = (result) => {
            state.canLoadOlder = readBool(result, "canLoadOlder", "CanLoadOlder");
            state.canLoadNewer = readBool(result, "canLoadNewer", "CanLoadNewer");
        };

        const scrollAfterRender = (ratio) => {
            window.setTimeout(() => {
                window.requestAnimationFrame(() => {
                    window.requestAnimationFrame(() => {
                        window.quasarConfigs.scrollToRatio(id, ratio);
                        state.busy = false;
                    });
                });
            }, 0);
        };

        const requestWindow = (method, direction) => {
            if (state.busy) {
                return;
            }

            state.busy = true;
            state.dotNetRef.invokeMethodAsync(method, direction)
                .then((result) => {
                    updateCapabilities(result);
                    if (readBool(result, "shifted", "Shifted")) {
                        scrollAfterRender(readNumber(result, "scrollRatio", "ScrollRatio", 0.5));
                    } else {
                        state.busy = false;
                    }
                })
                .catch(() => {
                    state.busy = false;
                });
        };

        state.handleScroll = () => {
            if (state.busy) {
                return;
            }

            const current = document.getElementById(id);
            if (!current) {
                return;
            }

            const nearTop = current.scrollTop <= state.threshold;
            const nearBottom = current.scrollHeight - current.scrollTop - current.clientHeight <= state.threshold;

            if (nearTop && state.canLoadOlder) {
                requestWindow("RequestServerLogWindowShiftAsync", -1);
            } else if (nearBottom && state.canLoadNewer) {
                requestWindow("RequestServerLogWindowShiftAsync", 1);
            }
        };

        state.handleKeyDown = (event) => {
            if (!event.ctrlKey || event.altKey || event.metaKey) {
                return;
            }

            if (event.key === "PageUp" && state.canLoadOlder) {
                event.preventDefault();
                requestWindow("RequestServerLogWindowShiftAsync", -1);
            } else if (event.key === "PageDown" && state.canLoadNewer) {
                event.preventDefault();
                requestWindow("RequestServerLogWindowShiftAsync", 1);
            } else if (event.key === "Home") {
                event.preventDefault();
                requestWindow("RequestServerLogWindowJumpAsync", -1);
            } else if (event.key === "End") {
                event.preventDefault();
                requestWindow("RequestServerLogWindowJumpAsync", 1);
            }
        };

        state.handleClick = () => element.focus({ preventScroll: true });

        element.addEventListener("scroll", state.handleScroll, { passive: true });
        element.addEventListener("click", state.handleClick);
        document.addEventListener("keydown", state.handleKeyDown, true);
        window.quasarLogRollovers[id] = state;
    },
    detachRolloverLog(id) {
        const rollovers = window.quasarLogRollovers;
        const state = rollovers && rollovers[id];
        if (!state) {
            return;
        }

        const element = document.getElementById(id);
        if (element) {
            element.removeEventListener("scroll", state.handleScroll);
            element.removeEventListener("click", state.handleClick);
        }

        document.removeEventListener("keydown", state.handleKeyDown, true);
        delete rollovers[id];
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
