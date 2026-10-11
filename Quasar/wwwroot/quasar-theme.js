// Native system-theme observation. Each layout owns and disposes its subscription.
export function observeSystemTheme(callback) {
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    let disposed = false;
    const notify = event => {
        if (!disposed) {
            // A dropped Blazor circuit is expected during shutdown and reconnect.
            callback.invokeMethodAsync('OnSystemDarkModeChangedAsync', event.matches).catch(() => {});
        }
    };
    media.addEventListener('change', notify);
    // Cover an OS preference change between initialization and subscription.
    notify(media);
    return {
        dispose() {
            disposed = true;
            media.removeEventListener('change', notify);
        }
    };
}
