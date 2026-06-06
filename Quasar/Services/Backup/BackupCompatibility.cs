namespace Quasar.Services.Backup;

/// <summary>Result of checking a backup's version against the running Quasar.</summary>
public readonly record struct BackupCompatibilityResult(bool Allowed, bool MigrationRequired, string Reason);

/// <summary>
/// Applies the semantic-versioning rules that govern whether a backup may be
/// restored into the running Quasar:
/// <list type="bullet">
///   <item>Same Major.Minor — always allowed; patch may differ either direction.</item>
///   <item>Older Major.Minor — allowed only if a forward migration path exists
///         (see <see cref="BackupFormatMigrations"/>); otherwise rejected.</item>
///   <item>Newer Major.Minor — rejected (no cross-Major.Minor downgrade).</item>
/// </list>
/// </summary>
public static class BackupCompatibility
{
    public static BackupCompatibilityResult Evaluate(string? backupVersion, string? runningVersion)
    {
        if (!TryParse(backupVersion, out var backup))
            return new BackupCompatibilityResult(false, false, $"The backup version '{backupVersion}' is not recognized.");

        if (!TryParse(runningVersion, out var running))
            return new BackupCompatibilityResult(false, false, $"The running Quasar version '{runningVersion}' is not recognized.");

        var comparison = CompareMajorMinor(backup, running);
        if (comparison == 0)
            return new BackupCompatibilityResult(true, false, "Same major.minor version — fully compatible.");

        if (comparison > 0)
        {
            return new BackupCompatibilityResult(false, false,
                $"Cannot restore a backup from a newer Quasar ({backup.Major}.{backup.Minor}) into this older one " +
                $"({running.Major}.{running.Minor}). Downgrading across major.minor versions is not supported.");
        }

        // Backup is from an older major.minor — a forward migration path is required.
        if (BackupFormatMigrations.CanMigrate(backup, running))
            return new BackupCompatibilityResult(true, true, "An upgrade path is available for this older backup.");

        return new BackupCompatibilityResult(false, false,
            $"Restoring a backup from {backup.Major}.{backup.Minor} into {running.Major}.{running.Minor} requires a " +
            "settings upgrade that is not available yet.");
    }

    /// <summary>Compares two versions by Major then Minor only (patch is ignored).</summary>
    public static int CompareMajorMinor(Version a, Version b)
    {
        if (a.Major != b.Major)
            return a.Major.CompareTo(b.Major);

        return a.Minor.CompareTo(b.Minor);
    }

    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Version.TryParse(value.Trim(), out version!);
    }
}
