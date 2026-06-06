namespace Quasar.Services.Updates;

public sealed record QuasarUpdateSnapshot
{
    public bool Enabled { get; init; }

    public bool SupportedPlatform { get; init; }

    public string CurrentVersion { get; init; } = string.Empty;

    public QuasarUpdateStatus Status { get; init; } = QuasarUpdateStatus.Idle;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset? LastCheckedUtc { get; init; }

    public DateTimeOffset? LastChangedUtc { get; init; }

    public QuasarUpdateCandidate? Web { get; init; }

    public QuasarUpdateCandidate? Bootstrap { get; init; }
}

public sealed record QuasarUpdateCandidate
{
    public string Version { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string ExpectedSha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public string? StagedDirectory { get; init; }

    public bool IsAvailable { get; init; }

    public bool IsStaged { get; init; }

    public bool RequiresPrivilegedInstall { get; init; }
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
