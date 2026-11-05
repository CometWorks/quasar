using System.Security.Cryptography;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Services.Auth;

namespace Quasar.Services.Assistant;

public sealed record QuasarPlayerLink(
    string DiscordUserId,
    string SteamId,
    DateTimeOffset LinkedAtUtc
);

/// <summary>
/// Discord to Steam links of players (R13). Written only by the link exchange (<c>!link</c> in game,
/// <c>/link CODE</c> on Discord) and by an admin removing a link on the Players page.
/// </summary>
public sealed class QuasarPlayerLinks
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public const int CodeLength = 8;

    // No 0/O, 1/I/L: players type the code from the game chat.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
    private List<QuasarPlayerLink>? _links;

    public QuasarPlayerLinks()
        : this(MagnetarPaths.GetQuasarPlayerLinksPath()) { }

    internal QuasarPlayerLinks(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    public event Action? Changed;

    /// <summary>A new one-time code for the player; it replaces the player's previous code.</summary>
    public string IssueCode(string steamId)
    {
        if (!RbacConfigCatalog.IsSteamId64(steamId))
            throw new ArgumentException("A link code needs a 17-digit SteamID64.", nameof(steamId));

        var now = _time.GetUtcNow();
        lock (_sync)
        {
            foreach (
                var stale in _codes
                    .Where(pair => pair.Value.SteamId == steamId || pair.Value.ExpiresUtc <= now)
                    .Select(pair => pair.Key)
                    .ToList()
            )
                _codes.Remove(stale);

            string code;
            do code = RandomNumberGenerator.GetString(Alphabet, CodeLength);
            while (_codes.ContainsKey(code));
            _codes[code] = new PendingCode(steamId, now + CodeLifetime);
            return code;
        }
    }

    /// <summary>Links the Discord user to the code's Steam ID; null for an unknown or expired code.
    /// A Discord user has one Steam ID and a Steam ID one Discord user: a new link replaces older ones.</summary>
    public async Task<QuasarPlayerLink?> RedeemAsync(
        string discordUserId,
        string code,
        CancellationToken token = default
    )
    {
        if (!ulong.TryParse(discordUserId, out _))
            throw new ArgumentException("A Discord user ID is required.", nameof(discordUserId));

        var normalized = new string(
            (code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()
        ).ToUpperInvariant();
        var now = _time.GetUtcNow();
        PendingCode? pending;
        lock (_sync)
        {
            if (!_codes.Remove(normalized, out pending) || pending.ExpiresUtc <= now)
                return null;
        }

        var link = new QuasarPlayerLink(discordUserId, pending.SteamId, now);
        await UpdateAsync(
            links =>
            {
                links.RemoveAll(existing =>
                    existing.DiscordUserId == discordUserId || existing.SteamId == pending.SteamId
                );
                links.Add(link);
                return true;
            },
            token
        );
        return link;
    }

    public QuasarPlayerLink? FindByDiscordUser(string discordUserId) =>
        GetLinks().FirstOrDefault(link => link.DiscordUserId == discordUserId);

    public QuasarPlayerLink? FindBySteamId(string steamId) =>
        GetLinks().FirstOrDefault(link => link.SteamId == steamId);

    public IReadOnlyList<QuasarPlayerLink> GetLinks()
    {
        lock (_sync)
            return Load().ToList();
    }

    /// <summary>Removes the Steam ID's link; false when there was none.</summary>
    public Task<bool> RemoveAsync(string steamId, CancellationToken token = default) =>
        UpdateAsync(links => links.RemoveAll(link => link.SteamId == steamId) > 0, token);

    private async Task<bool> UpdateAsync(
        Func<List<QuasarPlayerLink>, bool> change,
        CancellationToken token
    )
    {
        await _saveLock.WaitAsync(token);
        try
        {
            string json;
            lock (_sync)
            {
                var links = Load().ToList();
                if (!change(links))
                    return false;
                _links = links;
                json = JsonSerializer.Serialize(new LinkFile(links), Json);
            }

            await AtomicFileWriter.WritePrivateTextAsync(_path, json, token);
        }
        finally
        {
            _saveLock.Release();
        }

        Changed?.Invoke();
        return true;
    }

    private List<QuasarPlayerLink> Load()
    {
        if (_links is not null)
            return _links;
        try
        {
            _links = File.Exists(_path)
                ? JsonSerializer
                    .Deserialize<LinkFile>(File.ReadAllText(_path), Json)
                    ?.Links.Where(link =>
                        ulong.TryParse(link.DiscordUserId, out _)
                        && RbacConfigCatalog.IsSteamId64(link.SteamId)
                    )
                    .ToList()
                    ?? []
                : [];
        }
        catch (JsonException)
        {
            // A damaged file must not lock players out of asking; the next link rewrites it.
            _links = [];
        }

        return _links;
    }

    private sealed record PendingCode(string SteamId, DateTimeOffset ExpiresUtc);

    private sealed record LinkFile(List<QuasarPlayerLink> Links);
}
