namespace Quasar.Services.Discord;

public sealed record DiscordTarget(string UniqueName, string Name, string DisplayName, bool IsCluster);
public sealed record DiscordDashboardState(bool CanManage, DiscordTarget[] Targets, string Settings, string? TemplatePath, DeathMessagesConfig? Templates, DiscordBotStatusSnapshot BotStatus);
public sealed record DiscordSettingsSave(string Settings, string? BotToken, bool ClearToken);
public sealed record DiscordPresencePreview(string Status, string? Activity, string ActivityType);
public sealed record DiscordLogExcerpt(string Path, bool Missing, bool Truncated, string Text);

public sealed class DiscordBotStatusSnapshot
{
    public bool Enabled { get; init; }

    public bool TokenConfigured { get; init; }

    public bool GuildConfigured { get; init; }

    public bool IsRunning { get; init; }

    public string StateText { get; init; } = string.Empty;

    public string LastError { get; init; } = string.Empty;
}
