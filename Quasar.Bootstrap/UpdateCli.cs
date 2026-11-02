using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;
using Quasar.Updates;

namespace Quasar.Bootstrap;

internal static class UpdateCli
{
    // CLI uses local filesystem permissions, just like activate-release. It never
    // trusts a remote URL or bypasses the UI's authenticated HTTP boundary.
    internal static async Task<int> RunAsync(string[] args, TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken token = default)
    {
        stdout ??= Console.Out; stderr ??= Console.Error;
        if (args.Length == 2 && args[1] is "--help" or "-h")
        {
            await stdout.WriteLineAsync("Quasar update --version <release> | --status | --retry\nUses this executable's install root (or QUASAR_INSTALL_DIR). Run as the installation account.\nExit codes: 0 healthy/status; 2 usage; 3 permission/unavailable; 4 staged/manual restart; 5 update failed; 6 still pending (use --status).");
            return 0;
        }
        var status = args.Length == 2 && args[1] == "--status";
        var retry = args.Length == 2 && args[1] == "--retry";
        var version = args.Length == 3 && args[1] == "--version" ? args[2] : null;
        if (!status && !retry && version is null) { await stderr.WriteLineAsync("Usage: Quasar update --version <release> | --status | --retry"); return 2; }
        try
        {
            if (status)
            {
                var state = QuasarUpdateJournal.Read();
                await stdout.WriteLineAsync(state is null ? "No update operation recorded." : JsonSerializer.Serialize(state, QuasarUpdateJournal.JsonOptions));
                var capability = BootstrapUpdateSupport.Read();
                await stdout.WriteLineAsync(capability.Supervisor + ": " + capability.Detail + (capability.CanRestart ? "" : " " + capability.Repair));
                if (state?.Stage == UpdateStage.Healthy) await stdout.WriteLineAsync(await IsRunningAsync(state, token) ? "Requested release is running and healthy." : "Last update passed health checks; that application process is no longer running. Start Quasar and recheck.");
                return state?.Stage == UpdateStage.Failed ? 5 : state?.Stage == UpdateStage.Ready ? 4 : 0;
            }
            QuasarUpdateOperation operation;
            using (await QuasarMaintenanceLease.AcquireAsync(token))
            {
                var previous = QuasarUpdateJournal.Read();
                if (retry && previous is { Stage: UpdateStage.Ready, WebRelease: null } && !LocalWorkerExists() && await NativeServicesStoppedAsync(token))
                {
                    QuasarUpgradePath.EnsureAllowed(previous.Version, MagnetarPaths.GetQuasarDirectory());
                    await VerifiedUpdatePackage.VerifyAsync(previous.PackageDirectory, token);
                    QuasarUpdateJournal.Write(previous with { Stage = UpdateStage.Restarting, Message = "Manual application replacement prepared. Start Quasar to complete health verification." });
                    try
                    {
                        LauncherReplacement.Apply(previous.PackageDirectory, AppContext.BaseDirectory);
                        LauncherReplacement.BackupSettings(AppContext.BaseDirectory, MagnetarPaths.GetQuasarDirectory());
                        QuasarUpdateJournal.WriteText(Path.Combine(LauncherReplacement.TransactionDirectory(AppContext.BaseDirectory), "previous-version"), previous.PreviousVersion);
                        if (File.Exists(previous.ResolvedSettings))
                            QuasarUpdateJournal.WriteText(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "appsettings.json"), File.ReadAllText(previous.ResolvedSettings));
                    }
                    catch (Exception exception) { QuasarUpdateJournal.Fail(exception.Message); throw; }
                    await stdout.WriteLineAsync("Application files installed. Start Quasar manually; success will be recorded after startup. If it cannot load, run Updater/Quasar.Updater recover --root <installation>.");
                    return 4;
                }
                if (retry && previous is { Stage: UpdateStage.Ready, WebRelease: not null } && !LocalWorkerExists() && !LocalBootstrapExists())
                {
                    await VerifiedUpdatePackage.VerifyAsync(previous.PackageDirectory, token);
                    QuasarWebReleaseLayout.ValidateDirectory(previous.WebRelease.WorkingDirectory);
                    QuasarUpdateJournal.Write(previous with { Stage = UpdateStage.Restarting, Message = "Manual activation prepared. Start Quasar from this installation to complete health verification." });
                    try
                    {
                        if (previous.NeedsBootstrap) LauncherReplacement.Apply(previous.PackageDirectory, MagnetarPaths.GetQuasarDirectory());
                        var active = MagnetarPaths.GetQuasarActiveReleasePath();
                        if (File.Exists(active)) QuasarUpdateJournal.WriteText(active + ".previous", File.ReadAllText(active));
                        QuasarUpdateJournal.WriteJson(active, previous.WebRelease);
                        SyncMergedSettings(previous.WebRelease);
                    }
                    catch (Exception exception) { QuasarUpdateJournal.Fail(exception.Message); throw; }
                    await stdout.WriteLineAsync("Prepared release " + previous.Version + ". Start Bootstrap manually: " + (OperatingSystem.IsWindows() ? ".\\Quasar.exe serve --foreground" : "./Quasar serve --foreground") + ". Success is recorded only after worker health checks. If it cannot start, run the installation's quasar-supervisor recovery script.");
                    return 4;
                }
                if (!LocalWorkerExists()) { await stderr.WriteLineAsync("No running local Quasar update service. Start Quasar from this installation, then retry. Use --status for retained failures/staging."); return 3; }
                operation = QuasarUpdateJournal.Submit(version, retry);
            }
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            var lastMessage = "";
            while (DateTimeOffset.UtcNow < deadline)
            {
                var current = QuasarUpdateJournal.Read() ?? throw new InvalidDataException("Update journal disappeared.");
                if (current.Id != operation.Id) { await stderr.WriteLineAsync("The requested operation was superseded. Use update --status."); return 5; }
                var message = $"{current.Id} {current.Version}: {current.Stage}: {current.Message}";
                if (message != lastMessage) { await stdout.WriteLineAsync(message); lastMessage = message; }
                if (current.Stage == UpdateStage.Failed) return 5;
                if (current.Stage == UpdateStage.Ready && QuasarServiceInstallation.Current is null && !File.Exists(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath())) return 4;
                if (current.Stage == UpdateStage.Healthy)
                {
                    if (await IsRunningAsync(current, token)) return 0;
                    await stderr.WriteLineAsync("The last health acknowledgement belongs to processes that are no longer healthy/running. Start Quasar and inspect update --status.");
                    return 3;
                }
                await Task.Delay(500, token);
            }
            await stderr.WriteLineAsync("The operation is still pending. No success reported. Use Quasar update --status; do not submit another activation.");
            return 6;
        }
        catch (OperationCanceledException) { await stderr.WriteLineAsync("Waiting cancelled; the durable update may still be running. Use update --status."); return 130; }
        catch (ArgumentException exception) { await stderr.WriteLineAsync(exception.Message); return 2; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or HttpRequestException)
        { await stderr.WriteLineAsync(exception.Message + " Run as the installation account; inspect Quasar update --status and the Bootstrap log."); return 3; }
    }

    internal static void SyncMergedSettings(QuasarActiveReleasePointer release)
    {
        var settings = Path.Combine(release.WorkingDirectory, "appsettings.json");
        if (File.Exists(settings)) QuasarUpdateJournal.WriteText(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "appsettings.json"), File.ReadAllText(settings));
    }

    private static async Task<bool> NativeServicesStoppedAsync(CancellationToken token)
    {
        if (QuasarServiceInstallation.Current is not { } installation) return true;
        return !(await NativeQuasarServices.ReadAsync(installation, false, token)).Running &&
               !(await NativeQuasarServices.ReadAsync(installation, true, token)).Running;
    }

    private static bool LocalBootstrapExists()
    {
        var capability = BootstrapUpdateSupport.Read();
        return ProcessMatches(capability.ProcessId, capability.ProcessStartedAtUtc);
    }

    private static bool LocalWorkerExists()
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<WebServiceDiscoveryManifest>(File.ReadAllText(MagnetarPaths.GetWebServiceManifestPath()), QuasarUpdateJournal.JsonOptions);
            if (manifest is null) return false;
            using var process = Process.GetProcessById(manifest.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() <= manifest.StartedAtUtc.UtcDateTime &&
                manifest.StartedAtUtc.UtcDateTime - process.StartTime.ToUniversalTime() < TimeSpan.FromMinutes(2);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or JsonException) { return false; }
    }

    internal static async Task<bool> IsRunningAsync(QuasarUpdateOperation operation, CancellationToken token)
    {
        if (QuasarServiceInstallation.Current is { } installation && operation.HealthyUpdaterProcessId > 0)
        {
            try
            {
                var status = await NativeQuasarServices.ReadAsync(installation, false, token);
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(installation.HealthUrl), "/api/internal/update-health"));
                request.Headers.Add("X-Quasar-Launcher-Token", installation.HealthToken);
                using var response = await client.SendAsync(request, token);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return response.IsSuccessStatusCode && status.Running && json.RootElement.GetProperty("pid").GetInt32() == status.Pid && json.RootElement.GetProperty("version").GetString() == operation.Version;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException) { return false; }
        }
        if (operation.HealthyProcessId > 0 && operation.HealthyProcessStartedAtUtc is not null)
        {
            if (!ProcessMatches(operation.HealthyProcessId, operation.HealthyProcessStartedAtUtc)) return false;
            return await CheckManifestHealthAsync(operation.HealthyProcessId, token);
        }
        if (!ProcessMatches(operation.HealthyBootstrapPid, operation.HealthyBootstrapStartedAtUtc) ||
            !ProcessMatches(operation.HealthyWorkerPid, operation.HealthyWorkerStartedAtUtc)) return false;
        return await CheckManifestHealthAsync(operation.HealthyWorkerPid, token);
    }
    private static async Task<bool> CheckManifestHealthAsync(int pid, CancellationToken token)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<WebServiceDiscoveryManifest>(File.ReadAllText(MagnetarPaths.GetWebServiceManifestPath()), QuasarUpdateJournal.JsonOptions);
            if (manifest?.ProcessId != pid) return false;
            // Restrict health traffic to the local installation, bypassing proxies.
            var url = QuasarServiceInstallation.Current is { } configured ? new Uri(configured.HealthUrl) : new UriBuilder(manifest.BaseUrl) {
                Host = IPAddress.TryParse(new Uri(manifest.BaseUrl).Host, out var address) && QuasarServiceInstallation.IsLocalAddress(address) ? address.ToString() : "127.0.0.1" }.Uri;
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            var serving = await client.GetFromJsonAsync<WebServiceDiscoveryManifest>(new Uri(url, "/api/discovery"), token);
            using var health = await client.GetAsync(new Uri(url, "/api/health"), token);
            return health.IsSuccessStatusCode && serving?.ProcessId == manifest.ProcessId && serving.WorkerId == manifest.WorkerId;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TaskCanceledException or JsonException or UriFormatException) { return false; }
    }
    private static bool ProcessMatches(int pid, DateTimeOffset? started)
    {
        if (pid <= 0 || started is null) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && (process.StartTime.ToUniversalTime() - started.Value.UtcDateTime).Duration() < TimeSpan.FromSeconds(1); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
