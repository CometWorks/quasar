namespace Quasar.Services.Analytics;

public sealed class AnalyticsViewConfig
{
    public string SelectedRangeKey { get; set; } = "1h";

    public int AutoRefreshSeconds { get; set; }

    public int GridColumns { get; set; } = 2;

    public int GridRows { get; set; } = 4;

    public int RowHeightPx { get; set; } = 320;

    public List<string> SelectedUniqueNames { get; set; } = [];

    public DateTime? CustomFromDate { get; set; } = DateTime.UtcNow.Date.AddDays(-1);

    public TimeSpan? CustomFromTime { get; set; } = TimeSpan.Zero;

    public DateTime? CustomToDate { get; set; } = DateTime.UtcNow.Date;

    public TimeSpan? CustomToTime { get; set; } = new TimeSpan(23, 59, 0);

    public List<AnalyticsPanelConfig> Panels { get; set; } = [];
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
