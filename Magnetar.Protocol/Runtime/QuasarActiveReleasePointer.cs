using System;

namespace Magnetar.Protocol.Runtime;

public sealed class QuasarActiveReleasePointer
{
    public string Version { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public DateTimeOffset ActivatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
