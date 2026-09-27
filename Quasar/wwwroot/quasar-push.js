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

    function isOurWorker(worker) {
        return worker?.scriptURL === new URL(workerUrl, location.origin).href;
    }

    function isActive(registration) {
        return isOurWorker(registration?.active) && registration.active.state === 'activated';
    }

    async function registration() {
        const worker = await navigator.serviceWorker.getRegistration('/');
        return isActive(worker) ? worker : null;
    }

    async function activeRegistration() {
        let registration;
        try {
            registration = await navigator.serviceWorker.register(workerUrl, { scope: '/' });
        } catch (error) {
            throw new Error(`Quasar push service worker registration failed: ${error.message ?? error}`);
        }
        if (isActive(registration)) return registration;

        // The global serviceWorker.ready promise can resolve to an older/different worker.
        // Follow the registration we just created, including its installing/waiting worker.
        await new Promise((resolve, reject) => {
            const watched = new Set();
            const timeout = setTimeout(() => finish(new Error('Quasar push service worker activation timed out.')), 15000);
            function finish(error) {
                clearTimeout(timeout);
                registration.removeEventListener('updatefound', check);
                for (const worker of watched) worker.removeEventListener('statechange', check);
                if (error) reject(error); else resolve();
            }
            function check() {
                for (const worker of [registration.installing, registration.waiting, registration.active]) {
                    if (!isOurWorker(worker) || watched.has(worker)) continue;
                    watched.add(worker);
                    worker.addEventListener('statechange', check);
                }
                if (isActive(registration)) return finish();
                if (watched.size && [...watched].every(worker => worker.state === 'redundant'))
                    finish(new Error('Quasar push service worker installation failed.'));
            }
            registration.addEventListener('updatefound', check);
            check();
        });
        return registration;
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
            const worker = await activeRegistration();
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
