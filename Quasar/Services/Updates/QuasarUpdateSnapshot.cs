namespace Quasar.Services.Updates;

public sealed record QuasarUpdateSnapshot
{
    public bool Enabled { get; init; }

    public bool SupportedPlatform { get; init; }

    public string CurrentVersion { get; init; } = string.Empty;

    public string CurrentBootstrapVersion { get; init; } = string.Empty;

    public QuasarUpdateStatus Status { get; init; } = QuasarUpdateStatus.Idle;

    public bool IncludePrerelease { get; init; }

    public bool AutoStageWebUpdates { get; init; } = true;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset? LastCheckedUtc { get; init; }

    public DateTimeOffset? LastChangedUtc { get; init; }

    public QuasarUpdateCandidate? Web { get; init; }

    public IReadOnlyList<QuasarUpdateCandidate> WebReleases { get; init; } = [];

    public string SelectedWebVersion { get; init; } = string.Empty;

    public QuasarUpdateCandidate? Bootstrap { get; init; }

    public bool AppSettingsConflict { get; init; }

    public string AppSettingsConflictPath { get; init; } = string.Empty;

    public string AppSettingsConflictMessage { get; init; } = string.Empty;
}

public sealed record QuasarUpdateCandidate
{
    public string Version { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string ExpectedSha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public string ChecksumDownloadUrl { get; init; } = string.Empty;

    public string? StagedDirectory { get; init; }

    public bool IsAvailable { get; init; }

    public bool IsStaged { get; init; }

    public bool RequiresPrivilegedInstall { get; init; }

    public bool IsPrerelease { get; init; }

    public bool IsCurrent { get; init; }

    public bool IsNewer { get; init; }
}

public enum QuasarUpdateStatus
{
    Idle = 0,
    Checking = 1,
    UpdateQueued = 2,
    Staging = 3,
    Staged = 4,
    Activating = 5,
    Failed = 6,
}
