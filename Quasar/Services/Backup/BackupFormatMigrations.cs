namespace Quasar.Services.Backup;

/// <summary>
/// Registry of forward upgrade steps that migrate backup contents from one
/// major.minor release to the next. Restores across an incompatible
/// major.minor boundary chain these steps from the backup's version up to the
/// running version.
/// <para>
/// No migrations exist yet — today's backups carry the same data structures as
/// the code that reads them, so only same-major.minor restores are accepted.
/// When a future release changes a persisted structure, add a
/// <see cref="BackupMigrationStep"/> from the last minor release to the new one
/// here, and teach <see cref="QuasarBackupService"/> to apply the chain while
/// extracting.
/// </para>
/// </summary>
public static class BackupFormatMigrations
{
    /// <summary>A single hop that upgrades backup data from <see cref="From"/> to <see cref="To"/> (major.minor).</summary>
    public sealed record BackupMigrationStep(Version From, Version To);

    /// <summary>Ordered, contiguous upgrade steps. Empty until the first breaking change ships.</summary>
    public static IReadOnlyList<BackupMigrationStep> Steps { get; } = Array.Empty<BackupMigrationStep>();

    /// <summary>
    /// True when a contiguous chain of <see cref="Steps"/> upgrades a backup taken at
    /// <paramref name="backupVersion"/> up to <paramref name="runningVersion"/> (compared by major.minor).
    /// </summary>
    public static bool CanMigrate(Version backupVersion, Version runningVersion)
    {
        var current = new Version(backupVersion.Major, backupVersion.Minor);
        var target = new Version(runningVersion.Major, runningVersion.Minor);

        // Walk the registered steps, advancing as long as one continues the chain.
        var advanced = true;
        while (advanced && BackupCompatibility.CompareMajorMinor(current, target) < 0)
        {
            advanced = false;
            foreach (var step in Steps)
            {
                if (BackupCompatibility.CompareMajorMinor(step.From, current) == 0)
                {
                    current = new Version(step.To.Major, step.To.Minor);
                    advanced = true;
                    break;
                }
            }
        }

        return BackupCompatibility.CompareMajorMinor(current, target) == 0;
    }
}
