using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Quasar.Models;

namespace Quasar.Services;

public sealed class KnownPlayerCatalog
{
    public const int DefaultRetentionDays = 30;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    private static readonly string[] PromoteLevels = ["None", "Scripter", "Moderator", "SpaceMaster", "Admin"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<KnownPlayerCatalog> _logger;
    private readonly Dictionary<string, KnownPlayerRecord> _players = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _saveDebounce;
    private int _retentionDays = DefaultRetentionDays;

    public KnownPlayerCatalog(ILogger<KnownPlayerCatalog> logger)
    {
        _logger = logger;
        _retentionDays = NormalizeRetentionDays(LoadSettings().RetentionDays);

        foreach (var player in LoadPlayers())
        {
            if (string.IsNullOrWhiteSpace(player.PlayerKey))
                continue;

            _players[player.PlayerKey] = NormalizeRecord(Clone(player));
        }

        if (RemoveExpiredPlayers(DateTimeOffset.UtcNow) > 0)
            _ = SaveAsync(CancellationToken.None);
    }

    public event Action? Changed;

    public int RetentionDays
    {
        get
        {
            lock (_sync)
            {
                return _retentionDays;
            }
        }
    }

    /// <summary>Re-reads the known players from disk, replacing the in-memory set (used after a backup restore).</summary>
    public void ReloadFromDisk()
    {
        var settings = LoadSettings();
        var reloaded = LoadPlayers();
        var removed = 0;
        lock (_sync)
        {
            _retentionDays = NormalizeRetentionDays(settings.RetentionDays);
            _players.Clear();
            foreach (var player in reloaded)
            {
                if (string.IsNullOrWhiteSpace(player.PlayerKey))
                    continue;

                _players[player.PlayerKey] = NormalizeRecord(Clone(player));
            }

            removed = RemoveExpiredPlayers(DateTimeOffset.UtcNow);
        }

        if (removed > 0)
            ScheduleSave();

        Changed?.Invoke();
    }

    public IReadOnlyList<KnownPlayerRecord> GetPlayers()
    {
        var removed = 0;
        List<KnownPlayerRecord> snapshot;
        lock (_sync)
        {
            removed = RemoveExpiredPlayers(DateTimeOffset.UtcNow);
            snapshot = _players.Values
                .Select(Clone)
                .OrderBy(player => player.ServerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(player => player.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(player => player.SteamId)
                .ToList();
        }

        if (removed > 0)
        {
            ScheduleSave();
            Changed?.Invoke();
        }

        return snapshot;
    }

    public async Task<int> SetRetentionDaysAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRetentionDays(retentionDays);
        var settingsChanged = false;
        var removed = 0;

        lock (_sync)
        {
            if (_retentionDays != normalized)
            {
                _retentionDays = normalized;
                settingsChanged = true;
            }

            removed = RemoveExpiredPlayers(DateTimeOffset.UtcNow);
        }

        if (settingsChanged)
            await SaveSettingsAsync(cancellationToken);

        if (removed > 0)
            await SaveAsync(cancellationToken);

        if (settingsChanged || removed > 0)
            Changed?.Invoke();

        return removed;
    }

    public async Task<int> CleanExpiredAsync(CancellationToken cancellationToken = default)
    {
        int removed;
        lock (_sync)
        {
            removed = RemoveExpiredPlayers(DateTimeOffset.UtcNow);
        }

        await SavePlayersIfChangedAsync(removed, cancellationToken);
        return removed;
    }

    public async Task<int> CleanAllAsync(CancellationToken cancellationToken = default)
    {
        int removed;
        lock (_sync)
        {
            removed = _players.Count;
            _players.Clear();
        }

        await SavePlayersIfChangedAsync(removed, cancellationToken);
        return removed;
    }

    public async Task<int> CleanServerAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUniqueName(uniqueName);
        if (string.IsNullOrWhiteSpace(normalized))
            return 0;

        int removed;
        lock (_sync)
        {
            removed = RemovePlayers(player => BelongsToServer(player, normalized));
        }

        await SavePlayersIfChangedAsync(removed, cancellationToken);
        return removed;
    }

    public void ObserveSnapshot(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var observedAt = snapshot.CapturedAtUtc == default
            ? DateTimeOffset.UtcNow
            : snapshot.CapturedAtUtc;

        var changed = false;
        lock (_sync)
        {
            changed |= RemoveExpiredPlayers(DateTimeOffset.UtcNow) > 0;
            changed |= RemoveHiddenPlayers(snapshot);

            foreach (var player in snapshot.Players ?? [])
            {
                if (player is null || player.SteamId <= 0)
                    continue;

                var playerKey = BuildPlayerKey(snapshot.UniqueName, player.SteamId);
                if (!_players.TryGetValue(playerKey, out var record))
                {
                    record = new KnownPlayerRecord
                    {
                        PlayerKey = playerKey,
                        UniqueName = NormalizeUniqueName(snapshot.UniqueName),
                        SteamId = player.SteamId,
                        FirstSeenUtc = observedAt,
                    };
                    _players[playerKey] = record;
                    changed = true;
                }

                changed |= ApplySnapshot(record, snapshot, player, observedAt);
            }
        }

        if (!changed)
            return;

        ScheduleSave();
        Changed?.Invoke();
    }

    public void ApplyCommandOutcome(ServerCommandEnvelope command, ServerCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Success || !command.SteamId.HasValue || command.SteamId.Value <= 0)
            return;

        var changed = false;
        lock (_sync)
        {
            changed |= RemoveExpiredPlayers(DateTimeOffset.UtcNow) > 0;
            var uniqueName = NormalizeUniqueName(command.UniqueName);
            var playerKey = BuildPlayerKey(uniqueName, command.SteamId.Value);
            if (!_players.TryGetValue(playerKey, out var record))
            {
                record = new KnownPlayerRecord
                {
                    PlayerKey = playerKey,
                    UniqueName = uniqueName,
                    ServerId = command.ServerId?.Trim() ?? string.Empty,
                    FirstSeenUtc = result.CompletedAtUtc,
                    LastSeenUtc = result.CompletedAtUtc,
                    SteamId = command.SteamId.Value,
                };
                _players[playerKey] = record;
                changed = true;
            }

            changed |= ApplyCommand(record, command, result.CompletedAtUtc);
        }

        if (!changed)
            return;

        ScheduleSave();
        Changed?.Invoke();
    }

    private async Task SavePlayersIfChangedAsync(int removed, CancellationToken cancellationToken)
    {
        if (removed <= 0)
            return;

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    private KnownPlayerSettings LoadSettings()
    {
        var path = MagnetarPaths.GetQuasarKnownPlayerSettingsPath();
        if (!File.Exists(path))
            return new KnownPlayerSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<KnownPlayerSettings>(json, JsonOptions) ?? new KnownPlayerSettings();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load known player settings from {Path}", path);
            return new KnownPlayerSettings();
        }
    }

    private List<KnownPlayerRecord> LoadPlayers()
    {
        var path = MagnetarPaths.GetQuasarKnownPlayersPath();
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<KnownPlayerRecord>>(json, JsonOptions) ?? [];
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load known players from {Path}", path);
            return [];
        }
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = new KnownPlayerSettings
        {
            RetentionDays = RetentionDays,
        };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(MagnetarPaths.GetQuasarKnownPlayerSettingsPath(), json, cancellationToken);
    }

    private static bool ApplySnapshot(
        KnownPlayerRecord record,
        AgentSnapshot snapshot,
        PlayerSnapshot player,
        DateTimeOffset observedAt)
    {
        var changed = false;

        changed |= Assign(record.UniqueName, NormalizeUniqueName(snapshot.UniqueName), value => record.UniqueName = value);
        changed |= Assign(record.ServerId, snapshot.ServerId?.Trim() ?? string.Empty, value => record.ServerId = value);
        changed |= Assign(record.ServerName, snapshot.ServerName?.Trim() ?? string.Empty, value => record.ServerName = value);
        changed |= Assign(record.WorldName, snapshot.WorldName?.Trim() ?? string.Empty, value => record.WorldName = value);
        changed |= Assign(record.HostId, snapshot.HostId?.Trim() ?? string.Empty, value => record.HostId = value);
        changed |= Assign(record.HostName, snapshot.HostName?.Trim() ?? string.Empty, value => record.HostName = value);

        changed |= Assign(record.IdentityId, player.IdentityId, value => record.IdentityId = value);
        changed |= Assign(record.SerialId, player.SerialId, value => record.SerialId = value);
        changed |= Assign(record.DisplayName, TextSanitizer.CleanGameText(player.DisplayName), value => record.DisplayName = value);
        changed |= Assign(record.PlatformDisplayName, TextSanitizer.CleanGameText(player.PlatformDisplayName), value => record.PlatformDisplayName = value);
        changed |= Assign(record.PlatformIcon, player.PlatformIcon?.Trim() ?? string.Empty, value => record.PlatformIcon = value);
        changed |= Assign(record.GameAcronym, player.GameAcronym?.Trim() ?? string.Empty, value => record.GameAcronym = value);
        changed |= Assign(record.ServiceName, player.ServiceName?.Trim() ?? string.Empty, value => record.ServiceName = value);
        changed |= Assign(record.FactionTag, player.FactionTag?.Trim() ?? string.Empty, value => record.FactionTag = value);
        changed |= Assign(record.PromoteLevel, player.PromoteLevel?.Trim() ?? string.Empty, value => record.PromoteLevel = value);
        changed |= Assign(record.IsAdmin, player.IsAdmin, value => record.IsAdmin = value);
        changed |= Assign(record.IsBanned, false, value => record.IsBanned = value);
        changed |= Assign(record.LastObservedPingMs, player.PingMs, value => record.LastObservedPingMs = value);

        if (record.FirstSeenUtc == default)
        {
            record.FirstSeenUtc = observedAt;
            changed = true;
        }

        if (ShouldAdvanceLastSeen(record.LastSeenUtc, observedAt))
        {
            record.LastSeenUtc = observedAt;
            record.LastOnlineUtc = observedAt;
            changed = true;
        }
        else if (!record.LastOnlineUtc.HasValue)
        {
            record.LastOnlineUtc = observedAt;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyCommand(KnownPlayerRecord record, ServerCommandEnvelope command, DateTimeOffset completedAtUtc)
    {
        var changed = false;

        if (completedAtUtc != default && ShouldAdvanceLastSeen(record.LastSeenUtc, completedAtUtc))
        {
            record.LastSeenUtc = completedAtUtc;
            changed = true;
        }

        switch (command.CommandType)
        {
            case ServerCommandType.BanPlayer:
                changed |= Assign(record.IsBanned, true, value => record.IsBanned = value);
                break;

            case ServerCommandType.UnbanPlayer:
                changed |= Assign(record.IsBanned, false, value => record.IsBanned = value);
                break;

            case ServerCommandType.PromotePlayer:
                var promotedLevel = GetAdjacentPromoteLevel(record.PromoteLevel, 1);
                changed |= Assign(record.PromoteLevel, promotedLevel, value => record.PromoteLevel = value);
                changed |= Assign(record.IsAdmin, IsAdminLevel(promotedLevel), value => record.IsAdmin = value);
                break;

            case ServerCommandType.DemotePlayer:
                var demotedLevel = GetAdjacentPromoteLevel(record.PromoteLevel, -1);
                changed |= Assign(record.PromoteLevel, demotedLevel, value => record.PromoteLevel = value);
                changed |= Assign(record.IsAdmin, IsAdminLevel(demotedLevel), value => record.IsAdmin = value);
                break;

            case ServerCommandType.SetPlayerPromoteLevel:
                var targetLevel = NormalizePromoteLevel(command.Text);
                changed |= Assign(record.PromoteLevel, targetLevel, value => record.PromoteLevel = value);
                changed |= Assign(record.IsAdmin, IsAdminLevel(targetLevel), value => record.IsAdmin = value);
                break;
        }

        return changed;
    }

    private bool RemoveHiddenPlayers(AgentSnapshot snapshot)
    {
        var hiddenSteamIds = snapshot.HiddenPlayerSteamIds?
            .Where(id => id > 0)
            .ToHashSet() ?? [];
        var hiddenIdentityIds = snapshot.HiddenPlayerIdentityIds?
            .Where(id => id != 0)
            .ToHashSet() ?? [];

        if (hiddenSteamIds.Count == 0 && hiddenIdentityIds.Count == 0)
            return false;

        var uniqueName = NormalizeUniqueName(snapshot.UniqueName);
        var removedKeys = _players
            .Where(pair => BelongsToServer(pair.Value, uniqueName) &&
                           (hiddenSteamIds.Contains(pair.Value.SteamId) ||
                            hiddenIdentityIds.Contains(pair.Value.IdentityId)))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in removedKeys)
            _players.Remove(key);

        return removedKeys.Count > 0;
    }

    private int RemoveExpiredPlayers(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_retentionDays);
        return RemovePlayers(player =>
        {
            var lastSeen = GetRetentionTimestamp(player);
            return lastSeen != default && lastSeen < cutoff;
        });
    }

    private int RemovePlayers(Func<KnownPlayerRecord, bool> predicate)
    {
        var removedKeys = _players
            .Where(pair => predicate(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in removedKeys)
            _players.Remove(key);

        return removedKeys.Count;
    }

    private static DateTimeOffset GetRetentionTimestamp(KnownPlayerRecord player)
    {
        if (player.LastSeenUtc != default)
            return player.LastSeenUtc;

        if (player.LastOnlineUtc is { } lastOnlineUtc && lastOnlineUtc != default)
            return lastOnlineUtc;

        return player.FirstSeenUtc;
    }

    private static string GetAdjacentPromoteLevel(string currentLevel, int direction)
    {
        var normalized = NormalizePromoteLevel(currentLevel);
        var index = Array.FindIndex(PromoteLevels, level => string.Equals(level, normalized, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = 0;

        return PromoteLevels[Math.Clamp(index + direction, 0, PromoteLevels.Length - 1)];
    }

    private static string NormalizePromoteLevel(string? level)
    {
        var normalized = PromoteLevels.FirstOrDefault(candidate => string.Equals(candidate, level?.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized ?? "None";
    }

    private static bool IsAdminLevel(string level) =>
        string.Equals(level, "Admin", StringComparison.OrdinalIgnoreCase);

    private void ScheduleSave()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _saveDebounce?.Cancel();
            _saveDebounce?.Dispose();
            _saveDebounce = new CancellationTokenSource();
            debounce = _saveDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), debounce.Token);
                await SaveAsync(debounce.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        List<KnownPlayerRecord> snapshot;
        lock (_sync)
        {
            snapshot = _players.Values
                .Select(Clone)
                .OrderBy(player => player.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(player => player.SteamId)
                .ToList();
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(MagnetarPaths.GetQuasarKnownPlayersPath(), json, cancellationToken);
    }

    private static string NormalizeUniqueName(string? uniqueName) =>
        uniqueName?.Trim() ?? string.Empty;

    private static string BuildPlayerKey(string? uniqueName, long steamId) =>
        $"{NormalizeUniqueName(uniqueName)}::{steamId}";

    private static bool BelongsToServer(KnownPlayerRecord player, string uniqueName)
    {
        if (string.Equals(NormalizeUniqueName(player.UniqueName), uniqueName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(uniqueName))
            return false;

        return player.PlayerKey.StartsWith($"{uniqueName}::", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAdvanceLastSeen(DateTimeOffset previous, DateTimeOffset current)
    {
        if (previous == default)
            return true;

        return current >= previous.AddMinutes(1);
    }

    private static bool Assign<T>(T current, T value, Action<T> assign)
    {
        if (typeof(T) == typeof(string) && value is null)
            value = (T)(object)string.Empty;

        if (EqualityComparer<T>.Default.Equals(current, value))
            return false;

        assign(value);
        return true;
    }

    private static KnownPlayerRecord Clone(KnownPlayerRecord player)
    {
        return new KnownPlayerRecord
        {
            PlayerKey = player.PlayerKey,
            UniqueName = player.UniqueName,
            ServerId = player.ServerId,
            ServerName = player.ServerName,
            WorldName = player.WorldName,
            HostId = player.HostId,
            HostName = player.HostName,
            SteamId = player.SteamId,
            IdentityId = player.IdentityId,
            SerialId = player.SerialId,
            DisplayName = TextSanitizer.CleanGameText(player.DisplayName),
            PlatformDisplayName = TextSanitizer.CleanGameText(player.PlatformDisplayName),
            PlatformIcon = player.PlatformIcon,
            GameAcronym = player.GameAcronym,
            ServiceName = player.ServiceName,
            FactionTag = player.FactionTag,
            PromoteLevel = player.PromoteLevel,
            IsAdmin = player.IsAdmin,
            IsBanned = player.IsBanned,
            LastObservedPingMs = player.LastObservedPingMs,
            FirstSeenUtc = player.FirstSeenUtc,
            LastSeenUtc = player.LastSeenUtc,
            LastOnlineUtc = player.LastOnlineUtc,
        };
    }

    private static KnownPlayerRecord NormalizeRecord(KnownPlayerRecord player)
    {
        player.DisplayName = TextSanitizer.CleanGameText(player.DisplayName);
        player.PlatformDisplayName = TextSanitizer.CleanGameText(player.PlatformDisplayName);
        return player;
    }

    private static int NormalizeRetentionDays(int retentionDays) =>
        Math.Clamp(retentionDays, MinRetentionDays, MaxRetentionDays);

    private sealed class KnownPlayerSettings
    {
        public int RetentionDays { get; set; } = DefaultRetentionDays;
    }
}
