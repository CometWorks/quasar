using System.Text.Json.Serialization;

namespace Quasar.Services.Analytics;

public sealed class AnalyticsViewConfig
{
    public string SelectedRangeKey { get; set; } = "1h";

    public int AutoRefreshSeconds { get; set; }

    public int GridColumns { get; set; } = 2;

    public int GridRows { get; set; } = 4;

    public int RowHeightPx { get; set; } = 320;

    public List<string> SelectedUniqueNames { get; set; } = [];

    public List<string> SelectedClusterIds { get; set; } = [];

    public DateTimeOffset? CustomFromUtc { get; set; }

    public DateTimeOffset? CustomToUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CustomFromDate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? CustomFromTime { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CustomToDate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? CustomToTime { get; set; }

    public List<AnalyticsPanelConfig> Panels { get; set; } = [];

    internal bool MigrateCustomRange(ViewerTimeZone viewer, DateTime today)
    {
        var hasLegacyFrom = CustomFromDate is not null || CustomFromTime is not null;
        var hasLegacyTo = CustomToDate is not null || CustomToTime is not null;
        if (CustomFromUtc is null && hasLegacyFrom)
            CustomFromUtc = viewer.FromBrowser(
                (CustomFromDate ?? today.AddDays(-1)).Date.Add(CustomFromTime ?? TimeSpan.Zero)
            );
        if (CustomToUtc is null && hasLegacyTo)
            CustomToUtc = viewer.FromBrowser(
                (CustomToDate ?? today).Date.Add(CustomToTime ?? new TimeSpan(23, 59, 0))
            );

        CustomFromUtc = CustomFromUtc?.ToUniversalTime();
        CustomToUtc = CustomToUtc?.ToUniversalTime();
        CustomFromDate = null;
        CustomFromTime = null;
        CustomToDate = null;
        CustomToTime = null;
        return hasLegacyFrom || hasLegacyTo;
    }
}

public sealed class AnalyticsPanelConfig
{
    public string Key { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    public int Order { get; set; }

    public int ColumnSpan { get; set; } = 1;

    public int RowSpan { get; set; } = 1;
}

public sealed class AnalyticsPanelDialogResult
{
    public bool Visible { get; set; } = true;

    public int Order { get; set; }

    public int ColumnSpan { get; set; } = 1;

    public int RowSpan { get; set; } = 1;
}
