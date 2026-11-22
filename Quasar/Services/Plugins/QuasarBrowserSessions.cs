using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

/// <summary>Server-owned identity binding, never a peer-supplied DTO.</summary>
public sealed record QuasarBrowserSession(ClaimsPrincipal User, string Id, string Owner, string PathBase,
    DateTimeOffset ExpiresAt, CancellationToken Revoked);

public sealed class QuasarBrowserSessions(IDataProtectionProvider protection) : IDisposable
{
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("Quasar.SDK.Session.v1").ToTimeLimitedDataProtector();
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private static readonly object CacheKey = new();

    public async Task<QuasarBrowserSession> BindHttpSessionAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(CacheKey, out var cached))
        {
            var existing = (QuasarBrowserSession)cached!;
            Verify(existing, http.RequestAborted);
            return existing;
        }
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true || user.IsQuasarServicePrincipal())
            throw new QuasarOperationException(401, "A Quasar browser session is required.");
        var owner = Owner(user) ?? throw new QuasarOperationException(401, "The session has no user identity.");
        var path = http.Request.PathBase.Value ?? "";
        var name = CookieName(path);
        var expiry = DateTimeOffset.UtcNow.AddHours(8);
        var authentication = await http.AuthenticateAsync();
        var login = DashboardLogin.Id(authentication.Properties, http.Request.PathBase);
        if (authentication.Ticket?.AuthenticationScheme == QuasarAuthSchemes.Cookie && login is null)
            throw new QuasarOperationException(401, "Sign in again to open a Quasar browser session.");

        // All tabs for this sign-in bind to its ticket, including simultaneous first requests.
        // Network-authenticated callers have no sign-in ticket and use the protected SDK cookie.
        var cookie = login is null ? ReadSdkCookie(http, path, owner) : null;
        var id = login ?? cookie?.Id;
        if (cookie is { } trusted) expiry = trusted.Expiry;
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            http.Response.Cookies.Append(name, _protector.Protect(JsonSerializer.Serialize(new CookieIdentity(owner, path, id)), expiry),
                new CookieOptions { Path = path + "/", HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict,
                    IsEssential = true, Expires = expiry });
        }
        if (authentication.Properties?.ExpiresUtc is { } authExpiry && authExpiry < expiry) expiry = authExpiry;
        if (expiry <= DateTimeOffset.UtcNow) throw new QuasarOperationException(401, "The Quasar session has expired.");
        lock (_gate)
        {
            foreach (var stale in _sessions.Where(pair => pair.Value.Binding.ExpiresAt <= DateTimeOffset.UtcNow ||
                         pair.Value.LastSeen < DateTimeOffset.UtcNow.AddMinutes(-15)).ToArray())
            { stale.Value.Stop.Cancel(); stale.Value.Stop.Dispose(); _sessions.Remove(stale.Key); }
            _sessions.TryGetValue(id, out var entry);
            // A shorter verified ticket must also shorten existing peer/transfer authority.
            if (entry is not null && expiry < entry.Binding.ExpiresAt)
            {
                entry.Stop.Cancel();
                entry.Stop.Dispose();
                _sessions.Remove(id);
                entry = null;
            }
            if (entry is not null && (entry.Binding.Owner != owner || entry.Binding.PathBase != path))
                throw new QuasarOperationException(401, "The browser session belongs to another sign-in.");
            if (entry is null)
            {
                if (login is not null && !DashboardLogin.IsActive(login))
                    throw new QuasarOperationException(401, "The Quasar login has ended.");
                if (_sessions.Count >= 512 || _sessions.Values.Count(item => item.Binding.Owner == owner) >= 32)
                    throw new QuasarOperationException(429, "Too many browser sessions.");
                var stop = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(0, (expiry - DateTimeOffset.UtcNow).Ticks)));
                var binding = new QuasarBrowserSession(new ClaimsPrincipal(user.Identities.Select(identity => new ClaimsIdentity(identity))),
                    id, owner, path, expiry, stop.Token);
                entry = new Entry(binding, stop, login);
                _sessions.Add(id, entry);
            }
            Verify(entry.Binding, http.RequestAborted);
            http.Items[CacheKey] = entry.Binding;
            return entry.Binding;
        }
    }

    public void Verify(QuasarBrowserSession session, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (session.Revoked.IsCancellationRequested || session.ExpiresAt <= DateTimeOffset.UtcNow ||
                !_sessions.TryGetValue(session.Id, out var entry) || !ReferenceEquals(entry.Binding, session) ||
                !entry.HasActiveLogin())
                throw new QuasarOperationException(401, "The Quasar browser session has expired or was revoked.");
            entry.LastSeen = DateTimeOffset.UtcNow;
        }
    }

    public async Task RevokeAsync(HttpContext http)
    {
        var path = http.Request.PathBase.Value ?? "";
        var authentication = await http.AuthenticateAsync();
        var login = DashboardLogin.Id(authentication.Properties, http.Request.PathBase);
        var id = login ?? (Owner(http.User) is { } owner ? ReadSdkCookie(http, path, owner)?.Id : null);
        lock (_gate)
        {
            if (login is not null) DashboardLogin.Revoke(login);
            if (id is not null && _sessions.Remove(id, out var entry)) { entry.Stop.Cancel(); entry.Stop.Dispose(); }
        }
        if (http.Request.Cookies.ContainsKey(CookieName(path)))
            http.Response.Cookies.Delete(CookieName(path), new CookieOptions { Path = path + "/" });
    }

    private static string? Owner(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) is { } subject
            ? (user.FindFirstValue(QuasarClaimTypes.Provider) ?? user.Identity?.AuthenticationType) + "/" + subject : null;

    private (string Id, DateTimeOffset Expiry)? ReadSdkCookie(HttpContext http, string path, string owner)
    {
        if (http.Request.Cookies.TryGetValue(CookieName(path), out var cookie))
            try
            {
                var value = JsonSerializer.Deserialize<CookieIdentity>(_protector.Unprotect(cookie, out var cookieExpiry));
                if (value?.Owner == owner && value.Path == path) return (value.Id, cookieExpiry);
            }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { }
        return null;
    }

    private static string CookieName(string path) =>
        "Quasar.SdkSession." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16];

    public void Dispose()
    {
        lock (_gate) { foreach (var entry in _sessions.Values) { entry.Stop.Cancel(); entry.Stop.Dispose(); } _sessions.Clear(); }
    }
    private sealed record CookieIdentity(string Owner, string Path, string Id);
    private sealed class Entry(QuasarBrowserSession binding, CancellationTokenSource stop, string? login)
    {
        private long _checkedAt = Stopwatch.GetTimestamp();

        // Local logout cancels the binding immediately. While workers overlap during an
        // update, pick up deletion by the other worker without touching disk per bulk frame.
        public bool HasActiveLogin()
        {
            if (login is null || Stopwatch.GetElapsedTime(_checkedAt) < TimeSpan.FromSeconds(1))
                return true;
            _checkedAt = Stopwatch.GetTimestamp();
            if (DashboardLogin.IsActive(login)) return true;
            Stop.Cancel();
            return false;
        }

        public QuasarBrowserSession Binding { get; } = binding;
        public CancellationTokenSource Stop { get; } = stop;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
}
