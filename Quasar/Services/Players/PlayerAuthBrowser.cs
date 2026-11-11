using System.Security.Cryptography;
using Quasar.Hosting;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

/// <summary>Canonical public address and short-lived, game-bound browser authorization requests.</summary>
public sealed class PlayerAuthBrowser(PlayerAccessCatalog catalog, HostedPanelService hosted,
    QuasarAuthOptions auth, IHttpClientFactory clients)
{
    public const string Path = "/player-auth";
    private readonly object sync = new();
    private readonly SemaphoreSlim verification = new(1, 1);
    private readonly Dictionary<string, PlayerAuthRequest> requests = new(StringComparer.Ordinal);
    private string? challenge;
    private string? proof;
    private DateTimeOffset challengeUntil;

    public string? PublicUrl
    {
        get
        {
            if (!auth.Enabled || !auth.Steam.Enabled || !string.Equals(auth.DefaultProvider, QuasarAuthSchemes.Steam, StringComparison.OrdinalIgnoreCase)) return null;
            var settings = catalog.GetSettings();
            if (hosted.AccessActive && hosted.State is { Choice: "hosted", Credential: not null } state)
                return state.DashboardUrl.TrimEnd('/');
            return settings.VerifiedUrl == settings.PublicUrl && settings.VerifiedUntil > DateTimeOffset.UtcNow
                && PlayerAccessCatalog.IsPublicUrl(settings.PublicUrl) ? settings.PublicUrl : null;
        }
    }

    public bool Ready => PublicUrl is not null && catalog.GetSettings() is { DiscordClientId.Length: > 0, DiscordClientSecret.Length: > 0 };

    public bool IsCanonical(HttpContext context)
    {
        var url = PublicUrl;
        return url is not null && Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase,
            UriKind.Absolute, out var actual) && actual.UserInfo.Length == 0 && new Uri(url).Equals(actual);
    }

    public string Begin(string target, string steamId)
    {
        if (!Ready) throw new InvalidOperationException("Player authentication needs Discord OAuth and a verified public website or active Nebula enrollment.");
        if (!RbacConfigCatalog.IsSteamId64(steamId)) throw new ArgumentException("Invalid Steam identity.");
        lock (sync)
        {
            foreach (var expired in requests.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) requests.Remove(expired);
            var existing = requests.FirstOrDefault(p => p.Value.Target == target && p.Value.SteamId == steamId);
            if (existing.Key is not null) return PublicUrl + Path + "?request=" + existing.Key;
            if (requests.Count >= 4096) throw new InvalidOperationException("Too many pending authorization requests. Try again later.");
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            requests.Add(key, new(target, steamId, DateTimeOffset.UtcNow.AddMinutes(20)));
            return PublicUrl + Path + "?request=" + key;
        }
    }

    public PlayerAuthRequest? Find(string? key)
    {
        lock (sync) return key is not null && requests.TryGetValue(key, out var request)
            && request.ExpiresAt > DateTimeOffset.UtcNow ? request : null;
    }

    public void Consume(string? key) { if (key is not null) lock (sync) requests.Remove(key); }

    public string? ChallengeProof(string? nonce)
    { lock (sync) return nonce == challenge && challengeUntil > DateTimeOffset.UtcNow ? proof : null; }

    public async Task VerifyAsync(CancellationToken token = default)
    {
        await verification.WaitAsync(token);
        try
        {
            var url = catalog.GetSettings().PublicUrl;
            if (!PlayerAccessCatalog.IsPublicUrl(url)) throw new InvalidOperationException("Save a public HTTPS website address first.");
            string nonce, expected;
            lock (sync)
            {
                challenge = nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                proof = expected = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                challengeUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            using var response = await clients.CreateClient("PlayerDomainVerification").PostAsJsonAsync(
                HostedPanelState.Origin + "/api/v1/quasar/verify-domain", new { url, challenge = nonce, proof = expected }, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Public website verification failed. Check public DNS, HTTPS, and reverse-proxy routing; the Nebula verifier must support this feature.");
            var result = await response.Content.ReadFromJsonAsync<VerificationResult>(token);
            if (result is not { Verified: true } || result.Url != url || result.Challenge != nonce)
                throw new InvalidDataException("Invalid public website verification response.");
            await catalog.RecordVerificationAsync(url, DateTimeOffset.UtcNow.AddDays(7), token);
        }
        finally
        {
            lock (sync) { challenge = null; proof = null; }
            verification.Release();
        }
    }

    private sealed record VerificationResult(bool Verified, string Url, string Challenge);
}

public sealed record PlayerAuthRequest(string Target, string SteamId, DateTimeOffset ExpiresAt);
