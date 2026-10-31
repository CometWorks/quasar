using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quasar.Hosting;

namespace Quasar.Bootstrap;

// Bootstrap owns the connector, independently of worker release swaps and restarts.
internal sealed class HostedPanelConnector(int workerPort, ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? run;
    private Process? process;
    private int adminPort;
    public void Start() => run = RunAsync(stop.Token);
    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        using var local = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 128 * 1024 };
        using var remote = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 64 * 1024 };
        string? configuration = null;
        RetirePreviousConnector();
        try
        {
            do
            {
                try
                {
                    var state = HostedPanelState.Read();
                    if (state is not { Choice: "hosted", Credential: not null })
                    {
                        StopProcess(); configuration = null;
                        if (state is not null && HostedPanelState.IsPanelId(state.PanelId))
                            await new HostedConnectorHealth(state.PanelId, false, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(ct);
                        continue;
                    }
                    var binary = await EnsureBinaryAsync(ct);
                    string trust = ExportTrust();
                    if (process is null || process.HasExited)
                    {
                        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                        adminPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    }
                    string config = BuildConfiguration(state, workerPort, trust, adminPort);
                    if (process is { HasExited: false } && configuration == config)
                    {
                        await ReportHealthAsync(state, local, remote, ct);
                        continue;
                    }
                    StopProcess();
                    string path = Path.Combine(HostedPanelState.DirectoryPath, "frpc.toml");
                    await PrivateFileAsync(path, config, ct);
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = HostedPanelState.DirectoryPath };
                    start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
                    process = Process.Start(start) ?? throw new IOException("Cannot start hosted connector.");
                    // Drain upstream logs without recording secrets or customer request paths.
                    process.OutputDataReceived += (_, _) => { };
                    process.ErrorDataReceived += (_, _) => { };
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    await File.WriteAllTextAsync(ProcessLeasePath, JsonSerializer.Serialize(new ConnectorLease(
                        process.Id, process.StartTime.ToUniversalTime().Ticks, binary)), ct);
                    configuration = config;
                    logger.LogInformation("Hosted connector started for {Panel}; local Quasar remains independent.", state.PanelId);
                    await ReportHealthAsync(state, local, remote, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                { logger.LogWarning("Hosted connector unavailable ({Type}); retrying. Local Quasar continues working.", e.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { StopProcess(); }
    }

    internal static string BuildConfiguration(HostedPanelState state, int workerPort, string caFile, int statusPort = 0)
    {
        if (!HostedPanelState.IsPanelId(state.PanelId) || state.Credential is null
            || state.Credential.Length != state.PanelId.Length + 65 || !state.Credential.StartsWith(state.PanelId + ".", StringComparison.Ordinal)
            || state.LocalProxySecret.Length != 64 || state.LocalProxySecret.Any(c => !char.IsAsciiHexDigit(c))
            || state.Credential[(state.PanelId.Length + 1)..].Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid private connector configuration.");
        return $$"""
            webServer.addr = "127.0.0.1"
            webServer.port = {{statusPort}}
            webServer.user = "nebula"
            webServer.password = "{{state.LocalProxySecret}}"
            serverAddr = "dash.se-servers.net"
            serverPort = 443
            user = "{{state.PanelId}}"
            loginFailExit = false
            transport.protocol = "wss"
            transport.tls.enable = true
            transport.tls.trustedCaFile = {{JsonSerializer.Serialize(caFile)}}
            transport.tls.serverName = "dash.se-servers.net"
            transport.heartbeatInterval = 10
            metadatas.connector_token = "{{state.Credential}}"
            log.level = "warn"
            log.to = "console"
            [[proxies]]
            name = "quasar"
            type = "http"
            localIP = "127.0.0.1"
            localPort = {{workerPort}}
            customDomains = ["dash.se-servers.net"]
            locations = ["/{{state.PanelId}}/"]
            requestHeaders.set.X-Quasar-Hosted = "{{state.LocalProxySecret}}"
            """;
    }

    internal static bool Registered(JsonElement status) => status.TryGetProperty("http", out var proxies)
        && proxies.ValueKind == JsonValueKind.Array && proxies.EnumerateArray().Any(proxy =>
            proxy.TryGetProperty("name", out var name) && name.GetString() == "quasar"
            && proxy.TryGetProperty("status", out var phase) && phase.GetString() == "running");

    private async Task ReportHealthAsync(HostedPanelState state, HttpClient local, HttpClient remote, CancellationToken ct)
    {
        bool registered = false, ready = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{adminPort}/api/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("nebula:" + state.LocalProxySecret)));
            using var response = await local.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) registered = Registered(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
            using var worker = await local.GetAsync($"http://127.0.0.1:{workerPort}/api/ready", ct);
            ready = worker.IsSuccessStatusCode;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException) { }
        var health = new HostedConnectorHealth(state.PanelId, registered, ready, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await health.SaveAsync(ct);
        using var heartbeat = new HttpRequestMessage(HttpMethod.Post, HostedPanelState.Origin + "/api/v1/connector/heartbeat")
        { Content = JsonContent.Create(new { tunnelRegistered = registered, workerReady = ready }) };
        heartbeat.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.Credential);
        using var reported = await remote.SendAsync(heartbeat, ct);
        // Entitlement/credential failures are shown by the worker's ordinary enrollment poll.
    }

    private static string ExportTrust()
    {
        var certificates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            foreach (var certificate in store.Certificates)
            { using (certificate) certificates.Add(certificate.ExportCertificatePem()); }
        }
        if (certificates.Count == 0) throw new InvalidOperationException("No trusted certificate roots; hosted access is disabled.");
        string path = Path.Combine(HostedPanelState.DirectoryPath, "trusted-roots.pem");
        var text = string.Join('\n', certificates) + "\n";
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path;
    }

    private static async Task PrivateFileAsync(string path, string content, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await file.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }

    private static async Task<string> EnsureBinaryAsync(CancellationToken ct)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Hosted connector supports Linux x64 and Windows x64.");
        bool windows = OperatingSystem.IsWindows();
        string name = windows ? "frpc.exe" : "frpc";
        string directory = Path.Combine(HostedPanelState.DirectoryPath, "frp-0.71.1");
        string binary = Path.Combine(directory, name);
        string marker = Path.Combine(directory, "verified.sha256");
        if (File.Exists(binary) && File.Exists(marker)
            && HostedPanelState.SecretEquals(File.ReadAllText(marker), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary, ct))))) return binary;
        Directory.CreateDirectory(directory);
        var archive = "frp_0.71.1_" + (windows ? "windows_amd64.zip" : "linux_amd64.tar.gz");
        var expected = windows ? "b90074b2b6a1d792a7a40f085ac6d276bcab2d1bc60201f8a86ca53ce65ce821" : "1d9875210972a5d1a62b2dc4c48df45ab0af05d3ca1b5e494a014428259df803";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CometWorks-Quasar-Nebula/1");
        var bytes = await http.GetByteArrayAsync("https://github.com/fatedier/frp/releases/download/v0.71.1/" + archive, ct);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("frpc archive checksum mismatch.");
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        string? license = null;
        if (windows)
        {
            using var zip = new ZipArchive(input);
            using var member = zip.Entries.Single(e => e.Name == name).Open();
            await member.CopyToAsync(output, ct);
            using var licenseEntry = zip.Entries.Single(e => e.Name == "LICENSE").Open();
            using var reader = new StreamReader(licenseEntry);
            license = await reader.ReadToEndAsync(ct);
        }
        else
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                if (Path.GetFileName(entry.Name) == name) await entry.DataStream!.CopyToAsync(output, ct);
                else if (Path.GetFileName(entry.Name) == "LICENSE")
                {
                    using var reader = new StreamReader(entry.DataStream!, leaveOpen: true);
                    license = await reader.ReadToEndAsync(ct);
                }
            }
        }
        if (license is not null) await File.WriteAllTextAsync(Path.Combine(directory, "LICENSE"), license, ct);
        if (output.Length == 0) throw new InvalidDataException("frpc binary missing.");
        string temporary = binary + ".tmp";
        await File.WriteAllBytesAsync(temporary, output.ToArray(), ct);
        if (!windows) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(temporary, binary, true);
        await File.WriteAllTextAsync(marker, Convert.ToHexString(SHA256.HashData(output.ToArray())), ct);
        return binary;
    }

    private static string ProcessLeasePath => Path.Combine(HostedPanelState.DirectoryPath, "connector-process.json");
    private sealed record ConnectorLease(int Pid, long StartTicks, string Binary);
    private void RetirePreviousConnector()
    {
        if (!File.Exists(ProcessLeasePath)) return;
        try
        {
            var lease = JsonSerializer.Deserialize<ConnectorLease>(File.ReadAllText(ProcessLeasePath));
            if (lease is null) return;
            var expected = Path.Combine(HostedPanelState.DirectoryPath, "frp-0.71.1", OperatingSystem.IsWindows() ? "frpc.exe" : "frpc");
            using var previous = Process.GetProcessById(lease.Pid);
            if (lease.Binary == expected && previous.StartTime.ToUniversalTime().Ticks == lease.StartTicks
                && previous.MainModule?.FileName == expected && !previous.HasExited)
                previous.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception or JsonException)
        { logger.LogDebug("Prior connector lease unavailable ({Type}).", e.GetType().Name); }
        finally
        {
            try { File.Delete(ProcessLeasePath); }
            catch (IOException) { }
        }
    }

    private void StopProcess()
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose(); process = null;
        try { File.Delete(ProcessLeasePath); } catch (IOException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (run is not null) await run;
        stop.Dispose();
    }
}
