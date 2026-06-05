using Quasar.Models;

namespace Quasar.Services;

/// <summary>
/// Orchestrates a graceful shutdown of all managed Magnetar servers before
/// stopping the Quasar host process.
/// </summary>
public sealed class QuasarShutdownService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DedicatedServerSupervisor _supervisor;

    public QuasarShutdownService(IHostApplicationLifetime lifetime, DedicatedServerSupervisor supervisor)
    {
        _lifetime = lifetime;
        _supervisor = supervisor;
    }

    /// <summary>
    /// Gracefully stops every running Magnetar instance, then requests host shutdown.
    /// Progress messages are reported via <paramref name="progress"/> so the caller
    /// can update a UI while waiting.
    /// </summary>
    public async Task ShutdownAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var running = _supervisor.GetSnapshots()
            .Where(static s => s.State is DedicatedServerInstanceProcessState.Starting
                or DedicatedServerInstanceProcessState.Running
                or DedicatedServerInstanceProcessState.Restarting
                or DedicatedServerInstanceProcessState.Stopping)
            .ToList();

        if (running.Count > 0)
        {
            progress?.Report($"Stopping {running.Count} instance{(running.Count == 1 ? "" : "s")}…");

            foreach (var snapshot in running)
            {
                var label = snapshot.UniqueName;
                progress?.Report($"Stopping \"{label}\"…");

                try
                {
                    await _supervisor.StopInstanceAsync(snapshot.UniqueName, forceAfter: null, cancellationToken);
                }
                catch
                {
                    // Best-effort: keep shutting down the remaining instances.
                }
            }
        }

        progress?.Report("Shutting down Quasar…");
        _lifetime.StopApplication();
    }
}
