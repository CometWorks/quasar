// Billing notices may link to the one fixed Nebula account URL, including from local Quasar.
// Other external URLs keep the existing fallback.
function noticeUrl(value, tag) {
    try {
        const target = new URL((typeof value === 'string' ? value : '').replace(/^\/(?!\/)/, ''), self.registration.scope);
        if (target.origin === self.location.origin || typeof tag === 'string' && tag.startsWith('nebula-billing:')
            && target.href === 'https://dash.se-servers.net/account') return target.href;
    } catch { }
    return new URL('notifications', self.registration.scope).href;
}

// This worker only handles push; activate updates without waiting for open tabs to close.
self.addEventListener('install', event => {
    event.waitUntil(self.skipWaiting());
});

self.addEventListener('push', event => {
    let notice;
    try { notice = event.data?.json(); } catch { return; }
    if (!notice || typeof notice.title !== 'string' || typeof notice.body !== 'string') return;
    const url = noticeUrl(notice.url, notice.tag);
    event.waitUntil(self.registration.showNotification(notice.title, {
        body: notice.body,
        icon: new URL('Quasar.png', self.registration.scope).href,
        tag: String(notice.tag ?? ''),
        data: { url, tag: String(notice.tag ?? '') }
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = noticeUrl(event.notification.data?.url, event.notification.data?.tag);
    event.waitUntil((async () => {
        const windows = await clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = windows.find(window => window.url === url);
        if (existing) return existing.focus();
        return clients.openWindow(url);
    })());
});
