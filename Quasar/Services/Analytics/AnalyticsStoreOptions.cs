using Microsoft.Extensions.Configuration;

namespace Quasar.Services.Analytics;

public sealed class AnalyticsStoreOptions
{
    public const int DefaultRetentionDays = 30;
    private static readonly int[] AllowedRetentionDays = [30, 45, 60, 90];

    public int RetentionDays { get; init; } = DefaultRetentionDays;

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
        };
    }

    public static bool IsAllowedRetentionDays(int retentionDays) =>
        Array.IndexOf(AllowedRetentionDays, retentionDays) >= 0;
}
