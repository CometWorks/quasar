using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quasar.Bootstrap;

internal static class Program
{
    private const string EnsureRunningCommand = "ensure-running";
    private const string ServeCommand = "serve";
    private const string ActivateReleaseCommand = "activate-release";
    private const string SpawnMutexName = "Quasar.Bootstrap";
    private static readonly HttpClient HealthHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    public static async Task<int> Main(string[] args)
    {
        var quiet = args.Any(static arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
        var openBrowser = args.Any(static arg => string.Equals(arg, "--open-browser", StringComparison.OrdinalIgnoreCase));
        var force = args.Any(static arg => string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase));
        var service = args.Any(static arg => string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase));
        var explicitForeground = args.Any(static arg =>
            string.Equals(arg, "--foreground", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase));
        var command = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal)) ?? EnsureRunningCommand;

        // When started from a real terminal we keep the bootstrap and worker in the
        // foreground so the user sees live logs in the console they invoked us from.
        // Detached spawn is still used for non-interactive launches (services, scripts).
        var foreground = !quiet && (explicitForeground || IsAttachedToInteractiveConsole());

        return command.ToLowerInvariant() switch
        {
            EnsureRunningCommand => await EnsureRunningAsync(quiet, openBrowser, force, foreground),
            ServeCommand => await ServeAsync(quiet, foreground, service),
            ActivateReleaseCommand => await ActivateReleaseAsync(args, quiet),
            _ => InvalidUsage(quiet),
        };
    }

    private static bool IsAttachedToInteractiveConsole()
    {
        try
        {
            return !Console.IsOutputRedirected && Environment.UserInteractive;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> EnsureRunningAsync(bool quiet, bool openBrowser = false, bool force = false, bool foreground = false)
    {
        var existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
        if (existing is not null)
        {
            if (!force)
                return Complete(existing, quiet, openBrowser);

            if (!quiet)
            {
                Console.Error.WriteLine("Warning: a Quasar server is already running.");
                Console.Error.WriteLine("Terminating existing server before relaunch (--force).");
            }

            await KillExistingServerAsync().ConfigureAwait(false);
        }

        using var spawnMutex = new Mutex(false, SpawnMutexName);
        try
        {
            spawnMutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch
        {
            //
        }

        existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
        if (existing is not null)
        {
            if (!force)
                return Complete(existing, quiet, openBrowser);

            // Another server surfaced during the mutex wait; kill it too.
            await KillExistingServerAsync().ConfigureAwait(false);
        }

        // If the port is already bound but no healthy Quasar responded, something else owns it.
        // Fail fast rather than spawning a process that will silently exit due to EADDRINUSE.
        if (!force)
        {
            var bootstrapOptions = BootstrapOptions.Create();
            if (IsPortInUse(bootstrapOptions.Port, bootstrapOptions.AdvertisedHost))
            {
                if (!quiet)
                    Console.Error.WriteLine($"Error: port {bootstrapOptions.Port} is already bound by another process and no healthy Quasar server was detected. Use --force to terminate the existing process and restart.");
                return 5;
            }
        }

        if (foreground)
        {
            if (openBrowser && !IsHeadless())
            {
                _ = Task.Run(async () =>
                {
                    var baseUrl = $"http://127.0.0.1:{BootstrapOptions.Create().Port}";
                    for (var attempt = 0; attempt < 60; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        if (await TryGetHealthyServiceUriAsync().ConfigureAwait(false) is { } ready)
                        {
                            TryOpenBrowser(ready);
                            return;
                        }
                    }
                    TryOpenBrowser(new Uri(baseUrl));
                });
            }

            return await ServeAsync(quiet, foreground: true).ConfigureAwait(false);
        }

        if (!TryBuildBootstrapLaunchSpec(out var fileName, out var arguments, out var workingDirectory))
        {
            if (!quiet)
                Console.Error.WriteLine("Quasar.Bootstrap could not locate its launcher entrypoint.");

            return 3;
        }

        StartDetachedProcess(fileName, arguments, workingDirectory);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
            if (existing is not null)
                return Complete(existing, quiet, openBrowser);
        }

        if (!quiet)
            Console.Error.WriteLine("Quasar launcher did not become healthy before timeout.");

        return 4;
    }

    private static async Task KillExistingServerAsync()
    {
        var manifest = ReadManifest();
        if (manifest is not null && manifest.ProcessId > 0)
        {
            try
            {
                var process = Process.GetProcessById(manifest.ProcessId);
                process.Kill(entireProcessTree: false);
            }
            catch
            {
                // Process already gone or access denied — that is fine.
            }
        }

        // Wait up to 15 s for the health endpoint to stop responding.
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (await TryGetHealthyServiceUriAsync().ConfigureAwait(false) is null)
                return;
        }
    }

    private static async Task<int> ServeAsync(bool quiet = false, bool foreground = false, bool service = false)
    {
        var existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
        if (existing is not null)
            return 0;

        var options = BootstrapOptions.Create();

        if (IsPortInUse(options.Port, options.AdvertisedHost))
        {
            existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
            if (existing is not null)
                return 0;

            if (!quiet)
                Console.Error.WriteLine($"Error: port {options.Port} is already bound by another process and is not a healthy Quasar server.");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            if (foreground)
            {
                logging.AddSimpleConsole(formatterOptions =>
                {
                    formatterOptions.SingleLine = true;
                    formatterOptions.TimestampFormat = "HH:mm:ss ";
                });
            }
        });

        var coordinator = new LauncherCoordinator(
            options,
            new LauncherForegroundOptions(foreground, service),
            loggerFactory.CreateLogger<LauncherCoordinator>());

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await coordinator.StartAsync(shutdown.Token).ConfigureAwait(false);

            if (!quiet)
                Console.WriteLine($"{BootstrapOptions.SupervisorName} worker launching on {options.BaseUrl}");

            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            coordinator.Dispose();
        }

        return 0;
    }

    private static bool IsPortInUse(int port, string host)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any))
        {
            address = IPAddress.Loopback;
        }

        try
        {
            using var tcp = new TcpClient();
            tcp.Connect(address, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> ActivateReleaseAsync(string[] args, bool quiet)
    {
        var fileName = GetOptionValue(args, "--file");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            if (!quiet)
                Console.Error.WriteLine("Usage: Quasar.Bootstrap activate-release --file <worker exe|dll> [--working-dir <dir>] [--args <worker args>] [--version <version>] [--quiet]");

            return 2;
        }

        var workingDirectory = GetOptionValue(args, "--working-dir");
        if (string.IsNullOrWhiteSpace(workingDirectory))
            workingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory;

        var version = GetOptionValue(args, "--version") ?? string.Empty;
        var workerArguments = GetOptionValue(args, "--args") ?? BuildDefaultWorkerArguments(fileName);
        var resolvedFileName = ResolveWorkerFileName(fileName);

        var pointer = new QuasarActiveReleasePointer
        {
            Version = version,
            FileName = resolvedFileName,
            Arguments = workerArguments,
            WorkingDirectory = workingDirectory,
            ActivatedAtUtc = DateTimeOffset.UtcNow,
        };

        WriteActiveReleasePointer(pointer);
        var ensureResult = await EnsureRunningAsync(true).ConfigureAwait(false);
        if (ensureResult != 0)
        {
            if (!quiet)
                Console.Error.WriteLine("Failed ensuring the Quasar launcher is running after release activation.");

            return ensureResult;
        }

        if (!quiet)
            Console.WriteLine("Quasar active release pointer updated.");

        return 0;
    }

    private static int Complete(Uri uri, bool quiet, bool openBrowser = false)
    {
        if (!quiet)
        {
            Console.WriteLine(BootstrapOptions.SupervisorName);
            Console.WriteLine(uri.AbsoluteUri.TrimEnd('/'));
        }

        if (openBrowser && !IsHeadless())
            TryOpenBrowser(uri);

        return 0;
    }

    private static bool IsHeadless()
    {
        if (OperatingSystem.IsWindows())
            return false;

        // On Linux/macOS, require a display server to be present.
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
               && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private static void TryOpenBrowser(Uri uri)
    {
        var url = uri.AbsoluteUri;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (TryStartBrowserCommand("xdg-open", url) ||
                    TryStartBrowserCommand("gio", $"open \"{url}\"") ||
                    TryStartBrowserCommand("sensible-browser", url))
                {
                    return;
                }
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: failure to open browser is not fatal.
        }
    }

    private static bool TryStartBrowserCommand(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int InvalidUsage(bool quiet)
    {
        if (!quiet)
            Console.Error.WriteLine("Usage: Quasar.Bootstrap [ensure-running|serve|activate-release] [options]");

        return 2;
    }

    private static async Task<Uri?> TryGetHealthyServiceUriAsync()
    {
        var manifest = ReadManifest();
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.BaseUrl))
            return null;

        if (!Uri.TryCreate(manifest.BaseUrl, UriKind.Absolute, out var baseUri))
            return null;

        try
        {
            using var response = await HealthHttpClient.GetAsync(new Uri(baseUri, "/api/health")).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? baseUri : null;
        }
        catch
        {
            return null;
        }
    }

    private static WebServiceDiscoveryManifest? ReadManifest()
    {
        try
        {
            var path = MagnetarPaths.GetWebServiceManifestPath();
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<WebServiceDiscoveryManifest>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBuildBootstrapLaunchSpec(out string fileName, out string arguments, out string workingDirectory)
    {
        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        var entryAssemblyPath = string.IsNullOrWhiteSpace(entryAssemblyName)
            ? string.Empty
            : Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.dll");
        if (!File.Exists(entryAssemblyPath))
            entryAssemblyPath = string.Empty;

        // Reject DLL paths that lack a sibling runtimeconfig.json — this happens when the
        // bootstrap is invoked from the obj/ intermediate directory after a RID-targeted build
        // moved runtimeconfig.json into the bin/<rid>/ output.  Using such a path causes the
        // dotnet host to attempt self-contained execution and fail with libhostpolicy errors.
        if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            var runtimeConfigPath = Path.ChangeExtension(entryAssemblyPath, ".runtimeconfig.json");
            if (!File.Exists(runtimeConfigPath))
                entryAssemblyPath = string.Empty;
        }

        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(entryAssemblyPath) &&
            entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(processPath) || IsDotNetHost(processPath)))
        {
            fileName = string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath;
            arguments = $"\"{entryAssemblyPath}\" {ServeCommand} --quiet";
            workingDirectory = Path.GetDirectoryName(entryAssemblyPath) ?? AppContext.BaseDirectory;
            return true;
        }

        // Only use processPath as a direct binary if it is NOT the dotnet host.
        // When processPath is the dotnet host and entryAssemblyPath is unavailable (e.g. no
        // runtimeconfig.json in the DLL's directory), spawning "dotnet serve --quiet" is wrong.
        if (!string.IsNullOrWhiteSpace(processPath) && !IsDotNetHost(processPath))
        {
            fileName = processPath;
            arguments = $"{ServeCommand} --quiet";
            workingDirectory = Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            fileName = entryAssemblyPath;
            arguments = $"{ServeCommand} --quiet";
            workingDirectory = Path.GetDirectoryName(entryAssemblyPath) ?? AppContext.BaseDirectory;
            return true;
        }

        fileName = string.Empty;
        arguments = string.Empty;
        workingDirectory = string.Empty;
        return false;
    }

    private static string? GetOptionValue(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static string BuildDefaultWorkerArguments(string fileName)
    {
        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"\"{fileName}\""
            : string.Empty;
    }

    private static string ResolveWorkerFileName(string fileName)
    {
        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? "dotnet"
            : fileName;
    }

    private static void WriteActiveReleasePointer(QuasarActiveReleasePointer pointer)
    {
        var path = MagnetarPaths.GetQuasarActiveReleasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(pointer, LauncherCoordinator.JsonOptions));
    }

    internal static void StartDetachedProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirect so the detached process's output does not bleed into the parent terminal.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
                return;

            // Drain output asynchronously so the child never blocks on a full pipe buffer.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
        }
        catch
        {
            // Spawn failure is handled by the health-check polling loop in EnsureRunningAsync timing out.
        }
    }

    private static bool IsDotNetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class BootstrapOptions
{
    public const string SupervisorName = "Quasar";

    public string Host { get; init; } = "127.0.0.1";

    public string AdvertisedHost { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 8080;

    // When true (the default), Quasar leaves managed Magnetar servers running on its
    // own shutdown — they are detached (Magnetar -daemon / setsid) and re-adopted on
    // restart. Set QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN=false to stop them on exit.
    public bool PreserveServersOnShutdown { get; init; } = true;

    public string BaseUrl => $"http://{AdvertisedHost}:{Port}";

    public string ListenUrl => $"http://{Host}:{Port}";

    public bool UpdatesEnabled { get; init; } = true;

    public string UpdatesOwner { get; init; } = "viktor-ferenczi";

    public string UpdatesRepository { get; init; } = "Quasar";

    public bool UpdatesIncludePrerelease { get; init; }

    public string LinuxWebAssetName { get; init; } = "quasar-web-linux-x64.tar.gz";

    public string LinuxBootstrapAssetName { get; init; } = "quasar-installer-linux.tar.gz";

    public string WindowsWebAssetName { get; init; } = "quasar-web-win-x64.zip";

    public string WindowsBootstrapAssetName { get; init; } = "quasar-installer-windows.zip";

    // Asset names resolved for the current operating system. Windows uses the .zip
    // assets; every other platform keeps the Linux .tar.gz assets.
    public string WebAssetName => OperatingSystem.IsWindows() ? WindowsWebAssetName : LinuxWebAssetName;

    public string BootstrapAssetName => OperatingSystem.IsWindows() ? WindowsBootstrapAssetName : LinuxBootstrapAssetName;

    public TimeSpan UpdatesCheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    public string Version { get; init; } = QuasarReleaseVersion.GetEntryAssemblyVersion();

    public static BootstrapOptions Create()
    {
        var configuration = BuildConfiguration();
        var section = configuration.GetSection("Quasar");
        if (!section.Exists())
            section = configuration.GetSection("MagnetarWeb");

        var host = section["Host"];
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";

        var portValue = section["Port"] ?? "8080";

        if (!int.TryParse(portValue, out var port) || port <= 0)
            port = 8080;

        var advertisedHost = host switch
        {
            "0.0.0.0" => "127.0.0.1",
            "*" => "127.0.0.1",
            "+" => "127.0.0.1",
            _ => host,
        };

        // Mirror the worker's WebServiceOptions key/env so the launcher and worker
        // agree on the policy and the launcher can propagate it to the worker.
        var preserveValue = Environment.GetEnvironmentVariable("QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN")
                            ?? section["PreserveManagedServersOnShutdown"]
                            ?? "true";
        if (!bool.TryParse(preserveValue, out var preserveServersOnShutdown))
            preserveServersOnShutdown = true;

        var updatesSection = section.GetSection("Updates");
        var updatesEnabledValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_ENABLED")
                                  ?? updatesSection["Enabled"]
                                  ?? "true";
        if (!bool.TryParse(updatesEnabledValue, out var updatesEnabled))
            updatesEnabled = true;

        var includePrereleaseValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_INCLUDE_PRERELEASE")
                                     ?? updatesSection["IncludePrerelease"]
                                     ?? "false";
        if (!bool.TryParse(includePrereleaseValue, out var includePrerelease))
            includePrerelease = false;

        var intervalValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_CHECK_INTERVAL_SECONDS")
                            ?? updatesSection["CheckIntervalSeconds"];
        if (!int.TryParse(intervalValue, out var intervalSeconds) || intervalSeconds < 60)
            intervalSeconds = 900;

        return new BootstrapOptions
        {
            Host = host,
            AdvertisedHost = advertisedHost,
            Port = port,
            PreserveServersOnShutdown = preserveServersOnShutdown,
            UpdatesEnabled = updatesEnabled,
            UpdatesOwner = Environment.GetEnvironmentVariable("QUASAR_UPDATES_OWNER")
                           ?? updatesSection["Owner"]
                           ?? "viktor-ferenczi",
            UpdatesRepository = Environment.GetEnvironmentVariable("QUASAR_UPDATES_REPOSITORY")
                                ?? updatesSection["Repository"]
                                ?? "Quasar",
            UpdatesIncludePrerelease = includePrerelease,
            UpdatesCheckInterval = TimeSpan.FromSeconds(intervalSeconds),
            LinuxWebAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_LINUX_WEB_ASSET")
                                ?? updatesSection["LinuxWebAssetName"]
                                ?? "quasar-web-linux-x64.tar.gz",
            LinuxBootstrapAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET")
                                       ?? updatesSection["LinuxBootstrapAssetName"]
                                       ?? "quasar-installer-linux.tar.gz",
            WindowsWebAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_WINDOWS_WEB_ASSET")
                                  ?? updatesSection["WindowsWebAssetName"]
                                  ?? "quasar-web-win-x64.zip",
            WindowsBootstrapAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_WINDOWS_BOOTSTRAP_ASSET")
                                        ?? updatesSection["WindowsBootstrapAssetName"]
                                        ?? "quasar-installer-windows.zip",
        };
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? "Production";
        var builder = new ConfigurationBuilder();

        foreach (var directory in EnumerateConfigurationDirectories())
        {
            builder.AddJsonFile(Path.Combine(directory, "appsettings.json"), optional: true, reloadOnChange: false);
            builder.AddJsonFile(Path.Combine(directory, $"appsettings.{environmentName}.json"), optional: true, reloadOnChange: false);
        }

        return builder.Build();
    }

    private static IEnumerable<string> EnumerateConfigurationDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "WebService");

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var sourceQuasar = Path.Combine(directory.FullName, "Quasar");
            if (File.Exists(Path.Combine(sourceQuasar, "appsettings.json")))
                yield return sourceQuasar;
        }

        yield return MagnetarPaths.GetQuasarDirectory();
    }
}

internal sealed class LauncherCoordinator : IHostedService, IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly BootstrapOptions _options;
    private readonly LauncherForegroundOptions _foregroundOptions;
    private readonly ILogger<LauncherCoordinator> _logger;
    private readonly HttpClient _healthClient;
    private readonly HttpClient _downloadClient;
    private readonly SemaphoreSlim _activationLock = new(1, 1);
    private readonly object _sync = new();
    private readonly string _workerId = Guid.NewGuid().ToString("N");
    private readonly string _launcherToken = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;
    private CancellationTokenSource? _bootstrapUpdateMonitor;
    private Task? _bootstrapUpdateMonitorTask;
    private FileSystemWatcher? _bootstrapUpdateRequestWatcher;
    private CancellationTokenSource? _bootstrapUpdateRequestDebounce;
    private readonly SemaphoreSlim _bootstrapUpdateLock = new(1, 1);
    private WorkerProcessHandle? _currentWorker;
    private bool _isStopping;
    private bool _isRestartingForBootstrapUpdate;

    public LauncherCoordinator(BootstrapOptions options, LauncherForegroundOptions foregroundOptions, ILogger<LauncherCoordinator> logger)
    {
        _options = options;
        _foregroundOptions = foregroundOptions;
        _logger = logger;
        _healthClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        _downloadClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar.Bootstrap");
    }

    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _currentWorker is not null && !_currentWorker.Process.HasExited;
            }
        }
    }

    public object GetHealthPayload()
    {
        var workerVersion = string.Empty;
        var workerBaseUrl = string.Empty;

        lock (_sync)
        {
            if (_currentWorker is not null)
            {
                workerVersion = _currentWorker.Release.Version;
                workerBaseUrl = _currentWorker.BaseUri.AbsoluteUri.TrimEnd('/');
            }
        }

        return new
        {
            status = IsReady ? "ok" : "starting",
            workerId = _workerId,
            hostId = Environment.MachineName.ToLowerInvariant(),
            hostName = Environment.MachineName,
            baseUrl = _options.BaseUrl,
            activeWorkerVersion = workerVersion,
            activeWorkerBaseUrl = workerBaseUrl,
        };
    }

    public WebServiceDiscoveryManifest GetManifest()
    {
        return new WebServiceDiscoveryManifest
        {
            WorkerId = _workerId,
            HostId = Environment.MachineName.ToLowerInvariant(),
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            BaseUrl = _options.BaseUrl,
            StartedAtUtc = _startedAtUtc,
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Quasar...");
        Directory.CreateDirectory(MagnetarPaths.GetWebServiceDirectory());
        Directory.CreateDirectory(MagnetarPaths.GetQuasarUpdatesDirectory());
        await EnsureInitialWebReleaseAvailableAsync(cancellationToken).ConfigureAwait(false);
        EnsureActiveReleasePointerExists();
        await ActivateCurrentReleaseAsync(force: false, cancellationToken).ConfigureAwait(false);
        StartWatchingReleasePointer();
        StartBootstrapUpdateMonitor();
        StartWatchingBootstrapUpdateRequests();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _bootstrapUpdateMonitor?.Cancel();
        _bootstrapUpdateRequestWatcher?.Dispose();
        _bootstrapUpdateRequestDebounce?.Cancel();
        _bootstrapUpdateRequestDebounce?.Dispose();

        WorkerProcessHandle? worker;
        lock (_sync)
        {
            worker = _currentWorker;
            _currentWorker = null;
        }

        if (worker is not null)
            await DrainAndRetireWorkerAsync(worker, TimeSpan.Zero, stopManagedServers: !_options.PreserveServersOnShutdown, cancellationToken);

        if (_bootstrapUpdateMonitorTask is not null)
        {
            try
            {
                await _bootstrapUpdateMonitorTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _bootstrapUpdateMonitor?.Cancel();
        _bootstrapUpdateMonitor?.Dispose();
        _bootstrapUpdateRequestWatcher?.Dispose();
        _bootstrapUpdateRequestDebounce?.Cancel();
        _bootstrapUpdateRequestDebounce?.Dispose();
        _activationLock.Dispose();
        _bootstrapUpdateLock.Dispose();
        _healthClient.Dispose();
        _downloadClient.Dispose();
    }

    private void StartBootstrapUpdateMonitor()
    {
        if (!_options.UpdatesEnabled || (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()))
            return;

        _bootstrapUpdateMonitor = new CancellationTokenSource();
        _bootstrapUpdateMonitorTask = Task.Run(
            () => RunBootstrapUpdateMonitorAsync(_bootstrapUpdateMonitor.Token),
            CancellationToken.None);
    }

    private void StartWatchingBootstrapUpdateRequests()
    {
        if (!_options.UpdatesEnabled || (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()))
            return;

        var path = MagnetarPaths.GetQuasarBootstrapUpdateRequestPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        _bootstrapUpdateRequestWatcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(path),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
        };

        _bootstrapUpdateRequestWatcher.Changed += HandleBootstrapUpdateRequestChanged;
        _bootstrapUpdateRequestWatcher.Created += HandleBootstrapUpdateRequestChanged;
        _bootstrapUpdateRequestWatcher.Renamed += HandleBootstrapUpdateRequestChanged;
        _bootstrapUpdateRequestWatcher.EnableRaisingEvents = true;

        if (File.Exists(path))
            QueueBootstrapUpdateRequest();
    }

    private void HandleBootstrapUpdateRequestChanged(object sender, FileSystemEventArgs args)
    {
        QueueBootstrapUpdateRequest();
    }

    private void QueueBootstrapUpdateRequest()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _bootstrapUpdateRequestDebounce?.Cancel();
            _bootstrapUpdateRequestDebounce?.Dispose();
            _bootstrapUpdateRequestDebounce = new CancellationTokenSource();
            debounce = _bootstrapUpdateRequestDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), debounce.Token);
                if (!TryConsumeBootstrapUpdateRequest())
                    return;

                _logger.LogInformation("Bootstrap update activation requested by Quasar worker.");
                await TryUpgradeBootstrapAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (debounce.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Requested Bootstrap update activation failed.");
            }
        }, CancellationToken.None);
    }

    private async Task RunBootstrapUpdateMonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await TryUpgradeBootstrapAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Bootstrap update check failed.");
                }

                await Task.Delay(_options.UpdatesCheckInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Bootstrap update monitor stopped unexpectedly.");
        }
    }

    private async Task TryUpgradeBootstrapAsync(CancellationToken cancellationToken)
    {
        if (_isStopping || _isRestartingForBootstrapUpdate)
            return;

        await _bootstrapUpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStopping || _isRestartingForBootstrapUpdate)
                return;

            var release = await GetLatestReleaseWithAssetAsync(_options.BootstrapAssetName, cancellationToken).ConfigureAwait(false);
            if (release is null)
                return;

            var version = QuasarReleaseVersion.Normalize(release.TagName);
            if (!IsNewerVersion(version, _options.Version))
                return;

            var asset = release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, _options.BootstrapAssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return;

            var checksums = await GetChecksumsAsync(release, cancellationToken).ConfigureAwait(false);
            var cacheDirectory = MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory();
            Directory.CreateDirectory(cacheDirectory);
            var archivePath = Path.Combine(cacheDirectory, asset.Name);
            var extractDirectory = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), $"Bootstrap-{version}");

            if (Directory.Exists(extractDirectory))
                Directory.Delete(extractDirectory, recursive: true);
            Directory.CreateDirectory(extractDirectory);

            using (var response = await _downloadClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var archiveFile = File.Create(archivePath);
                await response.Content.CopyToAsync(archiveFile, cancellationToken).ConfigureAwait(false);
            }

            await VerifySha256Async(archivePath, GetChecksum(checksums, asset.Name), cancellationToken).ConfigureAwait(false);
            ExtractArchive(archivePath, extractDirectory);
            TryDeleteFile(archivePath);

            var payloadDirectory = ResolveBootstrapPayloadDirectory(extractDirectory);
            var replacement = Path.Combine(payloadDirectory, LauncherExecutableFileName);
            if (!File.Exists(replacement))
                throw new InvalidOperationException($"Bootstrap update archive did not contain executable '{replacement}'.");

            var installedLauncher = Path.Combine(AppContext.BaseDirectory, LauncherExecutableFileName);
            if (await FilesHaveSameSha256Async(replacement, installedLauncher, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Bootstrap release {Version} is already installed at {Path}; skipping worker drain and launcher restart.",
                    version,
                    installedLauncher);
                return;
            }

            ApplyBootstrapUpdate(payloadDirectory, AppContext.BaseDirectory);
            _logger.LogInformation("Installed Bootstrap update {Version}; restarting Bootstrap.", version);
            _isRestartingForBootstrapUpdate = true;

            WorkerProcessHandle? worker;
            lock (_sync)
            {
                worker = _currentWorker;
                _currentWorker = null;
            }

            if (worker is not null)
                await DrainAndRetireWorkerAsync(worker, TimeSpan.FromSeconds(20), stopManagedServers: false, cancellationToken).ConfigureAwait(false);

            RestartBootstrap();
        }
        finally
        {
            _bootstrapUpdateLock.Release();
        }
    }

    // Worker and launcher are both named Quasar (Linux/macOS) or Quasar.exe (Windows).
    private static string LauncherExecutableFileName =>
        OperatingSystem.IsWindows() ? "Quasar.exe" : "Quasar";

    private static string ResolveBootstrapPayloadDirectory(string extractDirectory)
    {
        if (File.Exists(Path.Combine(extractDirectory, LauncherExecutableFileName)))
            return extractDirectory;

        var childDirectories = Directory.EnumerateDirectories(extractDirectory).Take(2).ToArray();
        if (childDirectories.Length == 1 &&
            File.Exists(Path.Combine(childDirectories[0], LauncherExecutableFileName)))
        {
            return childDirectories[0];
        }

        return extractDirectory;
    }

    // Linux runs under systemd, which restarts the unit when Bootstrap exits with a
    // failure code. Windows has no systemd: spawn a detached replacement launcher from
    // the freshly installed binary, then exit cleanly so the Scheduled Task keep-alive
    // (which only restarts on failure) does not also start a second instance.
    private void RestartBootstrap()
    {
        if (OperatingSystem.IsWindows())
        {
            var launcherPath = Path.Combine(AppContext.BaseDirectory, LauncherExecutableFileName);
            if (File.Exists(launcherPath))
            {
                _logger.LogInformation("Spawning detached replacement launcher {Path}.", launcherPath);
                var serviceFlag = _foregroundOptions.IsService ? " --service" : "";
                Program.StartDetachedProcess(launcherPath, $"serve --quiet{serviceFlag}", AppContext.BaseDirectory);
                Environment.Exit(0);
                return;
            }

            _logger.LogWarning("Replacement launcher not found at {Path}; exiting for external supervisor restart.", launcherPath);
        }

        Environment.Exit(75);
    }

    private static void ApplyBootstrapUpdate(string sourceDirectory, string installDirectory)
    {
        installDirectory = Path.GetFullPath(installDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            if (string.Equals(relativePath, "appsettings.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var destinationPath = Path.GetFullPath(Path.Combine(installDirectory, relativePath));
            var installRoot = Path.GetFullPath(installDirectory + Path.DirectorySeparatorChar);
            if (!destinationPath.StartsWith(installRoot, StringComparison.Ordinal))
                throw new InvalidOperationException($"Bootstrap update entry escapes install directory: {relativePath}");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (string.Equals(Path.GetFileName(destinationPath), LauncherExecutableFileName, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(destinationPath))
            {
                // On Windows the running launcher .exe can be renamed but not deleted;
                // moving it aside lets us drop the replacement in place.
                var backupPath = destinationPath + ".previous";
                TryDeleteFile(backupPath);
                File.Move(destinationPath, backupPath);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
            if (string.Equals(Path.GetFileName(destinationPath), LauncherExecutableFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(destinationPath), "install.sh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(destinationPath), "uninstall.sh", StringComparison.OrdinalIgnoreCase))
            {
                EnsureExecutableBit(destinationPath);
            }
        }
    }

    private static async Task<bool> FilesHaveSameSha256Async(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
            return false;

        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
            return false;

        var leftHash = await ComputeSha256Async(leftPath, cancellationToken).ConfigureAwait(false);
        var rightHash = await ComputeSha256Async(rightPath, cancellationToken).ConfigureAwait(false);
        return leftHash.SequenceEqual(rightHash);
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitialWebReleaseAvailableAsync(CancellationToken cancellationToken)
    {
        var existing = ReadActiveReleasePointer();
        if (existing is not null && IsReleasePointerUsable(existing, allowExternalPointer: !IsServiceMode()))
        {
            TryMigrateStagedActiveRelease(existing);
            return;
        }

        if (FindPackagedWorkerCandidate() is not null)
            return;

        if (!_options.UpdatesEnabled)
            return;

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            _logger.LogWarning("Quasar web auto-download is not supported on this platform.");
            return;
        }

        try
        {
            var release = await GetLatestReleaseWithAssetAsync(_options.WebAssetName, cancellationToken).ConfigureAwait(false);
            var asset = release?.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, _options.WebAssetName, StringComparison.OrdinalIgnoreCase));
            var checksums = release is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : await GetChecksumsAsync(release, cancellationToken).ConfigureAwait(false);
            if (release is null || asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                _logger.LogWarning("No Quasar web release asset named {AssetName} was found.", _options.WebAssetName);
                return;
            }

            var version = QuasarReleaseVersion.Normalize(release.TagName);
            var releaseDirectory = MagnetarPaths.GetQuasarManagedWebReleaseDirectory(version);
            var workerPath = Path.Combine(releaseDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName);
            if (!File.Exists(workerPath))
            {
                if (Directory.Exists(releaseDirectory))
                    Directory.Delete(releaseDirectory, recursive: true);

                Directory.CreateDirectory(releaseDirectory);
                var archivePath = Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory(), asset.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

                _logger.LogInformation("Downloading Quasar web release {Version}...", version);
                using (var response = await _downloadClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var archiveFile = File.Create(archivePath);
                    await response.Content.CopyToAsync(archiveFile, cancellationToken).ConfigureAwait(false);
                }

                await VerifySha256Async(archivePath, GetChecksum(checksums, asset.Name), cancellationToken).ConfigureAwait(false);
                ExtractArchive(archivePath, releaseDirectory);
                TryDeleteFile(archivePath);
            }

            QuasarWebReleaseLayout.ValidateDirectory(releaseDirectory);

            EnsureExecutableBit(workerPath);
            WriteActiveReleasePointer(new QuasarActiveReleasePointer
            {
                Version = version,
                FileName = workerPath,
                Arguments = string.Empty,
                WorkingDirectory = releaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            });
            CleanupStagedUpdates(releaseDirectory);
            _logger.LogInformation("Downloaded initial Quasar web release {Version}.", version);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed downloading initial Quasar web release.");
        }
    }

    private async Task<GitHubRelease?> GetLatestReleaseWithAssetAsync(string assetName, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{_options.UpdatesOwner}/{_options.UpdatesRepository}/releases?per_page=100";
        using var response = await _downloadClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return releases?
            .Where(release => !release.Draft)
            .Where(release => _options.UpdatesIncludePrerelease || !release.Prerelease)
            .FirstOrDefault(release => release.Assets.Any(asset =>
                string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<IReadOnlyDictionary<string, string>> GetChecksumsAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        var asset = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var text = await _downloadClient.GetStringAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        return ParseSha256Sums(text);
    }

    private static Dictionary<string, string> ParseSha256Sums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[0].Length == 64)
                result[parts[^1]] = parts[0];
        }

        return result;
    }

    private static string GetChecksum(IReadOnlyDictionary<string, string> checksums, string assetName) =>
        checksums.TryGetValue(assetName, out var checksum) ? checksum : string.Empty;

    private static async Task VerifySha256Async(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidOperationException("Release asset has no SHA256SUMS entry.");

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SHA256 mismatch for {Path.GetFileName(path)}.");
    }

    private static bool IsNewerVersion(string candidate, string current) =>
        QuasarReleaseVersion.IsNewer(candidate, current);

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
            return;
        }

        throw new InvalidOperationException($"Unsupported Quasar web archive format: {archivePath}");
    }

    private static void EnsureExecutableBit(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private void EnsureActiveReleasePointerExists()
    {
        var existing = ReadActiveReleasePointer();
        if (existing is not null && IsReleasePointerUsable(existing, allowExternalPointer: !IsServiceMode()))
            return;

        if (existing is not null)
        {
            _logger.LogWarning(
                "Ignoring stale or unusable Quasar worker release pointer at {Path}.",
                MagnetarPaths.GetQuasarActiveReleasePath());
        }

        if (!TryBuildInitialReleasePointer(out var pointer))
        {
            _logger.LogWarning("Quasar launcher could not determine an initial worker release pointer.");
            return;
        }

        WriteActiveReleasePointer(pointer);
    }

    private void StartWatchingReleasePointer()
    {
        var directory = Path.GetDirectoryName(MagnetarPaths.GetQuasarActiveReleasePath())!;
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(MagnetarPaths.GetQuasarActiveReleasePath()),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
        };

        _watcher.Changed += HandleReleasePointerChanged;
        _watcher.Created += HandleReleasePointerChanged;
        _watcher.Renamed += HandleReleasePointerChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void HandleReleasePointerChanged(object sender, FileSystemEventArgs args)
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _reloadDebounce?.Cancel();
            _reloadDebounce?.Dispose();
            _reloadDebounce = new CancellationTokenSource();
            debounce = _reloadDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), debounce.Token);
                await ActivateCurrentReleaseAsync(force: false, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task ActivateCurrentReleaseAsync(bool force, CancellationToken cancellationToken)
    {
        var pointer = ReadActiveReleasePointer();
        if (pointer is null)
            return;

        pointer = Normalize(pointer);

        await _activationLock.WaitAsync(cancellationToken);
        try
        {
            WorkerProcessHandle? current;
            lock (_sync)
            {
                current = _currentWorker;
                if (!force &&
                    current is not null &&
                    !current.Process.HasExited &&
                    IsSameRelease(current.Release, pointer))
                {
                    return;
                }
            }

            if (current is not null && !current.Process.HasExited)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_currentWorker, current))
                        _currentWorker = null;
                }

                await DrainAndRetireWorkerAsync(
                    current,
                    TimeSpan.FromSeconds(2),
                    stopManagedServers: false,
                    cancellationToken).ConfigureAwait(false);
            }

            var nextWorker = await StartWorkerAsync(pointer, cancellationToken).ConfigureAwait(false);
            if (nextWorker is null)
            {
                if (current is not null && !IsSameRelease(current.Release, pointer))
                    _ = Task.Run(() => ActivateSpecificReleaseAsync(current.Release, CancellationToken.None), CancellationToken.None);

                return;
            }

            lock (_sync)
            {
                _currentWorker = nextWorker;
            }

            CleanupInactiveManagedWebReleases(pointer.WorkingDirectory);
            _logger.LogInformation("Activated Quasar worker version {Version} at {BaseUri}.", pointer.Version, nextWorker.BaseUri);
        }
        finally
        {
            _activationLock.Release();
        }
    }

    private async Task ActivateSpecificReleaseAsync(QuasarActiveReleasePointer pointer, CancellationToken cancellationToken)
    {
        await _activationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var worker = await StartWorkerAsync(pointer, cancellationToken).ConfigureAwait(false);
            if (worker is null)
                return;

            lock (_sync)
            {
                _currentWorker = worker;
            }

            WriteActiveReleasePointer(pointer);
            _logger.LogInformation("Rolled Quasar worker back to version {Version}.", pointer.Version);
        }
        finally
        {
            _activationLock.Release();
        }
    }

    private async Task<WorkerProcessHandle?> StartWorkerAsync(QuasarActiveReleasePointer pointer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pointer.FileName))
            return null;

        var resolvedFileName = pointer.FileName.Trim();
        var isDotNetHost = string.Equals(Path.GetFileNameWithoutExtension(resolvedFileName), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (!isDotNetHost && !File.Exists(resolvedFileName))
        {
            _logger.LogWarning("Quasar worker binary does not exist at {Path}.", resolvedFileName);
            return null;
        }

        var workerBaseUri = new Uri(_options.BaseUrl);
        var workerWorkingDirectory = string.IsNullOrWhiteSpace(pointer.WorkingDirectory)
            ? Path.GetDirectoryName(resolvedFileName) ?? AppContext.BaseDirectory
            : pointer.WorkingDirectory;
        SyncInstallAppSettingsToWorker(workerWorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedFileName,
            Arguments = pointer.Arguments,
            WorkingDirectory = workerWorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = _foregroundOptions.IsForeground,
            RedirectStandardError = _foregroundOptions.IsForeground,
        };

        startInfo.Environment["QUASAR_OPEN_BROWSER_ON_START"] = "false";
        startInfo.Environment["QUASAR_MODE"] = "service";
        startInfo.Environment["QUASAR_LAUNCHER_TOKEN"] = _launcherToken;
        startInfo.Environment["QUASAR_BOOTSTRAP_VERSION"] = _options.Version;
        startInfo.Environment["QUASAR_INSTALL_DIR"] = AppContext.BaseDirectory;
        startInfo.Environment["QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN"] = _options.PreserveServersOnShutdown ? "true" : "false";
        if (_foregroundOptions.IsForeground)
            startInfo.Environment["QUASAR_CONSOLE_LOGGING"] = "true";

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Process.Start returned null for the Quasar worker.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed starting Quasar worker from {Path}.", resolvedFileName);
            return null;
        }

        var worker = new WorkerProcessHandle(process, workerBaseUri, pointer);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => HandleWorkerExited(worker);

        if (_foregroundOptions.IsForeground)
        {
            _ = PumpWorkerStreamAsync(process.StandardOutput, Console.Out);
            _ = PumpWorkerStreamAsync(process.StandardError, Console.Error);
        }

        var healthy = await WaitForWorkerHealthyAsync(worker, cancellationToken);
        if (healthy)
            return worker;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
        }
        catch
        {
            //
        }

        process.Dispose();
        return null;
    }

    private static void SyncInstallAppSettingsToWorker(string workerDirectory)
    {
        if (string.IsNullOrWhiteSpace(workerDirectory))
            return;

        var sourcePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var destinationPath = Path.Combine(workerDirectory, "appsettings.json");
        if (!File.Exists(sourcePath) || IsSamePath(sourcePath, destinationPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
        catch
        {
        }
    }

    private async Task<bool> WaitForWorkerHealthyAsync(WorkerProcessHandle worker, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (worker.Process.HasExited)
            {
                _logger.LogWarning("Quasar worker exited with code {ExitCode} before becoming healthy.", SafeGetExitCode(worker.Process));
                return false;
            }

            try
            {
                using var response = await _healthClient.GetAsync(new Uri(worker.BaseUri, "/api/health"), cancellationToken);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                //
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        _logger.LogWarning("Timed out waiting for Quasar worker at {BaseUri} to become healthy.", worker.BaseUri);
        return false;
    }

    private async Task DrainAndRetireWorkerAsync(
        WorkerProcessHandle worker,
        TimeSpan graceDelay,
        bool stopManagedServers,
        CancellationToken cancellationToken)
    {
        try
        {
            var drainUri = new Uri(worker.BaseUri, $"/api/internal/drain?delaySeconds={(int)Math.Round(graceDelay.TotalSeconds)}&stopServers={stopManagedServers.ToString().ToLowerInvariant()}");
            using var request = new HttpRequestMessage(HttpMethod.Post, drainUri);
            request.Headers.Add("X-Quasar-Launcher-Token", _launcherToken);
            using var response = await _healthClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Quasar worker drain request returned {StatusCode}.", response.StatusCode);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed requesting graceful drain for Quasar worker at {BaseUri}.", worker.BaseUri);
        }

        try
        {
            if (stopManagedServers)
            {
                await worker.Process.WaitForExitAsync(cancellationToken);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(graceDelay + TimeSpan.FromSeconds(30));
                await worker.Process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!stopManagedServers && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!worker.Process.HasExited)
                    worker.Process.Kill(entireProcessTree: false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed force-retiring Quasar worker at {BaseUri}.", worker.BaseUri);
            }
        }
        finally
        {
            worker.Process.Dispose();
        }
    }

    private void HandleWorkerExited(WorkerProcessHandle worker)
    {
        var exitCode = SafeGetExitCode(worker.Process);
        var shouldRestart = false;

        if (TryConsumeLauncherShutdownRequest())
        {
            _logger.LogInformation("Quasar worker requested launcher shutdown; exiting without restarting worker.");
            Environment.Exit(0);
            return;
        }

        lock (_sync)
        {
            if (!_isStopping && ReferenceEquals(_currentWorker, worker))
            {
                _currentWorker = null;
                shouldRestart = true;
            }
        }

        if (shouldRestart)
            _logger.LogWarning("Quasar worker at {BaseUri} exited with code {ExitCode}. Restarting active release.", worker.BaseUri, exitCode);
        else
            _logger.LogInformation("Quasar worker at {BaseUri} exited with code {ExitCode}.", worker.BaseUri, exitCode);

        if (shouldRestart)
            _ = Task.Run(() => ActivateCurrentReleaseAsync(force: true, CancellationToken.None), CancellationToken.None);
    }

    private static int SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryConsumeLauncherShutdownRequest()
    {
        var path = GetLauncherShutdownRequestPath();
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
        }
        catch
        {
        }

        return true;
    }

    private static bool TryConsumeBootstrapUpdateRequest()
    {
        var path = MagnetarPaths.GetQuasarBootstrapUpdateRequestPath();
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
        }
        catch
        {
        }

        return true;
    }

    private static string GetLauncherShutdownRequestPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "launcher-shutdown-request");

    private static QuasarActiveReleasePointer Normalize(QuasarActiveReleasePointer pointer)
    {
        return new QuasarActiveReleasePointer
        {
            Version = pointer.Version.Trim(),
            FileName = pointer.FileName.Trim(),
            Arguments = pointer.Arguments,
            WorkingDirectory = pointer.WorkingDirectory.Trim(),
            ActivatedAtUtc = pointer.ActivatedAtUtc == default ? DateTimeOffset.UtcNow : pointer.ActivatedAtUtc,
        };
    }

    private static bool IsSameRelease(QuasarActiveReleasePointer left, QuasarActiveReleasePointer right)
    {
        return string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Arguments, right.Arguments, StringComparison.Ordinal)
               && string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static QuasarActiveReleasePointer? ReadActiveReleasePointer()
    {
        try
        {
            var path = MagnetarPaths.GetQuasarActiveReleasePath();
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<QuasarActiveReleasePointer>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteActiveReleasePointer(QuasarActiveReleasePointer pointer)
    {
        var path = MagnetarPaths.GetQuasarActiveReleasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(pointer, JsonOptions));
    }

    private static bool TryMigrateStagedActiveRelease(QuasarActiveReleasePointer pointer)
    {
        pointer = Normalize(pointer);
        var sourceDirectory = string.IsNullOrWhiteSpace(pointer.WorkingDirectory)
            ? Path.GetDirectoryName(pointer.FileName) ?? string.Empty
            : pointer.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(sourceDirectory) ||
            !IsPathUnder(sourceDirectory, MagnetarPaths.GetQuasarStagingDirectory()))
        {
            return false;
        }

        var version = QuasarReleaseVersion.Normalize(string.IsNullOrWhiteSpace(pointer.Version)
            ? Path.GetFileName(sourceDirectory)
            : pointer.Version);
        var activeDirectory = MagnetarPaths.GetQuasarManagedWebReleaseDirectory(version);
        if (IsSamePath(sourceDirectory, activeDirectory))
            return false;

        try
        {
            if (Directory.Exists(activeDirectory))
                Directory.Delete(activeDirectory, recursive: true);

            CopyDirectory(sourceDirectory, activeDirectory);
            QuasarWebReleaseLayout.ValidateDirectory(activeDirectory);
            var workerPath = Path.Combine(activeDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName);
            EnsureExecutableBit(workerPath);

            WriteActiveReleasePointer(new QuasarActiveReleasePointer
            {
                Version = version,
                FileName = workerPath,
                Arguments = string.Empty,
                WorkingDirectory = activeDirectory,
                ActivatedAtUtc = pointer.ActivatedAtUtc == default ? DateTimeOffset.UtcNow : pointer.ActivatedAtUtc,
            });
            CleanupStagedUpdates(activeDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupStagedUpdates(string activeWorkingDirectory)
    {
        var stagingDirectory = MagnetarPaths.GetQuasarStagingDirectory();
        if (!Directory.Exists(stagingDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(stagingDirectory))
        {
            if (IsSamePath(directory, activeWorkingDirectory))
                continue;

            TryDeleteDirectory(directory);
        }
    }

    private static void CleanupInactiveManagedWebReleases(string activeWorkingDirectory)
    {
        var releasesDirectory = MagnetarPaths.GetQuasarManagedWebServiceDirectory();
        if (!Directory.Exists(releasesDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(releasesDirectory))
        {
            if (IsSamePath(directory, activeWorkingDirectory))
                continue;

            TryDeleteDirectory(directory);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool TryBuildInitialReleasePointer(out QuasarActiveReleasePointer pointer)
    {
        var envExe = FirstExistingFile("QUASAR_WEB_EXE", "MAGNETAR_WEB_EXE");
        if (!string.IsNullOrWhiteSpace(envExe))
        {
            pointer = new QuasarActiveReleasePointer
            {
                FileName = envExe,
                Arguments = string.Empty,
                WorkingDirectory = Path.GetDirectoryName(envExe) ?? AppContext.BaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }

        var envDll = FirstExistingFile("QUASAR_WEB_DLL", "MAGNETAR_WEB_DLL");
        if (!string.IsNullOrWhiteSpace(envDll))
        {
            pointer = new QuasarActiveReleasePointer
            {
                FileName = "dotnet",
                Arguments = $"\"{envDll}\"",
                WorkingDirectory = Path.GetDirectoryName(envDll) ?? AppContext.BaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }

        var packagedWorker = FindPackagedWorkerCandidate();
        if (!string.IsNullOrWhiteSpace(packagedWorker))
        {
            pointer = new QuasarActiveReleasePointer
            {
                FileName = packagedWorker,
                Arguments = string.Empty,
                WorkingDirectory = Path.GetDirectoryName(packagedWorker) ?? AppContext.BaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }

        var candidateDll = FindWorkerCandidate("Quasar.dll");
        if (!string.IsNullOrWhiteSpace(candidateDll))
        {
            pointer = new QuasarActiveReleasePointer
            {
                FileName = "dotnet",
                Arguments = $"\"{candidateDll}\"",
                WorkingDirectory = Path.GetDirectoryName(candidateDll) ?? AppContext.BaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }

        var candidateExe = FindWorkerCandidate("Quasar.exe");
        if (!string.IsNullOrWhiteSpace(candidateExe))
        {
            pointer = new QuasarActiveReleasePointer
            {
                FileName = candidateExe,
                Arguments = string.Empty,
                WorkingDirectory = Path.GetDirectoryName(candidateExe) ?? AppContext.BaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }

        pointer = new QuasarActiveReleasePointer();
        return false;
    }

    private static string? FirstExistingFile(params string[] variableNames)
    {
        foreach (var variableName in variableNames)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                return value;
        }

        return null;
    }

    private static bool IsReleasePointerUsable(QuasarActiveReleasePointer pointer, bool allowExternalPointer)
    {
        pointer = Normalize(pointer);
        if (string.IsNullOrWhiteSpace(pointer.FileName))
            return false;

        var isDotNetHost = string.Equals(Path.GetFileNameWithoutExtension(pointer.FileName), "dotnet", StringComparison.OrdinalIgnoreCase);
        string workerPath;
        if (isDotNetHost)
        {
            if (!TryGetDotNetAssemblyArgument(pointer.Arguments, out var assemblyPath) ||
                !File.Exists(assemblyPath) ||
                IsCurrentBootstrapAssembly(assemblyPath))
            {
                return false;
            }

            workerPath = assemblyPath;
        }
        else
        {
            if (!File.Exists(pointer.FileName) || IsCurrentBootstrapExecutable(pointer.FileName))
                return false;

            workerPath = pointer.FileName;
        }

        return allowExternalPointer ||
               IsKnownReleasePath(workerPath) ||
               IsKnownReleasePath(pointer.WorkingDirectory) ||
               IsExplicitEnvironmentWorker(workerPath);
    }

    private static bool TryGetDotNetAssemblyArgument(string arguments, out string assemblyPath)
    {
        assemblyPath = string.Empty;
        var trimmed = arguments.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (trimmed[0] == '"')
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
                return false;

            assemblyPath = trimmed[1..endQuote];
            return true;
        }

        var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        assemblyPath = separator < 0 ? trimmed : trimmed[..separator];
        return true;
    }

    private static bool IsCurrentBootstrapExecutable(string path)
    {
        var processPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(processPath) &&
               string.Equals(Path.GetFullPath(path), Path.GetFullPath(processPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentBootstrapAssembly(string path)
    {
        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(entryAssemblyName))
            return false;

        var entryAssemblyPath = Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.dll");
        return File.Exists(entryAssemblyPath) &&
               string.Equals(Path.GetFullPath(path), Path.GetFullPath(entryAssemblyPath), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsServiceMode() =>
        _foregroundOptions.IsService ||
        string.Equals(Environment.GetEnvironmentVariable("QUASAR_MODE"), "service", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitEnvironmentWorker(string workerPath)
    {
        var envExe = FirstExistingFile("QUASAR_WEB_EXE", "MAGNETAR_WEB_EXE");
        if (!string.IsNullOrWhiteSpace(envExe) &&
            string.Equals(Path.GetFullPath(workerPath), Path.GetFullPath(envExe), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var envDll = FirstExistingFile("QUASAR_WEB_DLL", "MAGNETAR_WEB_DLL");
        return !string.IsNullOrWhiteSpace(envDll) &&
               string.Equals(Path.GetFullPath(workerPath), Path.GetFullPath(envDll), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownReleasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return IsPathUnder(path, Path.Combine(AppContext.BaseDirectory, "WebService")) ||
               IsPathUnder(path, MagnetarPaths.GetQuasarManagedWebServiceDirectory()) ||
               IsPathUnder(path, MagnetarPaths.GetQuasarStagingDirectory());
    }

    private static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPackagedWorkerCandidate()
    {
        foreach (var fileName in GetQuasarExecutableFileNames())
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "WebService", fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> GetQuasarExecutableFileNames()
    {
        yield return OperatingSystem.IsWindows() ? "Quasar.exe" : "Quasar";
        yield return OperatingSystem.IsWindows() ? "Quasar" : "Quasar.exe";
    }

    private static string? FindWorkerCandidate(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, fileName);
            if (File.Exists(direct) &&
                !IsCurrentBootstrapExecutable(direct) &&
                !IsCurrentBootstrapAssembly(direct))
            {
                return direct;
            }

            var projectBin = Path.Combine(directory.FullName, "Quasar", "bin");
            if (Directory.Exists(projectBin))
            {
                var files = Directory.GetFiles(projectBin, fileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
        }

        return null;
    }

    private static async Task PumpWorkerStreamAsync(StreamReader reader, TextWriter writer)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    return;

                await writer.WriteLineAsync(line);
            }
        }
        catch
        {
            // Worker exit closes the pipe; stop pumping silently.
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        public bool Draft { get; set; }

        public bool Prerelease { get; set; }

        public IReadOnlyList<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

    private sealed record WorkerProcessHandle(Process Process, Uri BaseUri, QuasarActiveReleasePointer Release);
}

internal sealed record LauncherForegroundOptions(bool IsForeground, bool IsService = false);
