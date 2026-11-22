using Quasar.Models;

namespace Quasar.Services.Backup;

public enum WorldBackupAction
{
    LatestSave,
    SaveAndBackUp,
    StopAndBackUp,
}

/// <summary>A backup ZIP found in the configured backup directory.</summary>
public sealed record QuasarBackupFileInfo(
    string Name,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    QuasarBackupKind Kind,
    bool Automatic,
    string? ServerUniqueName,
    string? ServerDisplayName);
