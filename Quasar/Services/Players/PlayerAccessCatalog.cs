using System.Text.Json;
using System.Security.Cryptography;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

public sealed class PlayerAccessSettings
{
    public long Revision { get; set; }
    public string PublicUrl { get; set; } = "";
    public string DiscordClientId { get; set; } = "";
    public string DiscordClientSecret { get; set; } = "";
    public string? VerifiedUrl { get; set; }
    public DateTimeOffset? VerifiedUntil { get; set; }
    public List<PlayerAccessPolicy> Policies { get; set; } = [];
}

public sealed class PlayerAccessPolicy
{
    public string Target { get; set; } = "";
    public ulong GuildId { get; set; }
    public bool Enabled { get; set; }
    public bool Whitelist { get; set; }
    public string Instructions { get; set; } = "";
    public List<ulong> RequiredRoles { get; set; } = [];
    public List<ulong> PriorityRoles { get; set; } = [];
    public List<PlayerPromotionMapping> Promotions { get; set; } = [];
    public List<string> AllowedSteamIds { get; set; } = [];
}

public sealed record PlayerPromotionMapping(ulong RoleId, int Level);
public sealed record PlayerAuthorization(string Target, string AuthenticationId);

/// <summary>One durable owner for player-access settings and explicit server authorizations.</summary>
public sealed class PlayerAccessCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private AccessFile file;
    private string? unreadableCredential;
    public bool CredentialUnavailable => unreadableCredential is not null;

    public PlayerAccessCatalog(IDataProtectionProvider protection)
        : this(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "player-access.json"), protection) { }

    internal PlayerAccessCatalog(string path, IDataProtectionProvider protection)
    {
        this.path = path;
        protector = protection.CreateProtector("Quasar.PlayerAccess.v1");
        file = File.Exists(path) ? JsonSerializer.Deserialize<AccessFile>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Player access settings are damaged.") : new();
        if (file.Settings.DiscordClientSecret.Length != 0)
        {
            try { file.Settings.DiscordClientSecret = protector.Unprotect(file.Settings.DiscordClientSecret); }
            catch (CryptographicException)
            {
                unreadableCredential = file.Settings.DiscordClientSecret;
                file.Settings.DiscordClientSecret = "";
            }
        }
        Validate(file.Settings);
    }

    public event Action? Changed;
    public PlayerAccessSettings GetSettings() { lock (sync) return Copy(file.Settings); }
    public bool IsAuthorized(string target, string authenticationId)
    { lock (sync) return file.Authorizations.Contains(new(target, authenticationId)); }

    internal void EnsureServerIdentityChangeAllowed(string current, string desired)
    {
        if (current != desired && GetSettings().Policies.Any(p => p.Enabled
            && string.Equals(p.Target, "server:" + current, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Disable player access, confirm revocation and save the world before changing its canonical server identity. Display name changes do not need this.");
    }

    public async Task SaveSettingsAsync(PlayerAccessSettings settings, CancellationToken token = default)
    {
        var next = Copy(settings);
        next.PublicUrl = next.PublicUrl.Trim();
        if (next.PublicUrl.Length != 0) next.PublicUrl = CanonicalPublicUrl(next.PublicUrl)
            ?? throw new ArgumentException("Use a public HTTPS hostname without a path, credentials, query or fragment.");
        Validate(next);
        await ChangeAsync(current =>
        {
            if (next.Revision != current.Settings.Revision)
                throw new InvalidOperationException("Player access settings changed. Reload before saving.");
            if (next.PublicUrl != current.Settings.PublicUrl)
            { next.VerifiedUrl = null; next.VerifiedUntil = null; }
            // Verification is written only by the verifier, never accepted from the settings form.
            else { next.VerifiedUrl = current.Settings.VerifiedUrl; next.VerifiedUntil = current.Settings.VerifiedUntil; }
            next.Revision = NextRevision(next.Revision);
            current.Settings = next;
        }, token);
    }

    internal Task RecordVerificationAsync(string url, DateTimeOffset until, CancellationToken token) =>
        ChangeAsync(current =>
        {
            if (current.Settings.PublicUrl != url) throw new InvalidOperationException("Public URL changed during verification.");
            current.Settings.VerifiedUrl = url;
            current.Settings.VerifiedUntil = until;
            current.Settings.Revision = NextRevision(current.Settings.Revision);
        }, token);

    internal Task AuthorizeAsync(string target, string authenticationId, CancellationToken token) =>
        ChangeAsync(current =>
        {
            if (!current.Settings.Policies.Any(p => p.Target == target && p.Enabled))
                throw new InvalidOperationException("Player authorization is not enabled for this server.");
            var authorization = new PlayerAuthorization(target, authenticationId);
            if (!current.Authorizations.Contains(authorization)) current.Authorizations.Add(authorization);
        }, token);

    private async Task ChangeAsync(Action<AccessFile> change, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            AccessFile next;
            lock (sync) next = Copy(file);
            change(next);
            var persisted = Copy(next);
            if (persisted.Settings.DiscordClientSecret.Length != 0)
                persisted.Settings.DiscordClientSecret = protector.Protect(persisted.Settings.DiscordClientSecret);
            else if (unreadableCredential is not null)
                persisted.Settings.DiscordClientSecret = unreadableCredential;
            await AtomicFileWriter.WritePrivateTextAsync(path, JsonSerializer.Serialize(persisted, Json), token);
            lock (sync) file = next;
            if (next.Settings.DiscordClientSecret.Length != 0) unreadableCredential = null;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    internal static void Validate(PlayerAccessSettings settings)
    {
        if (settings.PublicUrl.Length != 0 && !IsPublicUrl(settings.PublicUrl))
            throw new ArgumentException("Use a public HTTPS hostname with no path, credentials, query or fragment.");
        if (settings.DiscordClientId.Length != 0 && (!ulong.TryParse(settings.DiscordClientId, out var id) || id == 0))
            throw new ArgumentException("Discord application ID must be a numeric identifier.");
        if (settings.DiscordClientSecret.Length > 512 || settings.Policies.Count > 1000
            || settings.Policies.Select(p => p.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Policies.Count)
            throw new ArgumentException("Invalid or duplicate player access settings.");
        foreach (var policy in settings.Policies)
        {
            if (policy.Target.Length is < 1 or > 160 || !(policy.Target.StartsWith("server:") || policy.Target.StartsWith("cluster:"))
                || policy.Enabled && policy.GuildId == 0 || policy.Instructions.Length > 5000
                || policy.RequiredRoles.Count > 100 || policy.PriorityRoles.Count > 100 || policy.Promotions.Count > 100
                || policy.AllowedSteamIds.Count > 1000 || policy.RequiredRoles.Concat(policy.PriorityRoles).Any(r => r == 0)
                || policy.Promotions.Any(p => p.RoleId == 0 || p.Level is < 0 or > 4)
                || policy.AllowedSteamIds.Any(s => !RbacConfigCatalog.IsSteamId64(s)))
                throw new ArgumentException("Invalid player access policy, role, Steam ID or promotion level.");
        }
    }

    internal static bool IsPublicUrl(string value) => CanonicalPublicUrl(value) is not null;
    internal static string? CanonicalPublicUrl(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && uri.HostNameType == UriHostNameType.Dns && uri.Host.Contains('.')
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) && uri.Host != "localhost"
        && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri.GetLeftPart(UriPartial.Authority) : null;

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static long NextRevision(long previous) => Math.Max(checked(previous + 1), DateTimeOffset.UtcNow.UtcTicks);
    private sealed class AccessFile
    {
        public PlayerAccessSettings Settings { get; set; } = new();
        public List<PlayerAuthorization> Authorizations { get; set; } = [];
    }
}
