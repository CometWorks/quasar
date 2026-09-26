using System.Net;
using System.Text.Json;

namespace Quasar.Services;

/// <summary>Installs the update timer on legacy Hosts enrolled on this same machine.</summary>
public sealed class ClusterLocalHostUpdater(ClusterHostCatalog hosts, ClusterHostInstaller installer,
    ILogger<ClusterLocalHostUpdater> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (File.Exists(installer.BinaryPath))
                foreach (var host in hosts.GetAll())
                {
                    if (!IPAddress.TryParse(host.Address, out var address) || !IPAddress.IsLoopback(address)) continue;
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string timer = Path.Combine(home, ".config", "systemd", "user", $"quasar-host-{host.Id}-update.timer");
                    if (File.Exists(timer)) continue;
                    string config = Path.Combine(home, ".local", "share", "Quasar", "Hosts", host.Id, "host.json");
                    if (!File.Exists(config)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(config));
                        string origin = document.RootElement.GetProperty("connection").GetProperty("quasarUrl").GetString()
                            ?? throw new InvalidDataException("Host has no Quasar origin.");
                        await installer.InstallLocalAsync(host.Id, origin, stoppingToken);
                        logger.LogInformation("Installed automatic updates for local Host {Host}.", host.Id);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
                        or JsonException or KeyNotFoundException or ArgumentException or System.ComponentModel.Win32Exception)
                    {
                        logger.LogWarning(error, "Could not install automatic updates for local Host {Host}.", host.Id);
                    }
                }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
