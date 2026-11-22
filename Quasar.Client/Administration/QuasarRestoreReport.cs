namespace Quasar.Models;

/// <summary>Outcome of a restore attempt, surfaced to the Backup page.</summary>
public sealed class QuasarRestoreReport
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int FilesRestored { get; init; }

    public string? BackupVersion { get; init; }

    public string? RunningVersion { get; init; }

    /// <summary>
    /// True after a successful restore: catalogs reload live, but a full restart
    /// guarantees every in-memory consumer picks up the restored settings.
    /// </summary>
    public bool RestartRecommended { get; init; }

    public static QuasarRestoreReport Failed(string message, string? backupVersion = null, string? runningVersion = null) => new()
    {
        Success = false,
        Message = message,
        BackupVersion = backupVersion,
        RunningVersion = runningVersion,
    };
}
