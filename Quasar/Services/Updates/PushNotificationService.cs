using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Quasar.Services.Auth;
using WebPush;

namespace Quasar.Services.Updates;

public sealed record BrowserPushSubscription(string Endpoint, string P256dh, string Auth);

/// <summary>Stores browser subscriptions and sends the current update notice to each authorized viewer.</summary>
public sealed class PushNotificationService(
    IDataProtectionProvider protection, QuasarUpdateService updates, ClusterReleaseMonitor releases,
    ClusterCatalog clusters, QuasarRoleMapper roles, ILogger<PushNotificationService> logger) : BackgroundService
{
    private const string VapidSubject = "https://github.com/CometWorks/quasar";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("Quasar.PushNotifications.v1");
    private readonly string _path = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "PushNotifications.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WebPushClient _client = new();
    private PushState? _state;

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
            var notice = CurrentNotice(user);
            var next = state with { Subscriptions = RegisterSubscription(state.Subscriptions,
                identity.Provider, identity.Subject, subscription, notice?.Key) };
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Browser push notification sweep failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
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
            var notice = CurrentNotice(principal);
            if (notice is null || notice.Key == item.LastNoticeKey) continue;
            try
            {
                await _client.SendNotificationAsync(new PushSubscription(item.Endpoint, item.P256dh, item.Auth),
                    JsonSerializer.Serialize(new { title = notice.Title, body = notice.Body, url = notice.Url, tag = notice.Key }, Json),
                    new VapidDetails(VapidSubject, state.PublicKey, state.PrivateKey), token);
                await UpdateSubscriptionAsync(item, notice.Key, remove: false, token);
            }
            catch (WebPushException error) when (error.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                await UpdateSubscriptionAsync(item, null, remove: true, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning(error, "Could not send browser push notification."); }
        }
    }

    private UpdateNotice? CurrentNotice(ClaimsPrincipal user) =>
        UpdateNotices.Current(updates.GetSnapshot(), releases.GetSnapshot(), clusters.GetClusters(), user);

    internal static List<StoredSubscription> RegisterSubscription(List<StoredSubscription> existing,
        string provider, string subject, BrowserPushSubscription subscription, string? noticeKey)
    {
        var previous = existing.FirstOrDefault(item => item.Endpoint == subscription.Endpoint);
        if (existing.Count(item => item.Provider == provider && item.Subject == subject) >= 16
            && (previous is null || previous.Provider != provider || previous.Subject != subject))
            throw new InvalidOperationException("This account already has 16 push subscriptions. Disable one from its browser first.");

        var stored = new StoredSubscription(provider, subject, subscription.Endpoint, subscription.P256dh,
            subscription.Auth, previous?.Provider == provider && previous.Subject == subject
                ? previous.LastNoticeKey : noticeKey);
        return existing.Where(item => item.Endpoint != subscription.Endpoint).Append(stored).ToList();
    }

    internal static List<StoredSubscription> RemoveSubscription(List<StoredSubscription> existing,
        string provider, string subject, string endpoint) =>
        existing.Where(item => item.Endpoint != endpoint || item.Provider != provider || item.Subject != subject).ToList();

    private async Task UpdateSubscriptionAsync(StoredSubscription expected, string? key, bool remove, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadAsync(token);
            var current = state.Subscriptions.FirstOrDefault(item => item.Endpoint == expected.Endpoint);
            if (current != expected) return;
            await SaveAsync(state with { Subscriptions = state.Subscriptions.Select(item => item == expected
                ? remove ? null : item with { LastNoticeKey = key } : item).OfType<StoredSubscription>().ToList() }, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<PushState> LoadAsync(CancellationToken token)
    {
        if (_state is not null) return _state;
        if (File.Exists(_path))
        {
            var encrypted = await File.ReadAllTextAsync(_path, token);
            _state = JsonSerializer.Deserialize<PushState>(_protector.Unprotect(encrypted), Json)
                ?? throw new InvalidDataException("Push notification state is empty.");
            if (string.IsNullOrWhiteSpace(_state.PublicKey) || string.IsNullOrWhiteSpace(_state.PrivateKey)
                || _state.Subscriptions is null)
                throw new InvalidDataException("Push notification state is incomplete.");
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

    private sealed record PushState(string PublicKey, string PrivateKey, List<StoredSubscription> Subscriptions);
    internal sealed record StoredSubscription(string Provider, string Subject, string Endpoint, string P256dh, string Auth,
        string? LastNoticeKey);
}
