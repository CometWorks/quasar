using System.Text.Json.Serialization;

namespace Quasar.Services.Discord;

public sealed class DiscordOptions
{
    public string BotToken { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    public bool Enabled { get; set; }

    public DiscordPresenceOptions Presence { get; set; } = new();

    public List<DiscordServerOptions> Servers { get; set; } = [];

    public DiscordOptions Clone()
    {
        return new DiscordOptions
        {
            BotToken = BotToken,
            GuildId = GuildId,
            Enabled = Enabled,
            Presence = DiscordPresenceOptions.Normalize(Presence),
            Servers = Servers.Select(server => server.Clone()).ToList(),
        };
    }

    public static DiscordOptions Normalize(DiscordOptions? options)
    {
        options ??= new DiscordOptions();

        return new DiscordOptions
        {
            BotToken = options.BotToken?.Trim() ?? string.Empty,
            GuildId = options.GuildId,
            Enabled = options.Enabled,
            Presence = DiscordPresenceOptions.Normalize(options.Presence),
            Servers = (options.Servers ?? [])
                .Select(DiscordServerOptions.Normalize)
                .OrderBy(server => server.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(server => server.CommandPrefix, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}

public sealed class DiscordServerOptions
{
    public bool IsCluster { get; set; }

    [JsonIgnore]
    public string TargetKey => IsCluster ? "cluster:" + UniqueName : UniqueName;

    public string UniqueName { get; set; } = string.Empty;

    public string CommandPrefix { get; set; } = string.Empty;

    public ulong? CommandChannelId { get; set; }

    public ulong? ChatRelayChannelId { get; set; }

    public ulong? StatusChannelId { get; set; }

    public bool EnablePlayerNotifications { get; set; } = true;

    public bool EnableServerNotifications { get; set; } = true;

    public ulong? AdminChannelId { get; set; }

    public List<DiscordFactionChannelOptions> FactionChannels { get; set; } = [];

    public ulong? LogChannelId { get; set; }

    public ulong? AnalyticsChannelId { get; set; }

    public ulong? DeathChannelId { get; set; }

    public ulong? SimSpeedAlertChannelId { get; set; }

    public int LogExportIntervalMinutes { get; set; } = 30;

    public int AnalyticsExportIntervalMinutes { get; set; } = 60;

    public bool EnableChatRelay { get; set; } = true;

    public bool EnableLogExport { get; set; } = true;

    public bool EnableAnalyticsExport { get; set; } = true;

    public bool EnableDeathMessages { get; set; }

    public bool EnableSimSpeedAlerts { get; set; } = true;

    public bool EnableSimSpeedSharpDropAlerts { get; set; } = true;

    public bool EnableSimSpeedSustainedAlerts { get; set; } = true;

    public float SimSpeedSharpDropPreviousMinimum { get; set; } = 0.98f;

    public float SimSpeedSharpDropThreshold { get; set; } = 0.80f;

    public float SimSpeedSharpDropDelta { get; set; } = 0.15f;

    public int SimSpeedSharpDropCooldownSeconds { get; set; } = 120;

    public float SimSpeedSustainedThreshold { get; set; } = 0.90f;

    public int SimSpeedSustainedSeconds { get; set; } = 60;

    public int SimSpeedSustainedCooldownSeconds { get; set; } = 300;

    public string DeathMessageEmotes { get; set; } = "💀,⚔️,🔥,💥,☠️";

    public bool RelayNonCommandMessages { get; set; }

    [JsonIgnore]
    public bool HasCommandBinding => CommandChannelId.HasValue && !string.IsNullOrWhiteSpace(CommandPrefix);

    public DiscordServerOptions Clone()
    {
        return new DiscordServerOptions
        {
            UniqueName = UniqueName,
            IsCluster = IsCluster,
            CommandPrefix = CommandPrefix,
            CommandChannelId = CommandChannelId,
            ChatRelayChannelId = ChatRelayChannelId,
            StatusChannelId = StatusChannelId,
            EnablePlayerNotifications = EnablePlayerNotifications,
            EnableServerNotifications = EnableServerNotifications,
            AdminChannelId = AdminChannelId,
            FactionChannels = FactionChannels.Select(channel => channel.Clone()).ToList(),
            LogChannelId = LogChannelId,
            AnalyticsChannelId = AnalyticsChannelId,
            DeathChannelId = DeathChannelId,
            SimSpeedAlertChannelId = SimSpeedAlertChannelId,
            LogExportIntervalMinutes = LogExportIntervalMinutes,
            AnalyticsExportIntervalMinutes = AnalyticsExportIntervalMinutes,
            EnableChatRelay = EnableChatRelay,
            EnableLogExport = EnableLogExport,
            EnableAnalyticsExport = EnableAnalyticsExport,
            EnableDeathMessages = EnableDeathMessages,
            EnableSimSpeedAlerts = EnableSimSpeedAlerts,
            EnableSimSpeedSharpDropAlerts = EnableSimSpeedSharpDropAlerts,
            EnableSimSpeedSustainedAlerts = EnableSimSpeedSustainedAlerts,
            SimSpeedSharpDropPreviousMinimum = SimSpeedSharpDropPreviousMinimum,
            SimSpeedSharpDropThreshold = SimSpeedSharpDropThreshold,
            SimSpeedSharpDropDelta = SimSpeedSharpDropDelta,
            SimSpeedSharpDropCooldownSeconds = SimSpeedSharpDropCooldownSeconds,
            SimSpeedSustainedThreshold = SimSpeedSustainedThreshold,
            SimSpeedSustainedSeconds = SimSpeedSustainedSeconds,
            SimSpeedSustainedCooldownSeconds = SimSpeedSustainedCooldownSeconds,
            DeathMessageEmotes = DeathMessageEmotes,
            RelayNonCommandMessages = RelayNonCommandMessages,
        };
    }

    public static DiscordServerOptions Normalize(DiscordServerOptions? options)
    {
        options ??= new DiscordServerOptions();

        return new DiscordServerOptions
        {
            UniqueName = options.UniqueName?.Trim() ?? string.Empty,
            IsCluster = options.IsCluster,
            CommandPrefix = options.CommandPrefix?.Trim() ?? string.Empty,
            CommandChannelId = NormalizeChannelId(options.CommandChannelId),
            ChatRelayChannelId = NormalizeChannelId(options.ChatRelayChannelId),
            StatusChannelId = NormalizeChannelId(options.StatusChannelId),
            EnablePlayerNotifications = options.EnablePlayerNotifications,
            EnableServerNotifications = options.EnableServerNotifications,
            AdminChannelId = NormalizeChannelId(options.AdminChannelId),
            FactionChannels = (options.FactionChannels ?? [])
                .Select(DiscordFactionChannelOptions.Normalize)
                .Where(channel => !string.IsNullOrWhiteSpace(channel.FactionTag) && channel.ChannelId > 0)
                .GroupBy(channel => channel.FactionTag, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(channel => channel.FactionTag, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LogChannelId = NormalizeChannelId(options.LogChannelId),
            AnalyticsChannelId = NormalizeChannelId(options.AnalyticsChannelId),
            DeathChannelId = NormalizeChannelId(options.DeathChannelId),
            SimSpeedAlertChannelId = NormalizeChannelId(options.SimSpeedAlertChannelId),
            LogExportIntervalMinutes = options.LogExportIntervalMinutes > 0 ? options.LogExportIntervalMinutes : 30,
            AnalyticsExportIntervalMinutes = options.AnalyticsExportIntervalMinutes > 0 ? options.AnalyticsExportIntervalMinutes : 60,
            EnableChatRelay = options.EnableChatRelay,
            EnableLogExport = options.EnableLogExport,
            EnableAnalyticsExport = options.EnableAnalyticsExport,
            EnableDeathMessages = options.EnableDeathMessages,
            EnableSimSpeedAlerts = options.EnableSimSpeedAlerts,
            EnableSimSpeedSharpDropAlerts = options.EnableSimSpeedSharpDropAlerts,
            EnableSimSpeedSustainedAlerts = options.EnableSimSpeedSustainedAlerts,
            SimSpeedSharpDropPreviousMinimum = NormalizeRatio(options.SimSpeedSharpDropPreviousMinimum, 0.98f),
            SimSpeedSharpDropThreshold = NormalizeRatio(options.SimSpeedSharpDropThreshold, 0.80f),
            SimSpeedSharpDropDelta = NormalizeRatio(options.SimSpeedSharpDropDelta, 0.15f),
            SimSpeedSharpDropCooldownSeconds = NormalizeSeconds(options.SimSpeedSharpDropCooldownSeconds, 120, 0, 86400),
            SimSpeedSustainedThreshold = NormalizeRatio(options.SimSpeedSustainedThreshold, 0.90f),
            SimSpeedSustainedSeconds = NormalizeSeconds(options.SimSpeedSustainedSeconds, 60, 5, 3600),
            SimSpeedSustainedCooldownSeconds = NormalizeSeconds(options.SimSpeedSustainedCooldownSeconds, 300, 0, 86400),
            DeathMessageEmotes = string.IsNullOrWhiteSpace(options.DeathMessageEmotes)
                ? "💀,⚔️,🔥,💥,☠️"
                : options.DeathMessageEmotes.Trim(),
            RelayNonCommandMessages = options.RelayNonCommandMessages,
        };
    }

    private static ulong? NormalizeChannelId(ulong? channelId)
    {
        return channelId is > 0 ? channelId : null;
    }

    private static float NormalizeRatio(float value, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return fallback;

        return Math.Clamp(value, 0f, 2f);
    }

    private static int NormalizeSeconds(int value, int fallback, int minimum, int maximum)
    {
        if (value < minimum)
            value = fallback;

        return Math.Clamp(value, minimum, maximum);
    }
}

public sealed class DiscordFactionChannelOptions
{
    public string FactionTag { get; set; } = string.Empty;

    public ulong ChannelId { get; set; }

    public DiscordFactionChannelOptions Clone() => new()
    {
        FactionTag = FactionTag,
        ChannelId = ChannelId,
    };

    public static DiscordFactionChannelOptions Normalize(DiscordFactionChannelOptions? options)
    {
        options ??= new DiscordFactionChannelOptions();
        return new DiscordFactionChannelOptions
        {
            FactionTag = options.FactionTag?.Trim().ToUpperInvariant() ?? string.Empty,
            ChannelId = options.ChannelId,
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DiscordPresenceStatus>))]
public enum DiscordPresenceStatus { Automatic, Online, Idle, DoNotDisturb, Invisible }
[JsonConverter(typeof(JsonStringEnumConverter<DiscordPresenceActivity>))]
public enum DiscordPresenceActivity { Watching, Playing, Listening, Competing }

public sealed class DiscordPresenceOptions
{
    public bool AllServers { get; set; } = true;
    public List<string> SelectedTargets { get; set; } = [];
    public DiscordPresenceStatus Status { get; set; }
    public DiscordPresenceActivity ActivityType { get; set; }
    public bool ShowActivity { get; set; } = true;
    public bool ShowServerCount { get; set; } = true;
    public bool ShowPlayerCount { get; set; } = true;
    public bool ShowHealth { get; set; } = true;
    public bool ShowServerNames { get; set; }
    public bool ShowServerStates { get; set; }

    public bool Includes(string key) => AllServers || SelectedTargets.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static DiscordPresenceOptions Normalize(DiscordPresenceOptions? value)
    {
        value ??= new();
        return new()
        {
            AllServers = value.AllServers,
            SelectedTargets = (value.SelectedTargets ?? []).Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Status = Enum.IsDefined(value.Status) ? value.Status : DiscordPresenceStatus.Automatic,
            ActivityType = Enum.IsDefined(value.ActivityType) ? value.ActivityType : DiscordPresenceActivity.Watching,
            ShowActivity = value.ShowActivity, ShowServerCount = value.ShowServerCount, ShowPlayerCount = value.ShowPlayerCount,
            ShowHealth = value.ShowHealth, ShowServerNames = value.ShowServerNames, ShowServerStates = value.ShowServerStates,
        };
    }
}
