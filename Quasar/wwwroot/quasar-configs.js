window.quasarConfigs = window.quasarConfigs || {
    getSystemDarkMode() {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
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
    }
};
