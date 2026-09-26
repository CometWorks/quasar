window.quasarPush = (() => {
    const workerUrl = '/push-worker.js';

    function supported() {
        return window.isSecureContext && 'serviceWorker' in navigator &&
            'PushManager' in window && 'Notification' in window;
    }

    function details(subscription) {
        if (!subscription) return null;
        const json = subscription.toJSON();
        return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
    }

    function keyBytes(value) {
        const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
        const decoded = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='));
        return Uint8Array.from(decoded, character => character.charCodeAt(0));
    }

    async function registration() {
        const worker = await navigator.serviceWorker.getRegistration('/');
        return worker?.active?.scriptURL === new URL(workerUrl, location.origin).href ? worker : null;
    }

    return {
        status: async () => {
            if (!supported()) return { supported: false, subscription: null };
            const worker = await registration();
            return { supported: true, subscription: Notification.permission === 'granted'
                ? details(await worker?.pushManager.getSubscription()) : null };
        },
        subscribe: async publicKey => {
            if (!supported()) throw new Error('Browser push requires a supported browser and HTTPS or localhost.');
            if (Notification.permission !== 'granted' && await Notification.requestPermission() !== 'granted')
                throw new Error('Browser notification permission was denied.');
            await navigator.serviceWorker.register(workerUrl, { scope: '/' });
            const worker = await navigator.serviceWorker.ready;
            const serverKey = keyBytes(publicKey);
            let current = await worker.pushManager.getSubscription();
            if (current) {
                const currentKey = current.options.applicationServerKey;
                const bytes = currentKey && new Uint8Array(currentKey);
                if (!bytes || bytes.length !== serverKey.length ||
                    !bytes.every((value, index) => value === serverKey[index])) {
                    await current.unsubscribe();
                    current = null;
                }
            }
            return details(current ?? await worker.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: serverKey
            }));
        },
        unsubscribe: async () => {
            if (!supported()) return null;
            const worker = await registration();
            const current = await worker?.pushManager.getSubscription();
            if (!current) return null;
            const endpoint = current.endpoint;
            await current.unsubscribe();
            return endpoint;
        }
    };
})();
