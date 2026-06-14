using Quasar.Models;

namespace Quasar.Services;

public sealed class ManagedRuntimeWarmupService : BackgroundService
{
    private static readonly TimeSpan MagnetarUpdateCheckInterval = TimeSpan.FromMinutes(15);
    private readonly ManagedDedicatedServerRuntimeResolver _runtimeResolver;
    private readonly ILogger<ManagedRuntimeWarmupService> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private ManagedRuntimeWarmupSnapshot _snapshot = ManagedRuntimeWarmupSnapshot.CreateInitial();

    public ManagedRuntimeWarmupService(
        ManagedDedicatedServerRuntimeResolver runtimeResolver,
        ILogger<ManagedRuntimeWarmupService> logger)
    {
        _runtimeResolver = runtimeResolver;
        _logger = logger;
    }

    public event Action? Changed;

    public ManagedRuntimeWarmupSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot.Copy();
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.State == ManagedRuntimeWarmupState.Complete;
            }
        }
    }

    public string BlockLaunchMessage
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.State == ManagedRuntimeWarmupState.Failed
                    ? _snapshot.Message
                    : "Managed SteamCMD and Dedicated Server runtime are still preparing.";
            }
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default) =>
        RunWarmupAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunWarmupAsync(stoppingToken);

        using var timer = new PeriodicTimer(MagnetarUpdateCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunMagnetarUpdateCheckAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunWarmupAsync(CancellationToken stoppingToken)
    {
        if (!await _runLock.WaitAsync(0, stoppingToken))
            return;

        try
        {
            SetState(ManagedRuntimeWarmupState.Running, "Preparing managed SteamCMD and Dedicated Server runtime.");

            var progress = new Progress<ManagedRuntimeInstallProgress>(ApplyProgress);
            var readiness = await _runtimeResolver.EnsureManagedRuntimeReadyAsync(progress, stoppingToken);
            if (!readiness.IsReady)
            {
                SetState(ManagedRuntimeWarmupState.Failed, readiness.FailureMessage);
                return;
            }

            SetState(ManagedRuntimeWarmupState.Complete, "Managed SteamCMD and Dedicated Server runtime ready.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Managed runtime warmup failed.");
            SetState(ManagedRuntimeWarmupState.Failed, exception.Message);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task RunMagnetarUpdateCheckAsync(CancellationToken stoppingToken)
    {
        if (!await _runLock.WaitAsync(0, stoppingToken))
            return;

        try
        {
            await _runtimeResolver.EnsureManagedMagnetarCurrentAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Managed Magnetar update check failed.");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private void SetState(ManagedRuntimeWarmupState state, string message)
    {
        lock (_sync)
        {
            _snapshot = state == ManagedRuntimeWarmupState.Running
                ? ManagedRuntimeWarmupSnapshot.CreateInitial()
                : _snapshot;

            _snapshot = _snapshot with
            {
                State = state,
                Message = message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            if (state == ManagedRuntimeWarmupState.Failed)
            {
                _snapshot = _snapshot.WithComponents(component => component.State is ManagedRuntimeComponentState.Ready
                    ? component
                    : component with
                    {
                        State = ManagedRuntimeComponentState.Failed,
                        Message = message,
                        Percent = null,
                    });
            }
        }

        Changed?.Invoke();
    }

    private void ApplyProgress(ManagedRuntimeInstallProgress progress)
    {
        lock (_sync)
        {
            _snapshot = _snapshot.WithComponent(progress.Component, component => component with
            {
                State = MapState(progress.Phase),
                Message = progress.Message,
                Percent = progress.Percent,
                Path = progress.Path,
            }) with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        Changed?.Invoke();
    }

    private static ManagedRuntimeComponentState MapState(ManagedRuntimeInstallPhase phase) => phase switch
    {
        ManagedRuntimeInstallPhase.Checking => ManagedRuntimeComponentState.Checking,
        ManagedRuntimeInstallPhase.Downloading => ManagedRuntimeComponentState.Downloading,
        ManagedRuntimeInstallPhase.Installing => ManagedRuntimeComponentState.Installing,
        ManagedRuntimeInstallPhase.Ready => ManagedRuntimeComponentState.Ready,
        ManagedRuntimeInstallPhase.Failed => ManagedRuntimeComponentState.Failed,
        _ => ManagedRuntimeComponentState.Pending,
    };
}

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
}
