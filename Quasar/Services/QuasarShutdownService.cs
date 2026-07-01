using Magnetar.Protocol.Runtime;
using Quasar.Models;
using System.Diagnostics;

namespace Quasar.Services;

/// <summary>
/// Orchestrates stopping all managed Magnetar servers, and recycling the Quasar
/// worker with or without leaving those servers running.
/// </summary>
public sealed class QuasarShutdownService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly WebServiceOptions _options;
    private readonly ILogger<QuasarShutdownService> _logger;

    public QuasarShutdownService(
        IHostApplicationLifetime lifetime,
        DedicatedServerSupervisor supervisor,
        WebServiceOptions options,
        ILogger<QuasarShutdownService> logger)
    {
        _lifetime = lifetime;
        _supervisor = supervisor;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gracefully stops every running Magnetar server. Quasar itself keeps running;
    /// the worker is not restarted. Progress messages are reported via
    /// <paramref name="progress"/> so the caller can update a UI while waiting.
    /// </summary>
    /// <param name="setGoalStateOff">
    /// When <c>true</c> the goal state of each server is set to Off before stopping
    /// it, so the reconcile loop treats the shutdown as intentional and does not
    /// auto-restart the server. Used by the admin "Shut down all servers" action,
    /// where Quasar stays up. Left <c>false</c> for full Quasar shutdown, so the
    /// servers resume on the next worker boot per their configured goal state.
    /// </param>
    public async Task StopAllServersAsync(IProgress<string>? progress = null, bool setGoalStateOff = false, CancellationToken cancellationToken = default)
    {
        var running = _supervisor.GetSnapshots()
            .Where(static s => s.State is DedicatedServerProcessState.Starting
                or DedicatedServerProcessState.Running
                or DedicatedServerProcessState.Restarting
                or DedicatedServerProcessState.Stopping)
            .ToList();

        if (running.Count == 0)
            return;

        progress?.Report($"Stopping {running.Count} server{(running.Count == 1 ? "" : "s")}…");

        foreach (var snapshot in running)
        {
            var label = snapshot.UniqueName;
            progress?.Report($"Stopping \"{label}\"…");

            try
            {
                // Record the intent first (without a competing reconcile-driven stop)
                // so the supervisor never auto-restarts the server we are about to stop.
                if (setGoalStateOff)
                    await _supervisor.SetGoalStateAsync(snapshot.UniqueName, DedicatedServerGoalState.Off, reconcile: false, cancellationToken);

                await _supervisor.StopServerAsync(snapshot.UniqueName, forceAfter: null, cancellationToken);
            }
            catch
            {
                // Best-effort: keep shutting down the remaining servers.
            }
        }
    }

    /// <summary>
    /// Gracefully stops every running Magnetar server, then requests host shutdown.
    /// Used by the launcher-driven full shutdown (drain endpoint / POSIX signals).
    /// </summary>
    public async Task ShutdownAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await StopAllServersAsync(progress, cancellationToken: cancellationToken);
        progress?.Report("Shutting down Quasar…");
        _lifetime.StopApplication();
    }

    /// <summary>
    /// Restarts the Quasar web worker <em>without</em> stopping any managed server.
    /// Running Magnetar servers stay up (detached via <c>-daemon</c>) and are
    /// re-adopted by process id once the Bootstrap launcher respawns the worker.
    /// When Quasar runs standalone (no launcher) the worker simply stops and is not
    /// brought back.
    /// </summary>
    public void RestartWorker(IProgress<string>? progress = null)
    {
        // BeginLauncherDrain marks the supervisor to preserve servers on this stop and
        // persists the runtime snapshot (including PIDs) so the next worker can adopt
        // them; StopApplication then exits the worker for the launcher to respawn.
        progress?.Report("Restarting Quasar worker…");
        _supervisor.BeginLauncherDrain();
        _lifetime.StopApplication();
    }

    /// <summary>
    /// Stops Quasar while preserving managed servers. When launched by Bootstrap,
    /// the worker first asks the launcher process to exit so it does not
    /// respawn the worker.
    /// </summary>
    public void ShutdownQuasarPreservingServers(IProgress<string>? progress = null)
    {
        progress?.Report("Shutting down Quasar…");
        _supervisor.BeginLauncherDrain();
        if (TryRequestSystemdServiceStop())
            return;

        RequestLauncherShutdown();
        _lifetime.StopApplication();
    }

    private bool TryRequestSystemdServiceStop()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var target = ResolveSystemdServiceTarget();
        if (target is null)
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            AddSystemctlStopArguments(startInfo, target.Value);

            using var process = Process.Start(startInfo);

            if (process is null)
                return false;

            if (!process.WaitForExit(3000))
            {
                TryKillProcess(process);
                _logger.LogWarning(
                    "systemctl stop {ServiceName} did not return before timeout; falling back to launcher shutdown request.",
                    target.Value.ServiceName);
                return false;
            }

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Requested systemd stop for {ServiceName}.", target.Value.ServiceName);
                return true;
            }

            _logger.LogWarning(
                "systemctl stop {ServiceName} failed with exit code {ExitCode}; falling back to launcher shutdown request.",
                target.Value.ServiceName,
                process.ExitCode);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed requesting systemd stop; falling back to launcher shutdown request.");
        }

        return false;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
        }
        catch
        {
        }
    }

    private static void AddSystemctlStopArguments(ProcessStartInfo startInfo, SystemdServiceTarget target)
    {
        if (target.Scope == SystemdServiceScope.User)
            startInfo.ArgumentList.Add("--user");

        startInfo.ArgumentList.Add("--no-block");
        startInfo.ArgumentList.Add("stop");
        startInfo.ArgumentList.Add(target.ServiceName);
    }

    private static SystemdServiceTarget? ResolveSystemdServiceTarget()
    {
        var serviceName = NormalizeServiceName(Environment.GetEnvironmentVariable("QUASAR_SYSTEMD_SERVICE"));
        var scope = ParseSystemdScope(Environment.GetEnvironmentVariable("QUASAR_SYSTEMD_SCOPE"));
        if (!string.IsNullOrWhiteSpace(serviceName) && scope is not null)
            return new SystemdServiceTarget(serviceName, scope.Value);

        var detected = DetectSystemdServiceTargetFromCgroup();
        if (detected is null)
            return null;

        return new SystemdServiceTarget(serviceName ?? detected.Value.ServiceName, scope ?? detected.Value.Scope);
    }

    private static SystemdServiceTarget? DetectSystemdServiceTargetFromCgroup()
    {
        const string cgroupPath = "/proc/self/cgroup";
        if (!File.Exists(cgroupPath))
            return null;

        try
        {
            foreach (var line in File.ReadLines(cgroupPath))
            {
                var pathStart = line.IndexOf(":/", StringComparison.Ordinal);
                if (pathStart < 0)
                    continue;

                var path = line[(pathStart + 1)..];
                var serviceName = ExtractServiceName(path);
                if (serviceName is null)
                    continue;

                if (path.Contains("/user.slice/", StringComparison.Ordinal) ||
                    path.Contains("/user@", StringComparison.Ordinal))
                {
                    return new SystemdServiceTarget(serviceName, SystemdServiceScope.User);
                }

                if (path.Contains("/system.slice/", StringComparison.Ordinal))
                    return new SystemdServiceTarget(serviceName, SystemdServiceScope.System);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ExtractServiceName(string cgroupPath)
    {
        foreach (var segment in cgroupPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (segment.EndsWith(".service", StringComparison.Ordinal))
                return segment;
        }

        return null;
    }

    private static string? NormalizeServiceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var serviceName = value.Trim();
        return serviceName.EndsWith(".service", StringComparison.Ordinal)
            ? serviceName
            : $"{serviceName}.service";
    }

    private static SystemdServiceScope? ParseSystemdScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "user" => SystemdServiceScope.User,
            "system" => SystemdServiceScope.System,
            _ => null,
        };
    }

    private void RequestLauncherShutdown()
    {
        if (string.IsNullOrWhiteSpace(_options.LauncherToken))
            return;

        try
        {
            File.WriteAllText(GetLauncherShutdownRequestPath(), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed writing Quasar launcher shutdown request.");
        }
    }

    private static string GetLauncherShutdownRequestPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "launcher-shutdown-request");

    private readonly record struct SystemdServiceTarget(string ServiceName, SystemdServiceScope Scope);

    private enum SystemdServiceScope
    {
        User,
        System,
    }
}
