namespace Quasar.Services.Updates;

public sealed record BrowserPushSubscription(string Endpoint, string P256dh, string Auth);
