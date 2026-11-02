using System.Diagnostics;
using System.Text.Json;
using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

public sealed record BootstrapRestartCapability
{
    public bool CanRestart { get; init; }
    public string Supervisor { get; init; } = "Manual launch";
    public string Detail { get; init; } = "No verified restart supervisor.";
    public string Repair { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTimeOffset? ProcessStartedAtUtc { get; init; }
}

internal static class BootstrapUpdateSupport
{
    internal static string CapabilityPath => Path.Combine(MagnetarPaths.GetQuasarUpdatesDirectory(), "restart-capability.json");
    internal static BootstrapRestartCapability Read() => File.Exists(CapabilityPath)
        ? JsonSerializer.Deserialize<BootstrapRestartCapability>(QuasarUpdateJournal.ReadText(CapabilityPath), QuasarUpdateJournal.JsonOptions) ?? Unavailable("Invalid supervisor status.")
        : Unavailable("No native main/updater service verification is available. Foreground updates are staged for manual restart.");

    internal static BootstrapRestartCapability Unavailable(string detail) => new()
    {
        Detail = detail,
        Repair = OperatingSystem.IsWindows()
            ? "Install or repair the native Quasar and QuasarUpdater services using the current install.ps1. Preserve existing paths and service names; Scheduled Tasks require explicit migration. Foreground updates need manual restart."
            : "Install or repair the native Quasar and updater systemd units using install.sh. Preserve the install path, service name, execution account and user/system scope. Foreground updates need manual restart."
    };

    // This source-tree launcher is retained for legacy worker recovery, not for
    // self-activation into the incompatible unified application layout.
    internal static Task<BootstrapRestartCapability> DetectAsync(string installDirectory, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable("Legacy Bootstrap cannot activate unified application packages. Migrate with the current platform installer.") with {
            ProcessId = Environment.ProcessId, ProcessStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime() });

    internal static async Task<(int ExitCode, string Output)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        Dictionary<string, string>? environment = null, int timeoutSeconds = 15)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {executable}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
    }
}
