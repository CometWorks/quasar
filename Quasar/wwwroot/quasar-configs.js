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
    downloadFile(url, fileName) {
        const link = document.createElement("a");
        link.href = url;
        if (fileName) {
            link.download = fileName;
        }
        link.rel = "noopener";
        link.style.display = "none";
        document.body.appendChild(link);
        link.click();
        link.remove();
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
            const useCtrlShortcut = event.ctrlKey && !event.altKey && !event.metaKey;
            const useAltShortcut = event.altKey && !event.ctrlKey && !event.metaKey;
            if (!useCtrlShortcut && !useAltShortcut) {
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
    showRestartFeedback(options) {
        const opts = options || {};
        const read = (camelName, pascalName, fallback) => opts?.[camelName] ?? opts?.[pascalName] ?? fallback;
        const readNumber = (camelName, pascalName, fallback) => {
            const value = read(camelName, pascalName, fallback);
            return typeof value === "number" && Number.isFinite(value) ? value : fallback;
        };
        const titleText = read("title", "Title", "Restarting Quasar");
        const messageText = read("message", "Message", "Waiting for the web worker to come back online.");
        const statusText = read("initialStatus", "InitialStatus", "Preparing restart request.");
        const startedAt = readNumber("startedAt", "StartedAt", Date.now());
        const steps = read("steps", "Steps", [
            "Request restart",
            "Wait for worker stop",
            "Poll health check",
            "Reload page"
        ]);

        let state = window.quasarRestartFeedback;
        if (!state || !state.root || !document.body.contains(state.root)) {
            const root = document.createElement("div");
            root.id = "quasar-restart-feedback";
            root.className = "quasar-restart-feedback";
            root.setAttribute("role", "status");
            root.setAttribute("aria-live", "polite");

            const panel = document.createElement("div");
            panel.className = "quasar-restart-feedback-panel";

            const spinner = document.createElement("div");
            spinner.className = "quasar-restart-feedback-spinner";
            spinner.setAttribute("aria-hidden", "true");

            const content = document.createElement("div");
            content.className = "quasar-restart-feedback-content";

            const title = document.createElement("div");
            title.className = "quasar-restart-feedback-title";

            const message = document.createElement("div");
            message.className = "quasar-restart-feedback-message";

            const status = document.createElement("div");
            status.className = "quasar-restart-feedback-status";

            const stepList = document.createElement("ol");
            stepList.className = "quasar-restart-feedback-steps";

            content.appendChild(title);
            content.appendChild(message);
            content.appendChild(status);
            content.appendChild(stepList);
            panel.appendChild(spinner);
            panel.appendChild(content);
            root.appendChild(panel);
            document.body.appendChild(root);

            state = {
                root,
                title,
                message,
                status,
                stepList,
                phaseOrder: ["request", "stop", "health", "reload"],
                startedAt: Date.now()
            };
            window.quasarRestartFeedback = state;
        }

        state.startedAt = startedAt;
        state.title.textContent = titleText;
        state.message.textContent = messageText;
        state.stepList.replaceChildren();
        state.steps = Array.isArray(steps)
            ? steps.map((label) => {
                const item = document.createElement("li");
                item.className = "quasar-restart-feedback-step";
                item.textContent = label;
                state.stepList.appendChild(item);
                return item;
            })
            : [];

        window.quasarConfigs.updateRestartFeedback(statusText, "request");
        return true;
    },
    updateRestartFeedback(statusText, phase) {
        const state = window.quasarRestartFeedback;
        if (!state || !state.root) {
            return false;
        }

        const elapsedSeconds = Math.max(0, Math.floor((Date.now() - state.startedAt) / 1000));
        state.status.textContent = elapsedSeconds > 0
            ? `${statusText} (${elapsedSeconds}s)`
            : statusText;

        const phaseName = phase || "request";
        const activeIndex = phaseName === "timeout"
            ? state.phaseOrder.length - 1
            : Math.max(0, state.phaseOrder.indexOf(phaseName));

        (state.steps || []).forEach((step, index) => {
            step.classList.toggle("quasar-restart-feedback-step-done", index < activeIndex);
            step.classList.toggle("quasar-restart-feedback-step-active", index === activeIndex);
        });

        return true;
    },
    clearRestartFeedback() {
        const state = window.quasarRestartFeedback;
        if (state && state.root) {
            state.root.remove();
        }
        window.quasarConfigs.clearRestartReloadState();
        window.quasarRestartFeedback = null;
        window.quasarRestartReloadSession = null;
    },
    getRestartReloadStorageKey() {
        return "quasar.restartReload";
    },
    readRestartReloadState() {
        try {
            const raw = window.sessionStorage?.getItem(window.quasarConfigs.getRestartReloadStorageKey());
            if (!raw) {
                return null;
            }

            return JSON.parse(raw);
        } catch {
            window.quasarConfigs.clearRestartReloadState();
            return null;
        }
    },
    writeRestartReloadState(state) {
        try {
            window.sessionStorage?.setItem(
                window.quasarConfigs.getRestartReloadStorageKey(),
                JSON.stringify(state));
        } catch {
            // sessionStorage can be unavailable in private or locked-down browsers.
        }
    },
    clearRestartReloadState() {
        try {
            window.sessionStorage?.removeItem(window.quasarConfigs.getRestartReloadStorageKey());
        } catch {
            // Ignore storage failures; in-memory restart polling still works.
        }
    },
    resumeRestartReload() {
        const saved = window.quasarConfigs.readRestartReloadState();
        if (!saved || !saved.url) {
            return false;
        }

        const options = saved.options || {};
        const read = (camelName, pascalName, fallback) => options?.[camelName] ?? options?.[pascalName] ?? fallback;
        const readNumber = (camelName, pascalName, fallback) => {
            const value = read(camelName, pascalName, fallback);
            return typeof value === "number" && Number.isFinite(value) ? value : fallback;
        };
        const startedAt = typeof saved.startedAt === "number" && Number.isFinite(saved.startedAt)
            ? saved.startedAt
            : readNumber("startedAt", "StartedAt", Date.now());
        const maxWaitMs = readNumber("maxWaitMs", "MaxWaitMs", 120000);
        if (Date.now() - startedAt >= maxWaitMs + 1000) {
            window.quasarConfigs.clearRestartReloadState();
            return false;
        }

        window.quasarConfigs.reloadWhenHealthy(saved.url, {
            ...options,
            initialDelayMs: 0,
            startedAt,
            resumeSessionId: saved.sessionId || read("resumeSessionId", "ResumeSessionId", ""),
            observedUnhealthy: !!(saved.observedUnhealthy ?? read("observedUnhealthy", "ObservedUnhealthy", false))
        });
        return true;
    },
    // Used when the Quasar worker is being restarted: the Blazor circuit drops, so we
    // poll the (anonymous) health endpoint from the browser and navigate to the target
    // page once the new worker answers. Falls back to a plain reload after a timeout.
    reloadWhenHealthy(targetUrl, options) {
        const url = targetUrl || "/";
        const opts = options || {};
        const read = (camelName, pascalName, fallback) => opts?.[camelName] ?? opts?.[pascalName] ?? fallback;
        const readNumber = (camelName, pascalName, fallback) => {
            const value = read(camelName, pascalName, fallback);
            return typeof value === "number" && Number.isFinite(value) ? value : fallback;
        };
        const pollIntervalMs = readNumber("pollIntervalMs", "PollIntervalMs", 1000);
        const maxWaitMs = readNumber("maxWaitMs", "MaxWaitMs", 120000);
        const initialDelayMs = readNumber("initialDelayMs", "InitialDelayMs", 1500);
        const stopWaitFallbackMs = readNumber("stopWaitFallbackMs", "StopWaitFallbackMs", 10000);
        const expectedVersion = (opts.expectedVersion || opts.ExpectedVersion || "").toString().trim().toLowerCase();
        const requireUnhealthy = !!(opts.requireUnhealthy ?? opts.RequireUnhealthy);
        const showFeedback = !!read(
            "showFeedback",
            "ShowFeedback",
            read("title", "Title", "") || read("message", "Message", ""));
        const waitingForStopMessage = read(
            "waitingForStopMessage",
            "WaitingForStopMessage",
            "Waiting for the current Quasar worker to stop.");
        const pollingMessage = read(
            "pollingMessage",
            "PollingMessage",
            "Waiting for Quasar to pass /api/health.");
        const successMessage = read(
            "successMessage",
            "SuccessMessage",
            "Quasar is healthy. Reloading page.");
        const timeoutMessage = read(
            "timeoutMessage",
            "TimeoutMessage",
            "Still waiting for Quasar. Reloading page now.");
        const startedAt = readNumber("startedAt", "StartedAt", Date.now());
        const sessionId = (read("resumeSessionId", "ResumeSessionId", "") || `${startedAt}:${Math.random()}`).toString();
        let observedUnhealthy = !!read("observedUnhealthy", "ObservedUnhealthy", !requireUnhealthy);
        window.quasarRestartReloadSession = sessionId;

        const isCurrentSession = () => window.quasarRestartReloadSession === sessionId;
        const persistState = () => {
            window.quasarConfigs.writeRestartReloadState({
                url,
                sessionId,
                startedAt,
                observedUnhealthy,
                options: {
                    ...opts,
                    initialDelayMs: 0,
                    startedAt,
                    resumeSessionId: sessionId,
                    observedUnhealthy,
                    stopWaitFallbackMs
                }
            });
        };
        const markObservedUnhealthy = () => {
            if (!observedUnhealthy) {
                observedUnhealthy = true;
                persistState();
            }
        };
        const canAcceptHealthyWorker = () => {
            if (observedUnhealthy || !requireUnhealthy) {
                return true;
            }

            if (Date.now() - startedAt < stopWaitFallbackMs) {
                return false;
            }

            // Browser may miss the brief outage when the worker restarts quickly.
            markObservedUnhealthy();
            return true;
        };

        persistState();

        if (showFeedback) {
            window.quasarConfigs.showRestartFeedback(opts);
        }

        const updateFeedback = (phase, message) => {
            if (showFeedback) {
                window.quasarConfigs.updateRestartFeedback(message, phase);
            }
        };

        const scheduleNext = () => {
            if (!isCurrentSession()) {
                return;
            }

            if (Date.now() - startedAt >= maxWaitMs) {
                updateFeedback("timeout", timeoutMessage);
                window.setTimeout(() => {
                    if (isCurrentSession()) {
                        window.quasarConfigs.clearRestartReloadState();
                        window.location.href = url;
                    }
                }, 500);
                return;
            }
            window.setTimeout(check, pollIntervalMs);
        };

        const isExpectedVersion = (payload) => {
            if (!expectedVersion) {
                return true;
            }

            const actual = (payload?.version ?? payload?.Version ?? "").toString().trim().toLowerCase();
            return actual === expectedVersion;
        };

        const check = () => {
            if (!isCurrentSession()) {
                return;
            }

            const currentPhase = canAcceptHealthyWorker() ? "health" : "stop";
            updateFeedback(currentPhase, currentPhase === "health" ? pollingMessage : waitingForStopMessage);
            fetch("/api/health", { cache: "no-store" })
                .then(async (response) => {
                    if (!isCurrentSession()) {
                        return;
                    }

                    if (!response.ok) {
                        markObservedUnhealthy();
                        updateFeedback("health", pollingMessage);
                        scheduleNext();
                        return;
                    }

                    let payload = null;
                    if (expectedVersion) {
                        try {
                            payload = await response.json();
                        } catch {
                            payload = null;
                        }
                    }

                    if (observedUnhealthy && isExpectedVersion(payload)) {
                        updateFeedback("reload", successMessage);
                        window.setTimeout(() => {
                            if (isCurrentSession()) {
                                window.quasarConfigs.clearRestartReloadState();
                                window.location.href = url;
                            }
                        }, 250);
                    } else {
                        updateFeedback(observedUnhealthy ? "health" : "stop", observedUnhealthy ? pollingMessage : waitingForStopMessage);
                        scheduleNext();
                    }
                })
                .catch(() => {
                    if (!isCurrentSession()) {
                        return;
                    }

                    markObservedUnhealthy();
                    updateFeedback("health", pollingMessage);
                    scheduleNext();
                });
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

window.setTimeout(() => window.quasarConfigs.resumeRestartReload?.(), 0);
