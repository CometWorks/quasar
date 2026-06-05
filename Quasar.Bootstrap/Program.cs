using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
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
            ServeCommand => await ServeAsync(quiet, foreground),
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
                Console.Error.WriteLine("Warning: a Quasar instance is already running.");
                Console.Error.WriteLine("Terminating existing instance before relaunch (--force).");
            }

            await KillExistingInstanceAsync().ConfigureAwait(false);
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

            // Another instance surfaced during the mutex wait; kill it too.
            await KillExistingInstanceAsync().ConfigureAwait(false);
        }

        // If the port is already bound but no healthy Quasar responded, something else owns it.
        // Fail fast rather than spawning a process that will silently exit due to EADDRINUSE.
        if (!force)
        {
            var bootstrapOptions = BootstrapOptions.Create();
            if (IsPortInUse(bootstrapOptions.Port, bootstrapOptions.AdvertisedHost))
            {
                if (!quiet)
                    Console.Error.WriteLine($"Error: port {bootstrapOptions.Port} is already bound by another process and no healthy Quasar instance was detected. Use --force to terminate the existing process and restart.");
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

    private static async Task KillExistingInstanceAsync()
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

    private static async Task<int> ServeAsync(bool quiet = false, bool foreground = false)
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
                Console.Error.WriteLine($"Error: port {options.Port} is already bound by another process and is not a healthy Quasar instance.");
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
            new LauncherForegroundOptions(foreground),
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

    private static void StartDetachedProcess(string fileName, string arguments, string workingDirectory)
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

    public int Port { get; init; } = 58631;

    public string BaseUrl => $"http://{AdvertisedHost}:{Port}";

    public string ListenUrl => $"http://{Host}:{Port}";

    public static BootstrapOptions Create()
    {
        var configuration = BuildConfiguration();
        var section = configuration.GetSection("Quasar");
        if (!section.Exists())
            section = configuration.GetSection("MagnetarWeb");

        var host = section["Host"];
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";

        var portValue = section["Port"] ?? "58631";

        if (!int.TryParse(portValue, out var port) || port <= 0)
            port = 58631;

        var advertisedHost = host switch
        {
            "0.0.0.0" => "127.0.0.1",
            "*" => "127.0.0.1",
            "+" => "127.0.0.1",
            _ => host,
        };

        return new BootstrapOptions
        {
            Host = host,
            AdvertisedHost = advertisedHost,
            Port = port,
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
    private readonly SemaphoreSlim _activationLock = new(1, 1);
    private readonly object _sync = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _launcherToken = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;
    private WorkerProcessHandle? _currentWorker;
    private bool _isStopping;

    public LauncherCoordinator(BootstrapOptions options, LauncherForegroundOptions foregroundOptions, ILogger<LauncherCoordinator> logger)
    {
        _options = options;
        _foregroundOptions = foregroundOptions;
        _logger = logger;
        _healthClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

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
            instanceId = _instanceId,
            nodeId = Environment.MachineName.ToLowerInvariant(),
            nodeName = Environment.MachineName,
            baseUrl = _options.BaseUrl,
            activeWorkerVersion = workerVersion,
            activeWorkerBaseUrl = workerBaseUrl,
        };
    }

    public WebServiceDiscoveryManifest GetManifest()
    {
        return new WebServiceDiscoveryManifest
        {
            InstanceId = _instanceId,
            NodeId = Environment.MachineName.ToLowerInvariant(),
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            BaseUrl = _options.BaseUrl,
            StartedAtUtc = _startedAtUtc,
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(MagnetarPaths.GetWebServiceDirectory());
        Directory.CreateDirectory(MagnetarPaths.GetQuasarUpdatesDirectory());
        EnsureActiveReleasePointerExists();
        await ActivateCurrentReleaseAsync(force: false, cancellationToken).ConfigureAwait(false);
        StartWatchingReleasePointer();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();

        WorkerProcessHandle? worker;
        lock (_sync)
        {
            worker = _currentWorker;
            _currentWorker = null;
        }

        if (worker is not null)
            await DrainAndRetireWorkerAsync(worker, TimeSpan.Zero, stopManagedInstances: true, cancellationToken);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _activationLock.Dispose();
        _healthClient.Dispose();
    }

    private void EnsureActiveReleasePointerExists()
    {
        var existing = ReadActiveReleasePointer();
        if (existing is not null && IsReleasePointerUsable(existing))
            return;

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

            var nextWorker = await StartWorkerAsync(pointer, cancellationToken);
            if (nextWorker is null)
                return;

            WorkerProcessHandle? previousWorker;
            lock (_sync)
            {
                previousWorker = _currentWorker;
                _currentWorker = nextWorker;
            }

            _logger.LogInformation("Activated Quasar worker version {Version} at {BaseUri}.", pointer.Version, nextWorker.BaseUri);

            if (previousWorker is not null)
                _ = DrainAndRetireWorkerAsync(previousWorker, TimeSpan.FromSeconds(20), stopManagedInstances: false, CancellationToken.None);
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
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedFileName,
            Arguments = pointer.Arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(pointer.WorkingDirectory)
                ? Path.GetDirectoryName(resolvedFileName) ?? AppContext.BaseDirectory
                : pointer.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = _foregroundOptions.IsForeground,
            RedirectStandardError = _foregroundOptions.IsForeground,
        };

        startInfo.Environment["QUASAR_OPEN_BROWSER_ON_START"] = "false";
        startInfo.Environment["QUASAR_MODE"] = "service";
        startInfo.Environment["QUASAR_LAUNCHER_TOKEN"] = _launcherToken;
        startInfo.Environment["QUASAR_PRESERVE_INSTANCES_ON_SHUTDOWN"] = "false";
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
        bool stopManagedInstances,
        CancellationToken cancellationToken)
    {
        try
        {
            var drainUri = new Uri(worker.BaseUri, $"/api/internal/drain?delaySeconds={(int)Math.Round(graceDelay.TotalSeconds)}&stopInstances={stopManagedInstances.ToString().ToLowerInvariant()}");
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
            if (stopManagedInstances)
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
        catch (OperationCanceledException) when (!stopManagedInstances && !cancellationToken.IsCancellationRequested)
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

    private static bool IsReleasePointerUsable(QuasarActiveReleasePointer pointer)
    {
        pointer = Normalize(pointer);
        if (string.IsNullOrWhiteSpace(pointer.FileName))
            return false;

        var isDotNetHost = string.Equals(Path.GetFileNameWithoutExtension(pointer.FileName), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotNetHost)
        {
            return TryGetDotNetAssemblyArgument(pointer.Arguments, out var assemblyPath) &&
                   File.Exists(assemblyPath) &&
                   !IsCurrentBootstrapAssembly(assemblyPath);
        }

        return File.Exists(pointer.FileName) && !IsCurrentBootstrapExecutable(pointer.FileName);
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

    private sealed record WorkerProcessHandle(Process Process, Uri BaseUri, QuasarActiveReleasePointer Release);
}

internal sealed record LauncherForegroundOptions(bool IsForeground);
