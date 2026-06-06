namespace Quasar.Models;

/// <summary>How often automatic configuration backups are taken.</summary>
public enum BackupFrequency
{
    Hourly,
    Daily,
    Weekly,
}

/// <summary>
/// Persistent configuration for the automatic backup scheduler, serialized to
/// <c>backup-settings.json</c> in the Quasar data directory. A simple
/// preset-based schedule (frequency + time-of-day) keeps the UI approachable;
/// retention is expressed as the number of most-recent backups to keep.
/// </summary>
public sealed class QuasarBackupSettings
{
    public const int MinRetentionCount = 1;
    public const int MaxRetentionCount = 1000;
    public const int DefaultRetentionCount = 10;

    /// <summary>Whether the scheduler writes automatic backups at all.</summary>
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

    public QuasarBackupSettings Clone()
    {
        return new QuasarBackupSettings
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
    public static QuasarBackupSettings Normalize(QuasarBackupSettings? settings)
    {
        settings ??= new QuasarBackupSettings();

        var retention = settings.RetentionCount;
        if (retention < MinRetentionCount)
            retention = MinRetentionCount;
        else if (retention > MaxRetentionCount)
            retention = MaxRetentionCount;

        return new QuasarBackupSettings
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
