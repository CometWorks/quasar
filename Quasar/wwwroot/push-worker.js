// This worker only handles push; activate updates without waiting for open tabs to close.
self.addEventListener('install', event => {
    event.waitUntil(self.skipWaiting());
});

self.addEventListener('push', event => {
    let notice;
    try { notice = event.data?.json(); } catch { return; }
    if (!notice || typeof notice.title !== 'string' || typeof notice.body !== 'string') return;
    const target = new URL((typeof notice.url === 'string' ? notice.url : '').replace(/^\/(?!\/)/, ''), self.registration.scope);
    const url = target.origin === self.location.origin ? target.href : new URL('settings/updates', self.registration.scope).href;
    event.waitUntil(self.registration.showNotification(notice.title, {
        body: notice.body,
        icon: new URL('Quasar.png', self.registration.scope).href,
        tag: String(notice.tag ?? ''),
        data: { url }
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const target = new URL(event.notification.data?.url ?? 'settings/updates', self.registration.scope);
    const url = target.origin === self.location.origin ? target.href : new URL('settings/updates', self.registration.scope).href;
    event.waitUntil((async () => {
        const windows = await clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = windows.find(window => window.url === url);
        if (existing) return existing.focus();
        return clients.openWindow(url);
    })());
});
