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
    /// Gracefully stops every running Magnetar server, then requests host shutdown.
    /// Progress messages are reported via <paramref name="progress"/> so the caller
    /// can update a UI while waiting.
    /// </summary>
    public async Task ShutdownAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var running = _supervisor.GetSnapshots()
            .Where(static s => s.State is DedicatedServerProcessState.Starting
                or DedicatedServerProcessState.Running
                or DedicatedServerProcessState.Restarting
                or DedicatedServerProcessState.Stopping)
            .ToList();

        if (running.Count > 0)
        {
            progress?.Report($"Stopping {running.Count} server{(running.Count == 1 ? "" : "s")}…");

            foreach (var snapshot in running)
            {
                var label = snapshot.UniqueName;
                progress?.Report($"Stopping \"{label}\"…");

                try
                {
                    await _supervisor.StopServerAsync(snapshot.UniqueName, forceAfter: null, cancellationToken);
                }
                catch
                {
                    // Best-effort: keep shutting down the remaining servers.
                }
            }
        }

        progress?.Report("Shutting down Quasar…");
        _lifetime.StopApplication();
    }
}
