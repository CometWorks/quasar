using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

public sealed record PlayerAuthorization(string Target, string AuthenticationId);

/// <summary>One durable owner for player-access settings and explicit server authorizations.</summary>
public sealed class PlayerAccessCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private AccessFile file;

    public PlayerAccessCatalog()
        : this(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "player-access.json")) { }

    internal PlayerAccessCatalog(string path)
    {
        this.path = path;
        file = File.Exists(path) ? JsonSerializer.Deserialize<AccessFile>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Player access settings are damaged.") : new();
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
        Validate(next);
        await ChangeAsync(current =>
        {
            if (next.Revision != current.Settings.Revision)
                throw new InvalidOperationException("Player access settings changed. Reload before saving.");
            next.Revision = NextRevision(next.Revision);
            current.Settings = next;
        }, token);
    }

    internal Task AuthorizeAsync(PlayerAccessPolicy policy, string authenticationId, CancellationToken token) =>
        ChangeAsync(current =>
        {
            if (!current.Settings.Policies.Any(p => p.Target == policy.Target && p.Enabled && p.GuildId == policy.GuildId))
                throw new InvalidOperationException("Player authorization is not enabled for this server.");
            var authorization = new PlayerAuthorization(policy.Target, authenticationId);
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
            await AtomicFileWriter.WritePrivateTextAsync(path, JsonSerializer.Serialize(next, Json), token);
            lock (sync) file = next;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    internal static void Validate(PlayerAccessSettings settings)
    {
        if (settings.Policies.Count > 1000
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

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static long NextRevision(long previous) => Math.Max(checked(previous + 1), DateTimeOffset.UtcNow.UtcTicks);
    private sealed class AccessFile
    {
        public PlayerAccessSettings Settings { get; set; } = new();
        public List<PlayerAuthorization> Authorizations { get; set; } = [];
    }
}
