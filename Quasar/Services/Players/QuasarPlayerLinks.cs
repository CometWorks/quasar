using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

public sealed record QuasarPlayerLink(
    string DiscordUserId,
    string SteamId,
    DateTimeOffset LinkedAtUtc,
    string? AuthenticationId = null
)
{
    [JsonIgnore]
    public bool Authenticated => Guid.TryParseExact(AuthenticationId, "N", out _);
}

/// <summary>
/// Steam and Discord identities proven by browser authentication. Legacy code-based links remain
/// visible for migration, but cannot identify a player until both providers are authenticated.
/// </summary>
public sealed class QuasarPlayerLinks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<QuasarPlayerLink>? _links;

    public QuasarPlayerLinks()
        : this(MagnetarPaths.GetQuasarPlayerLinksPath()) { }

    internal QuasarPlayerLinks(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    public event Action? Changed;

    /// <summary>Called only after Steam OpenID and Discord OAuth have proved both identities.
    /// Conflicting authenticated links must be explicitly removed before linking again.</summary>
    internal async Task<QuasarPlayerLink> AuthenticateAsync(
        string steamId,
        string discordUserId,
        CancellationToken token = default
    )
    {
        if (!RbacConfigCatalog.IsSteamId64(steamId) || !ulong.TryParse(discordUserId, out var discord) || discord == 0)
            throw new ArgumentException("Valid Steam and Discord identities are required.");
        var link = new QuasarPlayerLink(discordUserId, steamId, _time.GetUtcNow(), Guid.NewGuid().ToString("N"));
        await UpdateAsync(
            links =>
            {
                var conflicts = links.Where(existing => existing.Authenticated &&
                    (existing.DiscordUserId == discordUserId || existing.SteamId == steamId)).ToArray();
                if (conflicts.Any(existing => existing.DiscordUserId != discordUserId || existing.SteamId != steamId))
                    throw new InvalidOperationException("An account is already linked. Remove its existing link before linking another account.");
                if (conflicts.FirstOrDefault() is { } existing)
                {
                    link = existing;
                    return false;
                }
                links.RemoveAll(existing => existing.DiscordUserId == discordUserId || existing.SteamId == steamId);
                links.Add(link);
                return true;
            },
            token
        );
        return link;
    }

    public QuasarPlayerLink? FindByDiscordUser(string discordUserId) =>
        GetLinks().FirstOrDefault(link => link.Authenticated && link.DiscordUserId == discordUserId);

    public QuasarPlayerLink? FindBySteamId(string steamId) =>
        GetLinks().FirstOrDefault(link => link.Authenticated && link.SteamId == steamId);

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
                json = JsonSerializer.Serialize(new LinkFile(links), Json);
            }

            await AtomicFileWriter.WritePrivateTextAsync(_path, json, token);
            lock (_sync)
                _links = JsonSerializer.Deserialize<LinkFile>(json, Json)!.Links;
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
        catch (JsonException error) { throw new InvalidDataException("Player links are damaged. Restore the private player-links.json backup.", error); }

        return _links;
    }

    private sealed record LinkFile(List<QuasarPlayerLink> Links);
}
