using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Quasar.Models;

namespace Quasar.Services;

public sealed class KnownPlayerCatalog
{
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

    public KnownPlayerCatalog(ILogger<KnownPlayerCatalog> logger)
    {
        _logger = logger;

        foreach (var player in LoadPlayers())
        {
            if (string.IsNullOrWhiteSpace(player.PlayerKey))
                continue;

            _players[player.PlayerKey] = NormalizeRecord(Clone(player));
        }
    }

    public event Action? Changed;

    public IReadOnlyList<KnownPlayerRecord> GetPlayers()
    {
        lock (_sync)
        {
            return _players.Values
                .Select(Clone)
                .OrderBy(player => player.ServerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(player => player.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(player => player.SteamId)
                .ToList();
        }
    }

    public void ObserveSnapshot(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Players is null || snapshot.Players.Count == 0)
            return;

        var observedAt = snapshot.CapturedAtUtc == default
            ? DateTimeOffset.UtcNow
            : snapshot.CapturedAtUtc;

        var changed = false;
        lock (_sync)
        {
            foreach (var player in snapshot.Players)
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
        changed |= Assign(record.NodeId, snapshot.NodeId?.Trim() ?? string.Empty, value => record.NodeId = value);
        changed |= Assign(record.NodeName, snapshot.NodeName?.Trim() ?? string.Empty, value => record.NodeName = value);

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
            NodeId = player.NodeId,
            NodeName = player.NodeName,
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
}
