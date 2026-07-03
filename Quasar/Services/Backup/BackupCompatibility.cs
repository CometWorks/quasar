namespace Quasar.Services.Backup;

/// <summary>Result of checking a backup's version against the running Quasar.</summary>
public readonly record struct BackupCompatibilityResult(bool Allowed, string Reason);

/// <summary>
/// Applies the semantic-versioning rules that govern whether a backup may be
/// restored into the running Quasar:
/// <list type="bullet">
///   <item>Same Major.Minor — always allowed; patch may differ either direction.</item>
///   <item>Older Major.Minor — rejected.</item>
///   <item>Newer Major.Minor — rejected (no cross-Major.Minor downgrade).</item>
/// </list>
/// </summary>
public static class BackupCompatibility
{
    public static BackupCompatibilityResult Evaluate(string? backupVersion, string? runningVersion)
    {
        if (!TryParse(backupVersion, out var backup))
            return new BackupCompatibilityResult(false, $"The backup version '{backupVersion}' is not recognized.");

        if (!TryParse(runningVersion, out var running))
            return new BackupCompatibilityResult(false, $"The running Quasar version '{runningVersion}' is not recognized.");

        var comparison = CompareMajorMinor(backup, running);
        if (comparison == 0)
            return new BackupCompatibilityResult(true, "Same major.minor version — fully compatible.");

        if (comparison > 0)
        {
            return new BackupCompatibilityResult(false,
                $"Cannot restore a backup from a newer Quasar ({backup.Major}.{backup.Minor}) into this older one " +
                $"({running.Major}.{running.Minor}). Downgrading across major.minor versions is not supported.");
        }

        return new BackupCompatibilityResult(false,
            $"Restoring a backup from older Quasar {backup.Major}.{backup.Minor} into {running.Major}.{running.Minor} " +
            "is not supported.");
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
