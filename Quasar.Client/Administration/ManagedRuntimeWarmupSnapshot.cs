namespace Quasar.Services;

public enum ManagedRuntimeWarmupState
{
    Pending = 0,
    Running = 1,
    Complete = 2,
    Failed = 3,
}

public sealed record ManagedRuntimeWarmupSnapshot
{
    public ManagedRuntimeWarmupState State { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<ManagedRuntimeComponentSnapshot> Components { get; init; } = [];

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static ManagedRuntimeWarmupSnapshot CreateInitial() => new()
    {
        State = ManagedRuntimeWarmupState.Pending,
        Message = "Managed runtime warmup pending.",
        Components =
        [
            new ManagedRuntimeComponentSnapshot
            {
                Component = ManagedRuntimeInstallComponent.SteamCmd,
                DisplayName = "SteamCMD",
                State = ManagedRuntimeComponentState.Pending,
                Message = "Waiting to check SteamCMD.",
            },
            new ManagedRuntimeComponentSnapshot
            {
                Component = ManagedRuntimeInstallComponent.Magnetar,
                DisplayName = "Magnetar",
                State = ManagedRuntimeComponentState.Pending,
                Message = "Waiting to check Magnetar runtime.",
            },
            new ManagedRuntimeComponentSnapshot
            {
                Component = ManagedRuntimeInstallComponent.DedicatedServer,
                DisplayName = "Dedicated Server",
                State = ManagedRuntimeComponentState.Pending,
                Message = "Waiting to check Dedicated Server.",
            },
        ],
    };

    public ManagedRuntimeWarmupSnapshot Copy() => this with
    {
        Components = Components.Select(component => component with { }).ToList(),
    };

    public ManagedRuntimeWarmupSnapshot WithComponent(
        ManagedRuntimeInstallComponent component,
        Func<ManagedRuntimeComponentSnapshot, ManagedRuntimeComponentSnapshot> update) =>
        WithComponents(current => current.Component == component ? update(current) : current);

    public ManagedRuntimeWarmupSnapshot WithComponents(
        Func<ManagedRuntimeComponentSnapshot, ManagedRuntimeComponentSnapshot> update) =>
        this with
        {
            Components = Components.Select(update).ToList(),
        };
}

public enum ManagedRuntimeComponentState
{
    Pending = 0,
    Checking = 1,
    Downloading = 2,
    Installing = 3,
    Ready = 4,
    Failed = 5,
}

public sealed record ManagedRuntimeComponentSnapshot
{
    public ManagedRuntimeInstallComponent Component { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public ManagedRuntimeComponentState State { get; init; }

    public string Message { get; init; } = string.Empty;

    public int? Percent { get; init; }

    public string Path { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public DateTimeOffset? LastCheckedUtc { get; init; }
}
