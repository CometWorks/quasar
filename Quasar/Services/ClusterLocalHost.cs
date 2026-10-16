using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

/// <summary>
/// The local Host every Quasar install on Linux x64 has without enrollment. Quasar runs Quasar.Host
/// straight from its release, keeps its configuration and state in <c>&lt;install root&gt;/LocalHost</c>,
/// starts it when the Hosts page, cluster setup or a cluster needs it, and adopts it again after a restart.
/// It has no service unit of its own; install.sh adds one that saves its clusters at logout or shutdown.
/// </summary>
public sealed class ClusterLocalHost : IHostedService, IDisposable
{
    internal const string HostId = "local";
    internal const string StopUnitVariable = "QUASAR_HOST_STOP_UNIT";
    private const int FirstCommandPort = 18401;
    private readonly ClusterHostCatalog hosts;
    private readonly ClusterCredentialStore credentials;
    private readonly ClusterHostInstaller installer;
    private readonly ClusterHostTunnels tunnels;
    private readonly WebServiceOptions options;
    private readonly ILogger<ClusterLocalHost> logger;
    private readonly string root;
    private readonly bool supported;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private Process? process;
    private bool wanted;
    private (string Path, DateTime Written, long Length, string Sha256)? binaryHash;
    private string status = "Preparing the local Host executor.";
    private string? error;

    public ClusterLocalHost(ClusterHostCatalog hosts, ClusterCredentialStore credentials, ClusterHostInstaller installer,
        ClusterHostTunnels tunnels, WebServiceOptions options, ILogger<ClusterLocalHost> logger)
        : this(hosts, credentials, installer, tunnels, options, logger, Path.Combine(MagnetarPaths.GetQuasarDirectory(), "LocalHost"),
            OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            && Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true") { }

    internal ClusterLocalHost(ClusterHostCatalog hosts, ClusterCredentialStore credentials, ClusterHostInstaller installer,
        ClusterHostTunnels tunnels, WebServiceOptions options, ILogger<ClusterLocalHost> logger, string root, bool supported)
    {
        (this.hosts, this.credentials, this.installer, this.tunnels, this.options, this.logger, this.root, this.supported)
            = (hosts, credentials, installer, tunnels, options, logger, root, supported);
        tunnels.OfflineHostRequested += id => { if (id == HostId) Request(); };
        if (!supported)
            status = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? "Quasar has no local Host executor in a container. Enroll a Linux host machine to run cluster processes."
                : "Automatic local cluster hosting requires Linux x64. Enroll a Linux host machine to run cluster processes.";
    }

    private bool Connected => tunnels.IsConnected(HostId);
    /// <summary>False on Windows, other architectures and in a container.</summary>
    public bool Supported => supported;
    public string Status => Connected ? "Local Host executor connected." : status;
    public string? Error => Connected ? null : error;
    /// <summary>The folder with the local Host's configuration, state and log.</summary>
    public string Directory => root;
    private string ConfigPath => Path.Combine(root, "host.json");
    private string PidPath => Path.Combine(root, "host.pid");
    internal string StopScriptPath => Path.Combine(root, "stop-clusters");

    /// <summary>Starts the local Host unless it already runs. Returns at once.</summary>
    public void Request()
    {
        if (supported && !stopping.IsCancellationRequested) _ = Task.Run(() => EnsureRunningAsync(stopping.Token));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!supported) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try
            {
                // Without a cluster on this PC nothing needs the Host until someone opens Hosts or cluster setup.
                if (ReadPid() is not null || HasClusters()) await EnsureRunningAsync(stopping.Token);
                else await PrepareAsync(stopping.Token);
                await WatchAsync(stopping.Token);
            }
            catch (OperationCanceledException) { }
        });
        return Task.CompletedTask;
    }

    // Starts the Host again when it exits while Quasar runs, as Restart=on-failure did for its old unit.
    private async Task WatchAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(token))
            if (wanted && ReadPid() is null)
            {
                logger.LogWarning("The local Host executor exited; starting it again. See {Log}.", Path.Combine(root, "host.log"));
                await EnsureRunningAsync(token);
            }
    }

    internal async Task EnsureRunningAsync(CancellationToken token)
    {
        try { await gate.WaitAsync(token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (token.IsCancellationRequested || !await PrepareCoreAsync(token)) return;
            wanted = true;
            string hash = BinarySha256();
            if (ReadPid() is { } running)
            {
                if (running.Sha256 == hash) return;
                // A Host restart leaves the Gateway and nodes running; the new Host adopts them.
                logger.LogInformation("Restarting the local Host executor with the binary of this Quasar release.");
                await StopProcessAsync(running.Pid);
            }
            Launch(installer.BinaryPath, hash);
            error = null;
            status = "Local Host executor started; waiting for its connection.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException
            or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            || exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            error = exception.Message;
            status = "Local Host executor is not ready.";
            logger.LogWarning(exception, "Could not start the local Host executor.");
        }
        catch (OperationCanceledException) { }
        finally { gate.Release(); }
    }

    // Requests arrive every few seconds while the Host is offline; hash the binary only when it changed.
    private string BinarySha256()
    {
        var file = new FileInfo(installer.BinaryPath);
        if (binaryHash is not { } cached || cached.Path != file.FullName || cached.Written != file.LastWriteTimeUtc || cached.Length != file.Length)
            binaryHash = cached = (file.FullName, file.LastWriteTimeUtc, file.Length, installer.GetBinaryInfo().Sha256);
        return cached.Sha256;
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (await PrepareCoreAsync(token))
                status = "Local Host executor ready. Quasar starts it when a cluster needs it.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException
            or JsonException or UnauthorizedAccessException)
        {
            error = exception.Message;
            status = "Local Host executor is not ready.";
            logger.LogWarning(exception, "Could not prepare the local Host executor.");
        }
        finally { gate.Release(); }
    }

    // Registers the Host and writes its files, all inside the install root.
    private async Task<bool> PrepareCoreAsync(CancellationToken token)
    {
        if (!File.Exists(installer.BinaryPath))
        {
            error = $"This Quasar build does not include the Host executor ({installer.BinaryPath}). For a development build set {ClusterHostInstaller.BinaryVariable} to a published single-file Quasar.Host.";
            status = "Local Host executor is not available.";
            return false;
        }
        var host = hosts.Get(HostId) ?? await RegisterAsync(token);
        var listening = new Uri(options.ListenUrl);
        string origin = listening.Host is "0.0.0.0" or "::" or "[::]" || listening.IsLoopback
            ? new UriBuilder(listening) { Host = "127.0.0.1" }.Uri.AbsoluteUri : options.BaseUrl;
        CreatePrivateDirectory(root);
        CreatePrivateDirectory(Path.Combine(root, "state"));
        // Rewritten every time, so a changed Quasar port reaches the Host at its next start.
        WritePrivate(ConfigPath, JsonSerializer.Serialize(ClusterHostInstaller.HostConfiguration(host, ClusterHostInstaller.ValidateOrigin(origin))));
        string secrets = Path.Combine(root, "state", "credentials.json");
        string credential = credentials.Resolve(host.CredentialReference) ?? throw new InvalidOperationException("The local Host executor credential is missing.");
        if (!File.Exists(secrets))
            WritePrivate(secrets, JsonSerializer.Serialize(new Dictionary<string, string> { [host.CredentialReference] = credential }));
        else if (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(secrets))?.GetValueOrDefault(host.CredentialReference) != credential)
            throw new InvalidOperationException($"{secrets} holds a different credential for the local Host executor; its data has been preserved.");
        WritePrivate(StopScriptPath, $"#!/bin/sh\nexec {Quote(installer.BinaryPath)} stop-clusters --config {Quote(ConfigPath)}\n", executable: true);
        return true;
    }

    private async Task<EnrolledClusterHost> RegisterAsync(CancellationToken token)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
        int[] taken = hosts.GetAll().Select(h => h.CommandPort).ToArray();
        return await hosts.RegisterAsync(HostId, options.HostName + " (this PC)", SelectLocalAddress(addresses),
            SelectCommandPort(root, taken, IsFree), token);
    }

    internal static string SelectLocalAddress(IEnumerable<IPAddress> addresses) => addresses
        .Where(a => !IPAddress.IsLoopback(a) && ClusterHostCatalog.IsClusterAddress(a.ToString()))
        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .Select(a => a.ToString()).FirstOrDefault() ?? "127.0.0.1";

    // Picked once and kept. Starting from an offset derived from the install root keeps two installs
    // of the same user apart even when the other one's Host isn't running at that moment.
    internal static int SelectCommandPort(string root, IReadOnlyCollection<int> taken, Func<int, bool> isFree)
    {
        int offset = (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(root))) % 200);
        for (int i = 0; i < 1000; i++)
        {
            int port = FirstCommandPort + (offset + i) % 1000;
            if (!taken.Contains(port) && isFree(port)) return port;
        }
        throw new IOException("No free loopback port for the local Host executor's control listener.");
    }

    private static bool IsFree(int port)
    {
        try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop(); return true; }
        catch (SocketException) { return false; }
    }

    private void Launch(string binary, string hash)
    {
        string log = Path.Combine(root, "host.log");
        if (File.Exists(log) && new FileInfo(log).Length > 10 * 1024 * 1024) File.Move(log, log + ".1", true);
        // setsid keeps the Host out of the terminal's process group, so Ctrl+C on a foreground Quasar
        // doesn't reach it. It execs in place, so the PID stays the Host's.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = root };
        foreach (string argument in new[] { "-c",
            "log=$1; shift; if command -v setsid >/dev/null; then exec setsid \"$@\" >>\"$log\" 2>&1 </dev/null; fi; exec \"$@\" >>\"$log\" 2>&1 </dev/null",
            "sh", log, binary, "run", "--config", ConfigPath })
            start.ArgumentList.Add(argument);
        // install.sh puts the cluster stop unit's path into Quasar's service; empty tells the Host it has none.
        start.Environment[StopUnitVariable] = Environment.GetEnvironmentVariable(StopUnitVariable) ?? "";
        process?.Dispose();
        process = Process.Start(start) ?? throw new IOException("Could not start the local Host executor.");
        string identity = ProcessIdentity(process.Id) ?? throw new IOException("The local Host executor exited at once; see " + log + ".");
        WritePrivate(PidPath, JsonSerializer.Serialize(new HostPid(process.Id, identity, binary, hash)));
        logger.LogInformation("Started the local Host executor (PID {Pid}).", process.Id);
    }

    internal sealed record HostPid(int Pid, string Identity, string Binary, string Sha256);

    /// <summary>The recorded Host process, or null when it no longer runs.</summary>
    internal HostPid? ReadPid()
    {
        try
        {
            var record = JsonSerializer.Deserialize<HostPid>(File.ReadAllText(PidPath));
            return record is not null && ProcessIdentity(record.Pid) == record.Identity ? record : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // Boot ID plus start time since boot, as Quasar.Host records its own children, so a reused PID doesn't match.
    internal static string? ProcessIdentity(int pid)
    {
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A zombie has exited and only waits to be reaped.
            return fields.Length < 20 || fields[0] == "Z" || !ulong.TryParse(fields[19], out ulong ticks) ? null : bootId + "/" + ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when stopping the Host would leave a cluster without it: a Gateway with goal On here,
    /// or this PC takes part in a cluster whose Gateway runs on another machine.</summary>
    internal bool HasRunningClusters()
    {
        string state = Path.Combine(root, "state");
        string[] gateways = ClusterIds(Path.Combine(state, "gateways"), out bool on);
        return on || ClusterIds(Path.Combine(state, "attachments"), out _).Except(gateways, StringComparer.OrdinalIgnoreCase).Any();
    }

    private bool HasClusters() => ClusterIds(Path.Combine(root, "state", "attachments"), out _).Length > 0;

    private static string[] ClusterIds(string directory, out bool anyOn)
    {
        anyOn = false;
        if (!System.IO.Directory.Exists(directory)) return [];
        var ids = new List<string>();
        foreach (string path in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.TryGetProperty("clusterId", out var id) && id.GetString() is { } value) ids.Add(value);
                anyOn |= document.RootElement.TryGetProperty("goal", out var goal) && goal.GetString() == "On";
            }
            // An unreadable record counts as a running cluster, so Quasar doesn't stop the Host under it.
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                anyOn = true;
                ids.Add(path);
            }
        }
        return ids.ToArray();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        if (!supported) return;
        // Let a start that is already under way finish, so its Host is seen below.
        if (await gate.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)) gate.Release();
        if (ReadPid() is not { } running) return;
        bool busy = HasRunningClusters();
        if (busy && options.PreserveManagedServersOnShutdown)
        {
            logger.LogInformation("Leaving the local Host executor running for its clusters (PID {Pid}).", running.Pid);
            return;
        }
        try
        {
            if (busy)
            {
                logger.LogInformation("Saving and stopping the clusters on this PC.");
                using var stop = Process.Start(new ProcessStartInfo(StopScriptPath) { UseShellExecute = false });
                if (stop is not null) await stop.WaitForExitAsync(cancellationToken);
            }
            await StopProcessAsync(running.Pid);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not stop the local Host executor (PID {Pid}).", running.Pid);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private async Task StopProcessAsync(int pid)
    {
        string? identity = ProcessIdentity(pid);
        if (identity is not null)
        {
            kill(pid, 15);
            for (int i = 0; i < 150 && ProcessIdentity(pid) == identity; i++) await Task.Delay(100);
            if (ProcessIdentity(pid) == identity) kill(pid, 9);
        }
        File.Delete(PidPath);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!System.IO.Directory.Exists(path))
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivate(string path, string content, bool executable = false)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        string temporary = path + ".new";
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | (executable ? UnixFileMode.UserExecute : 0);
        using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = mode }))
            file.Write(Encoding.UTF8.GetBytes(content));
        File.SetUnixFileMode(temporary, mode);
        File.Move(temporary, path, true);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public void Dispose()
    {
        process?.Dispose();
        stopping.Dispose();
        gate.Dispose();
    }
}
