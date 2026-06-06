namespace Quasar.Models;

/// <summary>
/// Metadata written to <c>quasar-backup.json</c> at the root of every backup ZIP.
/// <see cref="FormatVersion"/> versions the archive layout itself; <see cref="QuasarVersion"/>
/// records the Quasar build the backup was taken from so restore can apply the
/// semantic-version compatibility rules.
/// </summary>
public sealed class QuasarBackupManifest
{
    /// <summary>Layout/format version of the backup archive (not the Quasar version).</summary>
    public int FormatVersion { get; set; }

    /// <summary>Quasar version (Major.Minor.Build[.Revision]) the backup was saved from.</summary>
    public string QuasarVersion { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? CreatedByHost { get; set; }
}
