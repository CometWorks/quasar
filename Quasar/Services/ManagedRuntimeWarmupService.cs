using Quasar.Models;

namespace Quasar.Services;

public sealed class ManagedRuntimeWarmupService : BackgroundService
{
    private readonly ManagedDedicatedServerRuntimeResolver _runtimeResolver;
    private readonly ILogger<ManagedRuntimeWarmupService> _logger;
    private readonly object _sync = new();
    private ManagedRuntimeWarmupSnapshot _snapshot = new()
    {
        State = ManagedRuntimeWarmupState.Pending,
        Message = "Managed runtime warmup pending.",
    };

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
            return _snapshot with { };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            SetState(ManagedRuntimeWarmupState.Running, "Preparing managed Magnetar and Dedicated Server runtime in background.");

            await _runtimeResolver.ResolveAsync(new DedicatedServerInstanceDefinition
            {
                UniqueName = "warmup",
            }, stoppingToken);

            SetState(ManagedRuntimeWarmupState.Complete, "Managed runtime ready.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Managed runtime warmup failed.");
            SetState(ManagedRuntimeWarmupState.Failed, exception.Message);
        }
    }

    private void SetState(ManagedRuntimeWarmupState state, string message)
    {
        lock (_sync)
        {
            _snapshot = new ManagedRuntimeWarmupSnapshot
            {
                State = state,
                Message = message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        Changed?.Invoke();
    }
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

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
