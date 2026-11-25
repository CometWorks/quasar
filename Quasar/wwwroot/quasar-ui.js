// Native dialog helpers for the Quasar dashboard.
//
// This is a classic browser script with no framework and no build step. Blazor
// owns the render tree and the component state. This file only:
//   1. opens and closes the native <dialog> element,
//   2. applies modal or modeless behavior,
//   3. adds optional pointer/keyboard dragging for modeless dialogs,
//   4. adds optional pointer/keyboard resizing from the bottom-right corner,
//   5. reports the native close event back to the Blazor host.
//
// Public surface:
//   quasarUi.dialogs.show(element, options, dotNetRef)
//   quasarUi.dialogs.close(element)
//   quasarUi.dialogs.dispose(element)
//
// The host component calls show once after its first render. A dialog is closed
// either by the host calling close or by the user (Escape, backdrop click). In
// both cases the browser's native close event calls
// dotNetRef.OnNativeClosedAsync(reason), which lets the service complete the
// caller's result and remove the entry from the render tree.
(function () {
    "use strict";

    // Per-element state. A WeakMap lets the element be garbage collected once
    // Blazor removes it; dispose() also deletes the entry eagerly.
    const states = new WeakMap();

    // Number of open modal dialogs that requested a scroll lock. Reference
    // counting keeps the page locked until the last such dialog closes, which
    // matters when dialogs are stacked.
    let scrollLocks = 0;

    // Smallest size a modeless dialog may shrink to while resizing. The values
    // keep the header and footer usable; the actual limit is reduced on a
    // viewport smaller than this.
    const minDialogWidth = 280;
    const minDialogHeight = 180;

    /**
     * Locks page scrolling for a modal dialog. The CSS class also reserves the
     * scrollbar gutter so the layout does not jump when the scrollbar hides.
     */
    function lockScroll() {
        if (scrollLocks++ === 0)
            document.documentElement.classList.add("quasar-dialog-open");
    }

    /**
     * Releases one scroll lock. Safe to call only once per matching lock;
     * cleanup() tracks whether the dialog acquired one.
     */
    function unlockScroll() {
        if (scrollLocks > 0 && --scrollLocks === 0)
            document.documentElement.classList.remove("quasar-dialog-open");
    }

    /**
     * Returns true when viewport coordinates (x, y) are outside the dialog's
     * border box.
     */
    function isOutside(dialog, x, y) {
        const rect = dialog.getBoundingClientRect();
        return x < rect.left || x > rect.right || y < rect.top || y > rect.bottom;
    }

    /**
     * Clamps a proposed top-left position so at least an 8px strip of the
     * dialog stays reachable inside the viewport. The viewport is re-read on
     * every move so resizing the window cannot strand the dialog off-screen.
     */
    function clampToViewport(dialog, left, top) {
        const maxLeft = Math.max(8, window.innerWidth - dialog.offsetWidth - 8);
        const maxTop = Math.max(8, window.innerHeight - dialog.offsetHeight - 8);
        return {
            left: Math.min(Math.max(8, left), maxLeft),
            top: Math.min(Math.max(8, top), maxTop),
        };
    }

    /**
     * Gives a modeless dialog its initial centered position. Position is fixed
     * rather than absolute so page scrolling does not move the dialog. The
     * inline inset values are read by setupDrag() on the first move.
     */
    function positionModeless(dialog) {
        dialog.style.position = "fixed";
        dialog.style.margin = "0";
        dialog.style.right = "auto";
        dialog.style.bottom = "auto";
        const position = clampToViewport(
            dialog,
            (window.innerWidth - dialog.offsetWidth) / 2,
            (window.innerHeight - dialog.offsetHeight) / 2);
        dialog.style.left = position.left + "px";
        dialog.style.top = position.top + "px";
    }

    /**
     * Adds pointer dragging and arrow-key movement to a modeless dialog.
     *
     * Dragging uses pointer events with capture, so a fast pointer that leaves
     * the handle keeps moving the dialog until the button is released. Arrow
     * keys move by 10px, or 1px with Shift, and prevent page scrolling while
     * the handle has focus.
     *
     * Returns a cleanup function that removes every listener. The host calls it
     * when the dialog closes or is disposed.
     */
    function setupDrag(dialog, handle) {
        let pointerId = null;
        let startX = 0;
        let startY = 0;
        let startLeft = 0;
        let startTop = 0;

        const moveTo = (left, top) => {
            const position = clampToViewport(dialog, left, top);
            dialog.style.left = position.left + "px";
            dialog.style.top = position.top + "px";
        };

        const pointerdown = event => {
            // Only the primary button drags. preventDefault avoids text
            // selection and the browser's own drag behavior.
            if (event.button !== 0)
                return;
            event.preventDefault();
            pointerId = event.pointerId;
            const rect = dialog.getBoundingClientRect();
            startX = event.clientX;
            startY = event.clientY;
            startLeft = rect.left;
            startTop = rect.top;
            handle.setPointerCapture(event.pointerId);
        };

        const pointermove = event => {
            if (event.pointerId !== pointerId)
                return;
            moveTo(startLeft + event.clientX - startX, startTop + event.clientY - startY);
        };

        const pointerup = event => {
            if (event.pointerId !== pointerId)
                return;
            pointerId = null;
            // Releasing a capture that the browser already dropped throws; it
            // is harmless because the drag is over either way.
            try {
                handle.releasePointerCapture(event.pointerId);
            } catch {
                // The browser may already have released the capture.
            }
        };

        const keydown = event => {
            const step = event.shiftKey ? 1 : 10;
            const rect = dialog.getBoundingClientRect();
            let left = rect.left;
            let top = rect.top;
            switch (event.key) {
                case "ArrowLeft": left -= step; break;
                case "ArrowRight": left += step; break;
                case "ArrowUp": top -= step; break;
                case "ArrowDown": top += step; break;
                default: return;
            }
            event.preventDefault();
            moveTo(left, top);
        };

        handle.addEventListener("pointerdown", pointerdown);
        handle.addEventListener("pointermove", pointermove);
        handle.addEventListener("pointerup", pointerup);
        handle.addEventListener("pointercancel", pointerup);
        handle.addEventListener("keydown", keydown);

        return () => {
            handle.removeEventListener("pointerdown", pointerdown);
            handle.removeEventListener("pointermove", pointermove);
            handle.removeEventListener("pointerup", pointerup);
            handle.removeEventListener("pointercancel", pointerup);
            handle.removeEventListener("keydown", keydown);
        };
    }

    /**
     * Clamps a proposed outer size so the dialog keeps its top-left corner,
     * stays inside the viewport with an 8px gutter, and cannot collapse below
     * the usable minimum. The viewport is re-read on every move so window
     * resizing cannot strand the bottom-right corner off-screen.
     */
    function clampSize(dialog, width, height) {
        const rect = dialog.getBoundingClientRect();
        const availableWidth = Math.max(1, window.innerWidth - rect.left - 8);
        const availableHeight = Math.max(1, window.innerHeight - rect.top - 8);
        const minimumWidth = Math.min(minDialogWidth, availableWidth);
        const minimumHeight = Math.min(minDialogHeight, availableHeight);
        return {
            width: Math.min(Math.max(minimumWidth, width), availableWidth),
            height: Math.min(Math.max(minimumHeight, height), availableHeight),
        };
    }

    /**
     * Adds pointer resizing and arrow-key resizing to the bottom-right corner
     * of a modeless dialog.
     *
     * The top-left corner stays fixed, which matches the standard window
     * resize grip. Width and height are written as inline styles, so the
     * dialog's content region scrolls inside the new size instead of
     * stretching the page.
     *
     * Returns a cleanup function that removes every listener.
     */
    function setupResize(dialog, handle) {
        let pointerId = null;
        let startX = 0;
        let startY = 0;
        let startWidth = 0;
        let startHeight = 0;

        const resizeTo = (width, height) => {
            const size = clampSize(dialog, width, height);
            dialog.style.width = size.width + "px";
            dialog.style.height = size.height + "px";
        };

        const pointerdown = event => {
            // Only the primary button resizes. preventDefault avoids text
            // selection and the browser's own image/file drag behavior.
            if (event.button !== 0)
                return;
            event.preventDefault();
            pointerId = event.pointerId;
            const rect = dialog.getBoundingClientRect();
            startX = event.clientX;
            startY = event.clientY;
            startWidth = rect.width;
            startHeight = rect.height;
            handle.setPointerCapture(event.pointerId);
        };

        const pointermove = event => {
            if (event.pointerId !== pointerId)
                return;
            resizeTo(startWidth + event.clientX - startX, startHeight + event.clientY - startY);
        };

        const pointerup = event => {
            if (event.pointerId !== pointerId)
                return;
            pointerId = null;
            try {
                handle.releasePointerCapture(event.pointerId);
            } catch {
                // The browser may already have released the capture.
            }
        };

        const keydown = event => {
            const step = event.shiftKey ? 1 : 10;
            const rect = dialog.getBoundingClientRect();
            let width = rect.width;
            let height = rect.height;
            switch (event.key) {
                case "ArrowLeft": width -= step; break;
                case "ArrowRight": width += step; break;
                case "ArrowUp": height -= step; break;
                case "ArrowDown": height += step; break;
                default: return;
            }
            event.preventDefault();
            resizeTo(width, height);
        };

        handle.addEventListener("pointerdown", pointerdown);
        handle.addEventListener("pointermove", pointermove);
        handle.addEventListener("pointerup", pointerup);
        handle.addEventListener("pointercancel", pointerup);
        handle.addEventListener("keydown", keydown);

        return () => {
            handle.removeEventListener("pointerdown", pointerdown);
            handle.removeEventListener("pointermove", pointermove);
            handle.removeEventListener("pointerup", pointerup);
            handle.removeEventListener("pointercancel", pointerup);
            handle.removeEventListener("keydown", keydown);
        };
    }

    /**
     * Removes native listeners, drag/resize listeners, and any scroll lock.
     * Safe to call more than once; the drag cleanup is cleared after the first
     * call.
     */
    function cleanup(state) {
        const dialog = state.element;
        dialog.removeEventListener("cancel", state.cancel);
        dialog.removeEventListener("close", state.close);
        dialog.removeEventListener("keydown", state.keydown);
        dialog.removeEventListener("pointerdown", state.pointerdown);
        dialog.removeEventListener("pointerup", state.pointerup);
        state.dragCleanup?.();
        state.dragCleanup = null;
        state.resizeCleanup?.();
        state.resizeCleanup = null;
        if (state.locked) {
            state.locked = false;
            unlockScroll();
        }
    }

    /**
     * Reports closure to Blazor exactly once. It runs after the native close
     * event, so the DOM is already closed when the component is removed.
     *
     * The circuit can disappear while a dialog is closing (browser refresh,
     * tab close). The interop call then rejects; the rejection is swallowed
     * because there is no Blazor state left to update.
     */
    function notifyClosed(state, reason) {
        if (state.notified)
            return;
        state.notified = true;
        cleanup(state);
        states.delete(state.element);
        try {
            void state.dotNetRef.invokeMethodAsync("OnNativeClosedAsync", reason).catch(() => {
                // The Blazor circuit can disappear while the dialog is closing.
            });
        } catch {
            // The interop call can also fail synchronously before it returns a promise.
        }
    }

    /**
     * Wires one native dialog element and opens it.
     *
     * options:
     *   modal            true (default) uses showModal(); false uses show().
     *   draggable        modeless only; adds the drag handle behavior.
     *   resizable        modeless only; adds the bottom-right resize grip.
     *   closeOnEscapeKey false prevents Escape from closing the dialog.
     *   backdropClick    modal only; false prevents backdrop dismissal.
     *   lockScroll       modal only; false leaves the page scrollable.
     *   focusDialog      true focuses the dialog container instead of the
     *                    first focusable descendant.
     */
    function attach(dialog, dotNetRef, options) {
        const modal = options.modal !== false;
        const state = {
            element: dialog,
            dotNetRef,
            options,
            locked: false,
            notified: false,
            closing: false,
            pointerDownOutside: false,
            dragCleanup: null,
            resizeCleanup: null,
        };

        // Native modal dialogs fire "cancel" for Escape before closing. A
        // non-modal dialog does not, so modeless Escape handling is a keydown
        // listener below.
        state.cancel = event => {
            if (options.closeOnEscapeKey === false)
                event.preventDefault();
        };

        state.close = () => {
            notifyClosed(state, state.closing ? "close" : "dismissed");
        };

        // Modal dialogs close through the native "cancel" event. This keydown
        // listener exists for the locked case: when Escape is disabled, the
        // dialog itself must swallow it so page-level shortcuts cannot act on
        // the key while the modal owns the screen.
        state.keydown = event => {
            if (event.key !== "Escape")
                return;
            if (options.closeOnEscapeKey === false) {
                event.preventDefault();
                event.stopPropagation();
                return;
            }
            if (modal)
                return;
            state.closing = true;
            dialog.close();
        };

        // A click only counts as a backdrop click when both the press and the
        // release happen outside the dialog. This prevents dragging a text
        // selection out of the dialog from dismissing it.
        state.pointerdown = event => {
            state.pointerDownOutside = isOutside(dialog, event.clientX, event.clientY);
        };

        state.pointerup = event => {
            if (!state.pointerDownOutside || options.backdropClick === false)
                return;
            if (isOutside(dialog, event.clientX, event.clientY)) {
                state.closing = true;
                dialog.close();
            }
        };

        dialog.addEventListener("cancel", state.cancel);
        dialog.addEventListener("close", state.close);
        dialog.addEventListener("keydown", state.keydown);
        if (modal) {
            dialog.addEventListener("pointerdown", state.pointerdown);
            dialog.addEventListener("pointerup", state.pointerup);
        }

        states.set(dialog, state);

        if (modal) {
            if (options.lockScroll !== false) {
                state.locked = true;
                lockScroll();
            }
            dialog.showModal();
        } else {
            dialog.show();
            positionModeless(dialog);
            if (options.draggable !== false) {
                const handle = dialog.querySelector(".quasar-dialog-drag-handle");
                if (handle)
                    state.dragCleanup = setupDrag(dialog, handle);
            }
            if (options.resizable !== false) {
                const handle = dialog.querySelector(".quasar-dialog-resize-handle");
                if (handle)
                    state.resizeCleanup = setupResize(dialog, handle);
            }
        }

        // Explicit dialog focus is useful when the body has no focusable
        // control. tabIndex=-1 makes the element programmatically focusable
        // without adding it to the tab order.
        if (options.focusDialog === true) {
            dialog.tabIndex = -1;
            dialog.focus();
        }
    }

    window.quasarUi = window.quasarUi || {};
    window.quasarUi.dialogs = {
        /**
         * Opens an already-rendered <dialog>. No-op when the element is gone or
         * already open, which keeps repeated Blazor renders harmless.
         *
         * If opening fails, listeners and scroll locks are rolled back before
         * the error is rethrown, so the host can finish the dialog entry.
         */
        show(element, options, dotNetRef) {
            if (!element || !element.isConnected || element.open)
                return;

            const settings = options || {};
            try {
                attach(element, dotNetRef, settings);
            } catch (error) {
                const state = states.get(element);
                if (state) {
                    cleanup(state);
                    states.delete(element);
                }
                throw error;
            }
        },

        /**
         * Requests a native close. The close event then notifies Blazor. When
         * the element is already closed, Blazor is notified directly so the
         * caller's result cannot hang.
         */
        close(element) {
            const state = states.get(element);
            if (!state || state.closing)
                return;
            state.closing = true;
            if (element.open)
                element.close();
            else
                notifyClosed(state, "close");
        },

        /**
         * Releases listeners without notifying Blazor. The host uses this while
         * its component is being disposed, then finishes the entry itself.
         */
        dispose(element) {
            const state = states.get(element);
            if (!state)
                return;
            state.notified = true;
            cleanup(state);
            states.delete(element);
            if (element.open)
                element.close();
        }
    };
})();
