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
        if (http.Items.TryGetValue(CacheKey, out var cached)) return (QuasarBrowserSession)cached!;
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true || user.IsQuasarServicePrincipal())
            throw new QuasarOperationException(401, "A Quasar browser session is required.");
        var owner = (user.FindFirstValue(QuasarClaimTypes.Provider) ?? user.Identity.AuthenticationType) + "/"
            + (user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new QuasarOperationException(401, "The session has no user identity."));
        var path = http.Request.PathBase.Value ?? "";
        var name = "Quasar.SdkSession." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16];
        var expiry = DateTimeOffset.UtcNow.AddHours(8);
        string? id = null;
        if (http.Request.Cookies.TryGetValue(name, out var cookie))
            try
            {
                var value = JsonSerializer.Deserialize<CookieIdentity>(_protector.Unprotect(cookie, out var cookieExpiry));
                if (value?.Owner == owner && value.Path == path) { id = value.Id; expiry = cookieExpiry; }
            }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { }
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            http.Response.Cookies.Append(name, _protector.Protect(JsonSerializer.Serialize(new CookieIdentity(owner, path, id)), expiry),
                new CookieOptions { Path = path + "/", HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict,
                    IsEssential = true, Expires = expiry });
        }
        var authentication = await http.AuthenticateAsync();
        if (authentication.Properties?.ExpiresUtc is { } authExpiry && authExpiry < expiry) expiry = authExpiry;
        if (expiry <= DateTimeOffset.UtcNow) throw new QuasarOperationException(401, "The Quasar session has expired.");
        lock (_gate)
        {
            foreach (var stale in _sessions.Where(pair => pair.Value.Binding.ExpiresAt <= DateTimeOffset.UtcNow ||
                         pair.Value.LastSeen < DateTimeOffset.UtcNow.AddMinutes(-15)).ToArray())
            { stale.Value.Stop.Cancel(); stale.Value.Stop.Dispose(); _sessions.Remove(stale.Key); }
            if (!_sessions.TryGetValue(id, out var entry))
            {
                if (_sessions.Count >= 512 || _sessions.Values.Count(item => item.Binding.Owner == owner) >= 32)
                    throw new QuasarOperationException(429, "Too many browser sessions.");
                var stop = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(0, (expiry - DateTimeOffset.UtcNow).Ticks)));
                var binding = new QuasarBrowserSession(new ClaimsPrincipal(user.Identities.Select(identity => new ClaimsIdentity(identity))),
                    id, owner, path, expiry, stop.Token);
                entry = new Entry(binding, stop);
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
                !_sessions.TryGetValue(session.Id, out var entry) || !ReferenceEquals(entry.Binding, session))
                throw new QuasarOperationException(401, "The Quasar browser session has expired or was revoked.");
            entry.LastSeen = DateTimeOffset.UtcNow;
        }
    }

    public async Task RevokeAsync(HttpContext http)
    {
        var session = await BindHttpSessionAsync(http);
        lock (_gate)
            if (_sessions.Remove(session.Id, out var entry)) { entry.Stop.Cancel(); entry.Stop.Dispose(); }
        var name = "Quasar.SdkSession." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.PathBase)))[..16];
        http.Response.Cookies.Delete(name, new CookieOptions { Path = session.PathBase + "/" });
    }

    public void Dispose()
    {
        lock (_gate) { foreach (var entry in _sessions.Values) { entry.Stop.Cancel(); entry.Stop.Dispose(); } _sessions.Clear(); }
    }
    private sealed record CookieIdentity(string Owner, string Path, string Id);
    private sealed class Entry(QuasarBrowserSession binding, CancellationTokenSource stop)
    {
        public QuasarBrowserSession Binding { get; } = binding;
        public CancellationTokenSource Stop { get; } = stop;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
}
