using System.Net;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Quasar.Services;

/// <summary>Prepares the local cluster host and maintains its automatic updates.</summary>
public sealed class ClusterLocalHostUpdater(ClusterHostCatalog hosts, ClusterHostInstaller installer,
    ClusterHostTunnels tunnels, WebServiceOptions options, ILogger<ClusterLocalHostUpdater> logger) : BackgroundService
{
    private string _status = "Preparing the local Host executor.";
    private string? _error, _localHostId;
    public string Status => _localHostId is not null && tunnels.IsConnected(_localHostId) ? "Local Host executor connected." : _status;
    public string? Error => _localHostId is not null && tunnels.IsConnected(_localHostId) ? null : _error;

    internal static string SelectLocalAddress(IEnumerable<IPAddress> addresses) => addresses
        .Where(a => !IPAddress.IsLoopback(a) && ClusterHostCatalog.IsClusterAddress(a.ToString()))
        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .Select(a => a.ToString()).FirstOrDefault() ?? "127.0.0.1";

    internal async Task EnsureLocalHostAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            _status = "Automatic local cluster hosting requires Linux x64. Enroll a Linux host machine to run cluster processes.";
            return;
        }
        try
        {
            _ = installer.GetBinaryInfo();
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
            var local = hosts.GetAll().FirstOrDefault(h => IPAddress.TryParse(h.Address, out var address)
                && (IPAddress.IsLoopback(address) || addresses.Contains(address)))
                ?? await hosts.RegisterAsync("localhost", options.HostName + " (local)", SelectLocalAddress(addresses), 18400, token);
            _localHostId = local.Id;
            if (!tunnels.IsConnected(local.Id))
            {
                _status = "Preparing the local Host executor.";
                var listening = new Uri(options.ListenUrl);
                string origin = listening.Host is "0.0.0.0" or "::" or "[::]" || listening.IsLoopback
                    ? new UriBuilder(listening) { Host = "127.0.0.1" }.Uri.AbsoluteUri : options.BaseUrl;
                await installer.InstallLocalAsync(local.Id, origin, token);
            }
            _error = null;
            _status = "Local Host executor installed; waiting for its connection.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
            or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception
            || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            _error = error.Message;
            _status = "Local Host executor is not ready.";
            logger.LogWarning(error, "Could not prepare the local Host executor.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnsureLocalHostAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
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
                        logger.LogInformation("Installed automatic updates for local Host executor {Host}.", host.Id);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
                        or JsonException or KeyNotFoundException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
                    {
                        logger.LogWarning(error, "Could not install automatic updates for local Host executor {Host}.", host.Id);
                    }
                }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
