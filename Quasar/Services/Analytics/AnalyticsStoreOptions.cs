using Microsoft.Extensions.Configuration;

namespace Quasar.Services.Analytics;

public sealed class AnalyticsStoreOptions
{
    public const int DefaultRetentionDays = 30;
    private static readonly int[] AllowedRetentionDays = [30, 45, 60, 90];

    public const int DefaultPluginStatsRawRetentionHours = 48;
    public const int DefaultPluginStatsMinuteRetentionDays = 7;

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    // Plugin statistics history keeps raw rows this long, then 1-minute rollups, then 1-hour
    // rollups until RetentionDays.
    public int PluginStatsRawRetentionHours { get; init; } = DefaultPluginStatsRawRetentionHours;

    public int PluginStatsMinuteRetentionDays { get; init; } = DefaultPluginStatsMinuteRetentionDays;

    // Compaction cap of one provider's history file. Only tests lower it.
    public long PluginStatsMaxFileBytes { get; init; } = 32 * 1024 * 1024;

    public int RawCapacity => 3600;

    public int OneMinuteCapacity => RetentionDays * 24 * 60;

    public int OneHourCapacity => RetentionDays * 24;

    public static AnalyticsStoreOptions Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("Quasar");
        var explicitValue = Environment.GetEnvironmentVariable("QUASAR_ANALYTICS_RETENTION_DAYS")
                            ?? section["AnalyticsRetentionDays"]
                            ?? section.GetSection("Analytics")["RetentionDays"];

        if (!int.TryParse(explicitValue, out var parsedRetentionDays))
            parsedRetentionDays = DefaultRetentionDays;

        if (!IsAllowedRetentionDays(parsedRetentionDays))
            parsedRetentionDays = DefaultRetentionDays;

        return new AnalyticsStoreOptions
        {
            RetentionDays = parsedRetentionDays,
            PluginStatsRawRetentionHours = ReadPositive(section,
                "QUASAR_PLUGIN_STATS_RAW_RETENTION_HOURS", "PluginStatsRawRetentionHours", DefaultPluginStatsRawRetentionHours),
            PluginStatsMinuteRetentionDays = ReadPositive(section,
                "QUASAR_PLUGIN_STATS_MINUTE_RETENTION_DAYS", "PluginStatsMinuteRetentionDays", DefaultPluginStatsMinuteRetentionDays),
        };
    }

    private static int ReadPositive(IConfigurationSection quasar, string variable, string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable) ?? quasar.GetSection("Analytics")[key], out var value) && value > 0
            ? value
            : fallback;

    public static bool IsAllowedRetentionDays(int retentionDays) =>
        Array.IndexOf(AllowedRetentionDays, retentionDays) >= 0;
}
