namespace Quasar.Models;

/// <summary>How often automatic backups are taken.</summary>
public enum BackupFrequency
{
    Hourly,
    Daily,
    Weekly,
}

/// <summary>
/// One automatic-backup rule for a single backup scope.
/// </summary>
public sealed class QuasarBackupRuleSettings
{
    public const int MinRetentionCount = 1;
    public const int MaxRetentionCount = 1000;
    public const int DefaultRetentionCount = 10;

    /// <summary>Whether this rule writes automatic backups.</summary>
    public bool Enabled { get; set; }

    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

    /// <summary>Time-of-day (local) for Daily/Weekly backups. Ignored for Hourly.</summary>
    public TimeOnly TimeOfDay { get; set; } = new(3, 0);

    /// <summary>Day-of-week for Weekly backups. Ignored for Hourly/Daily.</summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>How many of the most-recent automatic backups to retain.</summary>
    public int RetentionCount { get; set; } = DefaultRetentionCount;

    /// <summary>Timestamp of the last automatic backup, used to compute the next due time.</summary>
    public DateTimeOffset? LastBackupUtc { get; set; }

    public QuasarBackupRuleSettings Clone()
    {
        return new QuasarBackupRuleSettings
        {
            Enabled = Enabled,
            Frequency = Frequency,
            TimeOfDay = TimeOfDay,
            DayOfWeek = DayOfWeek,
            RetentionCount = RetentionCount,
            LastBackupUtc = LastBackupUtc,
        };
    }

    /// <summary>Returns a copy with values clamped to their valid ranges.</summary>
    public static QuasarBackupRuleSettings Normalize(QuasarBackupRuleSettings? settings)
    {
        settings ??= new QuasarBackupRuleSettings();

        var retention = settings.RetentionCount;
        if (retention < MinRetentionCount)
            retention = MinRetentionCount;
        else if (retention > MaxRetentionCount)
            retention = MaxRetentionCount;

        return new QuasarBackupRuleSettings
        {
            Enabled = settings.Enabled,
            Frequency = settings.Frequency,
            TimeOfDay = settings.TimeOfDay,
            DayOfWeek = settings.DayOfWeek,
            RetentionCount = retention,
            LastBackupUtc = settings.LastBackupUtc,
        };
    }
}

/// <summary>
/// Persistent configuration for the automatic backup scheduler, serialized to
/// <c>backup-settings.json</c> in the Quasar data directory. Each backup scope
/// has its own rule.
/// </summary>
public sealed class QuasarBackupSettings
{
    public const int MinRetentionCount = QuasarBackupRuleSettings.MinRetentionCount;
    public const int MaxRetentionCount = QuasarBackupRuleSettings.MaxRetentionCount;
    public const int DefaultRetentionCount = QuasarBackupRuleSettings.DefaultRetentionCount;

    public QuasarBackupRuleSettings Configuration { get; set; } = new();

    public QuasarBackupRuleSettings Server { get; set; } = new();

    public QuasarBackupRuleSettings World { get; set; } = new();

    // Legacy flat settings kept for reading old backup-settings.json files.
    public bool? Enabled { get; set; }

    public BackupFrequency? Frequency { get; set; }

    public TimeOnly? TimeOfDay { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }

    public int? RetentionCount { get; set; }

    public DateTimeOffset? LastBackupUtc { get; set; }

    public QuasarBackupSettings Clone()
    {
        return new QuasarBackupSettings
        {
            Configuration = Configuration.Clone(),
            Server = Server.Clone(),
            World = World.Clone(),
        };
    }

    /// <summary>Returns a copy with values clamped to their valid ranges.</summary>
    public static QuasarBackupSettings Normalize(QuasarBackupSettings? settings)
    {
        settings ??= new QuasarBackupSettings();

        var useLegacyConfiguration =
            settings.Configuration is null ||
            settings.Enabled.HasValue ||
            settings.LastBackupUtc is not null ||
            settings.Frequency.HasValue ||
            settings.TimeOfDay.HasValue ||
            settings.DayOfWeek.HasValue ||
            settings.RetentionCount.HasValue;

        var configuration = useLegacyConfiguration
            ? QuasarBackupRuleSettings.Normalize(new QuasarBackupRuleSettings
            {
                Enabled = settings.Enabled ?? false,
                Frequency = settings.Frequency ?? BackupFrequency.Daily,
                TimeOfDay = settings.TimeOfDay ?? new TimeOnly(3, 0),
                DayOfWeek = settings.DayOfWeek ?? System.DayOfWeek.Sunday,
                RetentionCount = settings.RetentionCount ?? DefaultRetentionCount,
                LastBackupUtc = settings.LastBackupUtc,
            })
            : QuasarBackupRuleSettings.Normalize(settings.Configuration);

        return new QuasarBackupSettings
        {
            Configuration = configuration,
            Server = QuasarBackupRuleSettings.Normalize(settings.Server),
            World = QuasarBackupRuleSettings.Normalize(settings.World),
        };
    }

    public QuasarBackupRuleSettings GetRule(QuasarBackupKind kind) =>
        kind switch
        {
            QuasarBackupKind.Server => Server,
            QuasarBackupKind.World => World,
            _ => Configuration,
        };
}
