using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Quasar.Services.Auth;
using WebPush;

namespace Quasar.Services.Updates;

public sealed record BrowserPushSubscription(string Endpoint, string P256dh, string Auth);

/// <summary>Stores browser subscriptions and delivers outstanding notices to each authorized viewer.</summary>
public sealed class PushNotificationService(
    IDataProtectionProvider protection, QuasarUpdateService updates, ClusterReleaseMonitor releases,
    ClusterCatalog clusters, QuasarRoleMapper roles, ILogger<PushNotificationService> logger,
    ClusterContentMonitor content, AgentRegistry agents, DedicatedServerSupervisor supervisor) : BackgroundService
{
    private const string VapidSubject = "https://github.com/CometWorks/quasar";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("Quasar.PushNotifications.v1");
    private readonly string _path = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "PushNotifications.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WebPushClient _client = new();
    private PushState? _state;
    private readonly InstanceNotificationMonitor _instances = new();
    private readonly object _observationSync = new();
    public event Action? Changed;

    public async Task<NotificationPreferences> GetPreferencesAsync(ClaimsPrincipal user, CancellationToken token = default)
    {
        var identity = BrowserIdentity(user);
        await _gate.WaitAsync(token);
        try { return Preferences(await LoadAsync(token), identity.Provider, identity.Subject) with { }; }
        finally { _gate.Release(); }
    }

    public async Task SavePreferencesAsync(ClaimsPrincipal user, NotificationPreferences preferences, CancellationToken token = default)
    {
        var identity = BrowserIdentity(user);
        var copy = preferences with { };
        copy.Validate();
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadAsync(token);
            var settings = (state.Preferences ?? []).Where(p => p.Provider != identity.Provider || p.Subject != identity.Subject)
                .Append(new AccountPreferences(identity.Provider, identity.Subject, copy)).ToArray();
            await SaveAsync(state with { Preferences = settings }, token);
            lock (_observationSync)
            {
                _instances.Reset(identity.Provider, identity.Subject);
                ObserveInstances();
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    internal async Task<UpdateNotice[]> GetNoticesAsync(ClaimsPrincipal user, CancellationToken token = default)
    {
        if (!CanView(user)) return [];
        var identity = BrowserIdentity(user);
        PushState state;
        await _gate.WaitAsync(token);
        try { state = await LoadAsync(token); }
        finally { _gate.Release(); }
        return Notices(state, identity.Provider, identity.Subject, user);
    }

    private UpdateNotice[] Notices(PushState state, string provider, string subject, ClaimsPrincipal user) =>
        _instances.GetNotices(provider, subject, user, DateTimeOffset.UtcNow)
            .Concat(Preferences(state, provider, subject).Updates
                ? UpdateNotices.All(updates.GetSnapshot(), releases.GetSnapshot(), clusters.GetClusters(), user, content.GetSnapshot)
                : []).ToArray();

    internal static NotificationPreferences Preferences(PushState state, string provider, string subject) =>
        state.Preferences?.FirstOrDefault(p => p.Provider == provider && p.Subject == subject)?.Options ?? new();

    private void ObserveInstances()
    {
        bool changed = false;
        lock (_observationSync)
        {
            var settings = Volatile.Read(ref _state)?.Preferences;
            if (settings is null || settings.Length == 0) return;
            var runtime = supervisor.GetSnapshots();
            var observations = agents.GetAgents();
            var definitions = clusters.GetClusters();
            var now = DateTimeOffset.UtcNow;
            foreach (var account in settings)
                changed |= _instances.Observe(account.Provider, account.Subject, account.Options, runtime, observations, definitions, now);
        }
        if (changed) Changed?.Invoke();
    }

    public async Task<string> GetPublicKeyAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return (await LoadAsync(token)).PublicKey; }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsRegisteredAsync(ClaimsPrincipal user, string endpoint, CancellationToken token = default)
    {
        var identity = BrowserIdentity(user);
        await _gate.WaitAsync(token);
        try { return (await LoadAsync(token)).Subscriptions.Any(item => item.Provider == identity.Provider
            && item.Subject == identity.Subject && item.Endpoint == endpoint); }
        finally { _gate.Release(); }
    }

    public async Task SubscribeAsync(ClaimsPrincipal user, BrowserPushSubscription subscription, CancellationToken token = default)
    {
        var identity = BrowserIdentity(user);
        Validate(subscription);
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadAsync(token);
            var next = state with { Subscriptions = RegisterSubscription(state.Subscriptions,
                identity.Provider, identity.Subject, subscription, null) };
            await SaveAsync(next, token);
        }
        finally { _gate.Release(); }
    }

    public async Task UnsubscribeAsync(ClaimsPrincipal user, string endpoint, CancellationToken token = default)
    {
        var identity = BrowserIdentity(user);
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadAsync(token);
            var remaining = RemoveSubscription(state.Subscriptions, identity.Provider, identity.Subject, endpoint);
            if (remaining.Count == state.Subscriptions.Count) return;
            await SaveAsync(state with { Subscriptions = remaining }, token);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agents.Changed += ObserveInstances;
        supervisor.Changed += ObserveInstances;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Load persisted preferences even when no browser currently has push enabled.
                    await _gate.WaitAsync(stoppingToken);
                    try { await LoadAsync(stoppingToken); }
                    finally { _gate.Release(); }
                    ObserveInstances();
                    await SendPendingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogError(error, "Browser push notification sweep failed."); }
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            agents.Changed -= ObserveInstances;
            supervisor.Changed -= ObserveInstances;
        }
    }

    internal async Task SendPendingAsync(CancellationToken token)
    {
        PushState state;
        await _gate.WaitAsync(token);
        try { state = await LoadAsync(token); }
        finally { _gate.Release(); }
        foreach (var item in state.Subscriptions)
        {
            token.ThrowIfCancellationRequested();
            var principal = item.Provider switch
            {
                QuasarAuthSchemes.Steam => roles.CreateSteamPrincipal(item.Subject),
                QuasarAuthSchemes.TrustedNetwork => roles.CreateTrustedNetworkPrincipal(),
                _ => null,
            };
            if (principal is null || !CanView(principal)) continue;
            var notices = Notices(state, item.Provider, item.Subject, principal);
            var current = item;
            // Keep receipts only for outstanding notices. Each browser owns its delivery history.
            string[] retained = DeliveredKeys(current).Intersect(notices.Select(n => n.Key)).ToArray();
            if (!DeliveredKeys(current).SequenceEqual(retained))
                current = await UpdateSubscriptionAsync(current, null, false, token, retained);
            if (current is null) continue;
            foreach (var notice in PendingNotices(notices, current).Take(10))
            {
                if (current is null) break;
                var expected = current;
                try
                {
                    await _client.SendNotificationAsync(new PushSubscription(item.Endpoint, item.P256dh, item.Auth),
                        JsonSerializer.Serialize(new { title = notice.Title, body = notice.Body, url = notice.Url, tag = notice.Key }, Json),
                        new VapidDetails(VapidSubject, state.PublicKey, state.PrivateKey), token);
                    current = await UpdateSubscriptionAsync(expected, notice.Key, remove: false, token);
                    if (current is null) break;
                }
                catch (WebPushException error) when (error.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                {
                    await UpdateSubscriptionAsync(expected, null, remove: true, token);
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogWarning(error, "Could not send browser push notification."); }
            }
        }
    }

    internal static string[] DeliveredKeys(StoredSubscription item) => item.DeliveredNoticeKeys
        ?? (item.LastNoticeKey is null ? [] : [item.LastNoticeKey]);

    internal static UpdateNotice[] PendingNotices(IEnumerable<UpdateNotice> notices, StoredSubscription item) =>
        notices.Where(n => !DeliveredKeys(item).Contains(n.Key, StringComparer.Ordinal)).ToArray();

    internal static List<StoredSubscription> RegisterSubscription(List<StoredSubscription> existing,
        string provider, string subject, BrowserPushSubscription subscription, string? noticeKey)
    {
        var previous = existing.FirstOrDefault(item => item.Endpoint == subscription.Endpoint);
        if (existing.Count(item => item.Provider == provider && item.Subject == subject) >= 16
            && (previous is null || previous.Provider != provider || previous.Subject != subject))
            throw new InvalidOperationException("This account already has 16 push subscriptions. Disable one from its browser first.");

        var stored = new StoredSubscription(provider, subject, subscription.Endpoint, subscription.P256dh,
            subscription.Auth, previous?.Provider == provider && previous.Subject == subject
                ? previous.LastNoticeKey : noticeKey,
            previous?.Provider == provider && previous.Subject == subject ? previous.DeliveredNoticeKeys : null);
        return existing.Where(item => item.Endpoint != subscription.Endpoint).Append(stored).ToList();
    }

    internal static List<StoredSubscription> RemoveSubscription(List<StoredSubscription> existing,
        string provider, string subject, string endpoint) =>
        existing.Where(item => item.Endpoint != endpoint || item.Provider != provider || item.Subject != subject).ToList();

    private async Task<StoredSubscription?> UpdateSubscriptionAsync(StoredSubscription expected, string? key,
        bool remove, CancellationToken token, string[]? retained = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadAsync(token);
            var current = state.Subscriptions.FirstOrDefault(item => item.Endpoint == expected.Endpoint);
            if (current != expected) return null;
            var updated = remove ? null : expected with { LastNoticeKey = key,
                DeliveredNoticeKeys = retained ?? DeliveredKeys(expected).Concat(key is null ? [] : new[] { key }).Distinct().ToArray() };
            await SaveAsync(state with { Subscriptions = state.Subscriptions.Select(item => item == expected
                ? updated : item).OfType<StoredSubscription>().ToList() }, token);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task<PushState> LoadAsync(CancellationToken token)
    {
        if (_state is not null) return _state;
        if (File.Exists(_path))
        {
            var encrypted = await File.ReadAllTextAsync(_path, token);
            var loaded = JsonSerializer.Deserialize<PushState>(_protector.Unprotect(encrypted), Json)
                ?? throw new InvalidDataException("Push notification state is empty.");
            if (string.IsNullOrWhiteSpace(loaded.PublicKey) || string.IsNullOrWhiteSpace(loaded.PrivateKey)
                || loaded.Subscriptions is null)
                throw new InvalidDataException("Push notification state is incomplete.");
            foreach (var account in loaded.Preferences ?? []) account.Options.Validate();
            _state = loaded;
            return _state;
        }
        var keys = VapidHelper.GenerateVapidKeys();
        var state = new PushState(keys.PublicKey, keys.PrivateKey, []);
        await SaveAsync(state, token);
        return state;
    }

    private async Task SaveAsync(PushState state, CancellationToken token)
    {
        await AtomicFileWriter.WriteTextAsync(_path, _protector.Protect(JsonSerializer.Serialize(state, Json)), token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _state = state;
    }

    private static (string Provider, string Subject) BrowserIdentity(ClaimsPrincipal user)
    {
        if (!CanView(user)) throw new UnauthorizedAccessException("Sign in with a viewer account to enable push notifications.");
        string? provider = user.FindFirst(QuasarClaimTypes.Provider)?.Value;
        string? subject = provider switch
        {
            QuasarAuthSchemes.Steam => user.FindFirst(QuasarClaimTypes.SteamId)?.Value,
            QuasarAuthSchemes.TrustedNetwork => QuasarAuthSchemes.TrustedNetwork,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(subject) ? throw new UnauthorizedAccessException("Browser push requires a user session.")
            : (provider!, subject);
    }

    private static bool CanView(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
        && (user.IsInRole(QuasarRoles.Viewer) || user.IsInRole(QuasarRoles.Editor) || user.IsInRole(QuasarRoles.Admin));

    internal static void Validate(BrowserPushSubscription subscription)
    {
        if (subscription is null || subscription.Endpoint?.Length is < 1 or > 2048
            || !Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !AllowedPushHost(endpoint.Host) || DecodeKey(subscription.P256dh) is not { Length: 65 } publicKey
            || publicKey[0] != 4
            || DecodeKey(subscription.Auth)?.Length != 16)
            throw new InvalidDataException("Browser returned an invalid push subscription.");
    }

    private static bool AllowedPushHost(string host) => host is "fcm.googleapis.com" or "updates.push.services.mozilla.com"
        or "push.services.mozilla.com" or "web.push.apple.com"
        || host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);

    private static byte[]? DecodeKey(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return null;
        try { return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
            + new string('=', (4 - value.Length % 4) % 4)); }
        catch (FormatException) { return null; }
    }

    internal sealed record PushState(string PublicKey, string PrivateKey, List<StoredSubscription> Subscriptions,
        AccountPreferences[]? Preferences = null);
    internal sealed record AccountPreferences(string Provider, string Subject, NotificationPreferences Options);
    internal sealed record StoredSubscription(string Provider, string Subject, string Endpoint, string P256dh, string Auth,
        string? LastNoticeKey, string[]? DeliveredNoticeKeys = null);
}
