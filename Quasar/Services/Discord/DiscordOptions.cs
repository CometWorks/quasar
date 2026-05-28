using System.Text.Json.Serialization;

namespace Quasar.Services.Discord;

public sealed class DiscordOptions
{
    public string BotToken { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    public bool Enabled { get; set; }

    public List<DiscordInstanceOptions> Instances { get; set; } = [];

    public DiscordOptions Clone()
    {
        return new DiscordOptions
        {
            BotToken = BotToken,
            GuildId = GuildId,
            Enabled = Enabled,
            Instances = Instances.Select(instance => instance.Clone()).ToList(),
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
            Instances = (options.Instances ?? [])
                .Select(DiscordInstanceOptions.Normalize)
                .OrderBy(instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.CommandPrefix, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}

public sealed class DiscordInstanceOptions
{
    public string InstanceId { get; set; } = string.Empty;

    public string CommandPrefix { get; set; } = string.Empty;

    public ulong? CommandChannelId { get; set; }

    public ulong? ChatRelayChannelId { get; set; }

    public ulong? LogChannelId { get; set; }

    public ulong? AnalyticsChannelId { get; set; }

    public ulong? DeathChannelId { get; set; }

    public int LogExportIntervalMinutes { get; set; } = 30;

    public int AnalyticsExportIntervalMinutes { get; set; } = 60;

    public bool EnableChatRelay { get; set; } = true;

    public bool EnableLogExport { get; set; } = true;

    public bool EnableAnalyticsExport { get; set; } = true;

    public bool EnableDeathMessages { get; set; }

    public string DeathMessageEmotes { get; set; } = "💀,⚔️,🔥,💥,☠️";

    public bool RelayNonCommandMessages { get; set; }

    [JsonIgnore]
    public bool HasCommandBinding => CommandChannelId.HasValue && !string.IsNullOrWhiteSpace(CommandPrefix);

    public DiscordInstanceOptions Clone()
    {
        return new DiscordInstanceOptions
        {
            InstanceId = InstanceId,
            CommandPrefix = CommandPrefix,
            CommandChannelId = CommandChannelId,
            ChatRelayChannelId = ChatRelayChannelId,
            LogChannelId = LogChannelId,
            AnalyticsChannelId = AnalyticsChannelId,
            DeathChannelId = DeathChannelId,
            LogExportIntervalMinutes = LogExportIntervalMinutes,
            AnalyticsExportIntervalMinutes = AnalyticsExportIntervalMinutes,
            EnableChatRelay = EnableChatRelay,
            EnableLogExport = EnableLogExport,
            EnableAnalyticsExport = EnableAnalyticsExport,
            EnableDeathMessages = EnableDeathMessages,
            DeathMessageEmotes = DeathMessageEmotes,
            RelayNonCommandMessages = RelayNonCommandMessages,
        };
    }

    public static DiscordInstanceOptions Normalize(DiscordInstanceOptions? options)
    {
        options ??= new DiscordInstanceOptions();

        return new DiscordInstanceOptions
        {
            InstanceId = options.InstanceId?.Trim() ?? string.Empty,
            CommandPrefix = options.CommandPrefix?.Trim() ?? string.Empty,
            CommandChannelId = NormalizeChannelId(options.CommandChannelId),
            ChatRelayChannelId = NormalizeChannelId(options.ChatRelayChannelId),
            LogChannelId = NormalizeChannelId(options.LogChannelId),
            AnalyticsChannelId = NormalizeChannelId(options.AnalyticsChannelId),
            DeathChannelId = NormalizeChannelId(options.DeathChannelId),
            LogExportIntervalMinutes = options.LogExportIntervalMinutes > 0 ? options.LogExportIntervalMinutes : 30,
            AnalyticsExportIntervalMinutes = options.AnalyticsExportIntervalMinutes > 0 ? options.AnalyticsExportIntervalMinutes : 60,
            EnableChatRelay = options.EnableChatRelay,
            EnableLogExport = options.EnableLogExport,
            EnableAnalyticsExport = options.EnableAnalyticsExport,
            EnableDeathMessages = options.EnableDeathMessages,
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
}
