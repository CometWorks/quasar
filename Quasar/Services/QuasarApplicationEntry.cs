using System.Security.Cryptography;
using System.Text;
using Quasar.Updates;

namespace Quasar.Services;

internal static class QuasarApplicationEntry
{
    internal static bool TryRunCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(Magnetar.Protocol.Runtime.QuasarReleaseVersion.GetEntryAssemblyVersion()); return true; }
        if (args.FirstOrDefault() == "update") { exitCode = Quasar.Bootstrap.UpdateCli.RunAsync(args).GetAwaiter().GetResult(); return true; }
        if (args.FirstOrDefault() == "rollback" || args.Contains("--rollback"))
        {
            try
            {
                var installation = QuasarServiceInstallation.Current;
                var root = installation?.InstallDirectory ?? AppContext.BaseDirectory;
                var previous = Path.Combine(LauncherReplacement.TransactionDirectory(root), "previous-version");
                if (!File.Exists(previous) || !QuasarUpdateJournal.IsVersion(File.ReadAllText(previous))) throw new InvalidOperationException("No previous complete application release is retained. Select a release on Updates.");
                if (installation is null) throw new InvalidOperationException("Stop Quasar and use the retained updater recovery command, then start Quasar manually.");
                using var lease = QuasarMaintenanceLease.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
                var operation = QuasarUpdateJournal.Read();
                if (File.Exists(Path.Combine(Magnetar.Protocol.Runtime.MagnetarPaths.GetQuasarUpdatesDirectory(), "application-maintenance")))
                    throw new InvalidOperationException("Updater recovery is still in progress. Inspect update --status before rollback.");
                if (operation?.Stage is UpdateStage.Queued or UpdateStage.Downloading or UpdateStage.Verified or UpdateStage.Ready or UpdateStage.Restarting)
                    throw new InvalidOperationException("An update is pending. Inspect update --status before rollback.");
                var version = File.ReadAllText(previous);
                if (operation is { Stage: UpdateStage.Healthy } && operation.Version == version) throw new InvalidOperationException("The retained release is already restored.");
                QuasarUpdateJournal.Write(new QuasarUpdateOperation { Version = version, PreviousVersion = Magnetar.Protocol.Runtime.QuasarReleaseVersion.GetEntryAssemblyVersion(), Stage = UpdateStage.Ready, RestorePrevious = true, Message = "Complete application rollback requested; waiting for the updater service." });
                Console.WriteLine("Rollback queued. Use Quasar update --status to confirm its outcome.");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException) { Console.Error.WriteLine(exception.Message); exitCode = 3; }
            return true;
        }
        if (args.FirstOrDefault() == "cluster") { exitCode = Quasar.Bootstrap.ClusterCli.RunAsync(args).GetAwaiter().GetResult(); return true; }
        if (args.FirstOrDefault() is "help" or "--help" or "-h")
        {
            Console.WriteLine("Quasar [serve] [--headless] [--foreground]\nQuasar update --version <release> | --status | --retry\nQuasar cluster --help\nQuasar activate-release --restore (internal recovery; Quasar must be stopped)");
            return true;
        }
        if (args.FirstOrDefault() == "activate-release")
        {
            if (args.Length != 2 || args[1] != "--restore") { Console.Error.WriteLine("Internal recovery: Quasar activate-release --restore; stop Quasar and its updater first."); exitCode = 2; return true; }
            var installation = QuasarServiceInstallation.Current;
            var root = installation?.InstallDirectory ?? AppContext.BaseDirectory;
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "Updater", OperatingSystem.IsWindows() ? "Quasar.Updater.exe" : "Quasar.Updater")) { UseShellExecute = false };
            start.ArgumentList.Add("recover");
            if (installation is not null) { start.ArgumentList.Add("--installation"); start.ArgumentList.Add(installation.MetadataPath); }
            else { start.ArgumentList.Add("--root"); start.ArgumentList.Add(root); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Magnetar.Protocol.Runtime.MagnetarPaths.GetQuasarDirectory()); }
            try { using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot start retained updater recovery."); process.WaitForExit(); exitCode = process.ExitCode; }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception) { Console.Error.WriteLine(exception.Message); exitCode = 3; }
            return true;
        }
        return false;
    }
    internal static void ConfirmManualUpdate(string version)
    {
        if (QuasarServiceInstallation.Current is { } installation &&
            NativeQuasarServices.ReadAsync(installation, false, CancellationToken.None).GetAwaiter().GetResult().Pid == Environment.ProcessId) return;
        using var lease = QuasarMaintenanceLease.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (QuasarUpdateJournal.Read() is not { Stage: UpdateStage.Restarting, WebRelease: null } operation) return;
        if (operation.Version != version) { QuasarUpdateJournal.Fail("The started application is not the requested release. Use the retained installation for recovery."); return; }
        if (LauncherReplacement.Phase(AppContext.BaseDirectory) is "ready" or "attempted") LauncherReplacement.SetPhase(AppContext.BaseDirectory, "committed");
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        QuasarUpdateJournal.Write(operation with { Stage = UpdateStage.Healthy, Message = "The requested application completed manual startup.",
            HealthyProcessId = Environment.ProcessId, HealthyProcessStartedAtUtc = started });
    }

    internal static bool IsUpdaterAuthorized(HttpContext context, string token) =>
        !string.IsNullOrWhiteSpace(token) && IPAddressIsLoopback(context) && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(context.Request.Headers["X-Quasar-Launcher-Token"].ToString()), Encoding.UTF8.GetBytes(token));
    private static bool IPAddressIsLoopback(HttpContext context) => context.Connection.RemoteIpAddress is { } address &&
        QuasarServiceInstallation.IsLocalAddress(address);
}
