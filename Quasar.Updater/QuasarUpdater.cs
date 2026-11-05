using System.Net.Http.Json;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Updates;

internal sealed class QuasarUpdater(QuasarServiceInstallation installation, ILogger<QuasarUpdater> logger,
    Func<QuasarServiceInstallation, QuasarUpdateOperation, CancellationToken, Task<string>>? preparePackage = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(MaintenancePath) || LauncherReplacement.Phase(installation.InstallDirectory) is "installing" or "ready" or "attempted" ||
                    QuasarUpdateJournal.Read()?.Stage == UpdateStage.Restarting) await RecoverAsync(stoppingToken);
                else await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                using var lease = await QuasarMaintenanceLease.AcquireAsync(CancellationToken.None);
                QuasarUpdateJournal.Fail(exception.Message + " Check updater permissions and use Quasar update --retry.");
                logger.LogError(exception, "Quasar update/recovery failed; the retained installation remains available.");
            }
            await Task.Delay(1000, stoppingToken);
        }
    }

    internal async Task ProcessAsync(CancellationToken token)
    {
        var operation = QuasarUpdateJournal.Read();
        if (operation?.Stage != UpdateStage.Ready || !operation.ActivateWhenReady) return;
        var status = await NativeQuasarServices.ReadAsync(installation, false, token);
        if (!status.Running) return; // Respect intentional shutdown; staging is harmless.
        var capability = await NativeQuasarServices.VerifyAsync(installation, status.Pid, token);
        QuasarUpdateJournal.WriteJson(BootstrapUpdateSupport.CapabilityPath, capability);
        if (!capability.CanRestart)
        {
            using var lease = await QuasarMaintenanceLease.AcquireAsync(token);
            QuasarUpdateJournal.Write(operation with { Message = capability.Detail + " " + capability.Repair });
            return;
        }
        try { await WaitHealthyAsync(string.IsNullOrWhiteSpace(operation.PreviousVersion) ? operation.Version : operation.PreviousVersion, token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            QuasarUpdateJournal.WriteJson(BootstrapUpdateSupport.CapabilityPath, capability with { CanRestart = false, Detail = "The owning main service did not verify its application identity: " + exception.Message });
            throw;
        }
        // Prepare/validate while the existing main service still runs.
        if (!operation.RestartOnly && !operation.RestorePrevious)
        {
            var settingsPath = Path.Combine(installation.DataDirectory, "Updates", "Staged", "Package-" + operation.Id.ToString("N"), "Settings", "appsettings.json");
            if (!string.Equals(Path.GetFullPath(operation.ResolvedSettings), Path.GetFullPath(settingsPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The resolved configuration is outside its update operation.");
        }
        var payload = operation.RestartOnly || operation.RestorePrevious ? "" : preparePackage is null ? await TrustedUpdatePackage.PrepareAsync(installation, operation, token) : await preparePackage(installation, operation, token);
        using (await QuasarMaintenanceLease.AcquireAsync(token))
        {
            var currentStatus = await NativeQuasarServices.ReadAsync(installation, false, token);
            if (!currentStatus.Running || currentStatus.Pid != status.Pid)
            {
                if (QuasarUpdateJournal.Read()?.Id == operation.Id)
                    QuasarUpdateJournal.Write(operation with { Message = "Quasar stopped or changed while preparing. Staged safely; start the main service explicitly before activation." });
                return;
            }
            if (QuasarUpdateJournal.Read()?.Id != operation.Id || QuasarUpdateJournal.Read()?.Stage != UpdateStage.Ready) return;
            QuasarUpdateJournal.WriteText(MaintenancePath, operation.Id.ToString());
            QuasarUpdateJournal.Write(operation with { Stage = UpdateStage.Restarting, Message = "Updater is stopping Quasar, replacing the application and checking the requested release." });
        }
        // Do not hold the maintenance lease across native Stop: the main service
        // must finish/persist its own shutdown without deadlocking the updater.
        try
        {
            await NativeQuasarServices.StopAsync(installation, token);
            if (!operation.RestartOnly)
            {
                using var lease = await QuasarMaintenanceLease.AcquireAsync(token);
                if (operation.RestorePrevious)
                {
                    LauncherReplacement.SetPhase(installation.InstallDirectory, "installing");
                    LauncherReplacement.Restore(installation.InstallDirectory);
                    RestoreDataSettings();
                }
                else LauncherReplacement.Apply(payload, installation.InstallDirectory);
                QuasarUpdateJournal.WriteText(Path.Combine(LauncherReplacement.TransactionDirectory(installation.InstallDirectory), "operation-id"), operation.Id.ToString());
                if (!operation.RestorePrevious)
                {
                    QuasarUpdateJournal.WriteText(Path.Combine(LauncherReplacement.TransactionDirectory(installation.InstallDirectory), "previous-version"), operation.PreviousVersion);
                    BackupSettings();
                }
                if (!operation.RestorePrevious && File.Exists(operation.ResolvedSettings))
                    QuasarUpdateJournal.WriteText(Path.Combine(installation.DataDirectory, "appsettings.json"), await File.ReadAllTextAsync(operation.ResolvedSettings, token));
                LauncherReplacement.SetPhase(installation.InstallDirectory, "attempted");
            }
            await NativeQuasarServices.StartAsync(installation, token);
            var healthy = await WaitHealthyAsync(operation.Version, token);
            using (await QuasarMaintenanceLease.AcquireAsync(token))
            {
                if (!operation.RestartOnly) LauncherReplacement.SetPhase(installation.InstallDirectory, "committed");
                QuasarUpdateJournal.Write(operation with { Stage = UpdateStage.Healthy, Message = $"Quasar {operation.Version} is running and passed native-service and application health checks.",
                    HealthyProcessId = healthy.Pid, HealthyUpdaterProcessId = Environment.ProcessId, HealthyProcessStartedAtUtc = healthy.Started });
                File.Delete(MaintenancePath);
            }
        }
        catch (Exception exception)
        {
            using (await QuasarMaintenanceLease.AcquireAsync(CancellationToken.None)) QuasarUpdateJournal.Fail(exception.Message);
            logger.LogError(exception, "Update {OperationId} failed; restoring the previous application.", operation.Id);
            await RecoverAsync(CancellationToken.None, exception.Message);
        }
    }

    private string MaintenancePath => Path.Combine(installation.DataDirectory, "Updates", "application-maintenance");

    private void BackupSettings() => LauncherReplacement.BackupSettings(installation.InstallDirectory, installation.DataDirectory);
    private void RestoreDataSettings() => LauncherReplacement.RestoreSettings(installation.InstallDirectory, installation.DataDirectory);

    private async Task RecoverAsync(CancellationToken token, string reason = "Update was interrupted before health confirmation.")
    {
        var phase = LauncherReplacement.Phase(installation.InstallDirectory);
        var operation = QuasarUpdateJournal.Read();
        var operationFile = Path.Combine(LauncherReplacement.TransactionDirectory(installation.InstallDirectory), "operation-id");
        var sameTransaction = operation is not null && File.Exists(operationFile) && File.ReadAllText(operationFile) == operation.Id.ToString();
        if (phase == "committed" && sameTransaction && operation?.Stage == UpdateStage.Restarting)
        {
            try
            {
                var healthy = await WaitHealthyAsync(operation.Version, token);
                using var lease = await QuasarMaintenanceLease.AcquireAsync(token);
                QuasarUpdateJournal.Write(operation with { Stage = UpdateStage.Healthy, Message = "Update health acknowledgement recovered after updater restart.",
                    HealthyProcessId = healthy.Pid, HealthyUpdaterProcessId = Environment.ProcessId, HealthyProcessStartedAtUtc = healthy.Started });
                File.Delete(MaintenancePath);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { reason = exception.Message; }
        }
        var status = await NativeQuasarServices.ReadAsync(installation, false, token);
        if (phase is not ("installing" or "ready" or "attempted") && !(sameTransaction && phase == "committed") && status.Running)
        {
            using var lease = await QuasarMaintenanceLease.AcquireAsync(token);
            QuasarUpdateJournal.Fail(reason + " The original service is still running; retry safely.");
            File.Delete(MaintenancePath);
            return;
        }
        await NativeQuasarServices.StopAsync(installation, token);
        using (await QuasarMaintenanceLease.AcquireAsync(token))
        {
            if (phase is "installing" or "ready" or "attempted" || sameTransaction && phase == "committed")
            {
                LauncherReplacement.Restore(installation.InstallDirectory);
                RestoreDataSettings();
            }
            QuasarUpdateJournal.Fail(reason + " Previous application restored; inspect logs and use Quasar update --retry.");
        }
        await NativeQuasarServices.StartAsync(installation, token);
        File.Delete(MaintenancePath);
    }

    private async Task<(int Pid, DateTimeOffset Started)> WaitHealthyAsync(string version, CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await NativeQuasarServices.ReadAsync(installation, false, token);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(installation.HealthUrl), "/api/internal/update-health"));
                request.Headers.Add("X-Quasar-Launcher-Token", installation.HealthToken);
                using var response = await client.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                {
                    var health = await response.Content.ReadFromJsonAsync<Health>(cancellationToken: token);
                    if (status.Running && health?.Pid == status.Pid && health.Version == version) return (health.Pid, health.Started);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(500, token);
        }
        throw new InvalidOperationException($"Quasar {version} did not pass service identity and application health checks within 60 seconds.");
    }
    private sealed record Health(int Pid, string Version, DateTimeOffset Started);
}
