using System.Diagnostics;
using System.IO.Compression;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class ManagedDedicatedServerRuntimeResolver
{
    private const string MagnetarLauncherName = "MagnetarInterim";
    private const string DedicatedServerAppId = "298740";
    private const string DedicatedServerExecutableName = "SpaceEngineersDedicated";
    private static readonly string[] DedicatedServerExecutableNames =
    [
        "SpaceEngineersDedicated",
        "SpaceEngineersDedicated.exe",
    ];

    private static readonly UnixFileMode ExecutableUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly ILogger<ManagedDedicatedServerRuntimeResolver> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ManagedRuntimeOptions _options;
    private readonly SemaphoreSlim _magnetarInstallLock = new(1, 1);
    private readonly SemaphoreSlim _dedicatedServerInstallLock = new(1, 1);

    public ManagedDedicatedServerRuntimeResolver(
        ILogger<ManagedDedicatedServerRuntimeResolver> logger,
        IHttpClientFactory httpClientFactory,
        ManagedRuntimeOptions options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<ResolvedDedicatedServerRuntime> ResolveAsync(
        DedicatedServerInstanceDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var configuredExecutablePath = definition.ExecutablePath?.Trim() ?? string.Empty;
        var inferredDedicatedServer64Path = TryInferDedicatedServer64Path(configuredExecutablePath);

        string launcherExecutablePath;
        if (LooksLikeDedicatedServerExecutable(configuredExecutablePath) || string.IsNullOrWhiteSpace(configuredExecutablePath))
        {
            launcherExecutablePath = await EnsureManagedMagnetarInstallAsync(cancellationToken);
        }
        else
        {
            if (!File.Exists(configuredExecutablePath))
                throw new InvalidOperationException($"Executable not found: {configuredExecutablePath}");

            launcherExecutablePath = configuredExecutablePath;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory)
            ? Path.GetDirectoryName(launcherExecutablePath) ?? AppContext.BaseDirectory
            : definition.WorkingDirectory.Trim();

        var dedicatedServer64Path = await ResolveDedicatedServer64PathAsync(
            inferredDedicatedServer64Path,
            launcherExecutablePath,
            cancellationToken);

        return new ResolvedDedicatedServerRuntime(
            launcherExecutablePath,
            workingDirectory,
            dedicatedServer64Path);
    }

    private async Task<string> EnsureManagedMagnetarInstallAsync(CancellationToken cancellationToken)
    {
        var installDirectory = _options.MagnetarInstallDirectory;
        var launcherPath = Path.Combine(installDirectory, MagnetarLauncherName);
        var binaryLauncherPath = Path.Combine(installDirectory, "Bin", MagnetarLauncherName);
        if (File.Exists(launcherPath) && File.Exists(binaryLauncherPath))
            return launcherPath;

        await _magnetarInstallLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(launcherPath) && File.Exists(binaryLauncherPath))
                return launcherPath;

            Directory.CreateDirectory(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory());
            var extractRoot = Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory(), $"magnetar-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractRoot);

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);

                await using (var archiveStream = await client.GetStreamAsync(_options.MagnetarArchiveUrl, cancellationToken))
                {
                    using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
                    foreach (var entry in archive.Entries)
                        ExtractZipEntry(entry, extractRoot);
                }

                var sourceDirectory = FindMagnetarSourceDirectory(extractRoot)
                                      ?? throw new InvalidOperationException("Downloaded Magnetar archive did not contain Magnetar/Bin payload.");

                var sourceLauncherPath = Path.Combine(sourceDirectory, MagnetarLauncherName);
                var sourceBinDirectory = Path.Combine(sourceDirectory, "Bin");
                if (!File.Exists(sourceLauncherPath))
                    throw new InvalidOperationException($"Magnetar launcher not found in extracted archive: {sourceLauncherPath}");
                if (!Directory.Exists(sourceBinDirectory))
                    throw new InvalidOperationException($"Magnetar Bin directory not found in extracted archive: {sourceBinDirectory}");

                if (Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);

                Directory.CreateDirectory(installDirectory);
                CopyDirectory(sourceBinDirectory, Path.Combine(installDirectory, "Bin"));
                File.Copy(sourceLauncherPath, launcherPath, overwrite: true);
                EnsureExecutableBit(launcherPath);
                EnsureExecutableBit(Path.Combine(installDirectory, "Bin", MagnetarLauncherName));

                _logger.LogInformation("Installed managed Magnetar runtime into {Path}.", installDirectory);
                return launcherPath;
            }
            finally
            {
                TryDeleteDirectory(extractRoot);
            }
        }
        finally
        {
            _magnetarInstallLock.Release();
        }
    }

    private async Task<string> ResolveDedicatedServer64PathAsync(
        string inferredDedicatedServer64Path,
        string launcherExecutablePath,
        CancellationToken cancellationToken)
    {
        if (IsValidDedicatedServer64Directory(inferredDedicatedServer64Path))
            return inferredDedicatedServer64Path;

        if (IsValidDedicatedServer64Directory(_options.DedicatedServer64OverridePath))
            return _options.DedicatedServer64OverridePath;

        var adjacentPath = TryFindAdjacentDedicatedServer64Directory(launcherExecutablePath);
        if (IsValidDedicatedServer64Directory(adjacentPath))
            return adjacentPath;

        if (_options.PreferManagedDedicatedServerInstall)
        {
            var managedPath = await TryEnsureManagedDedicatedServerInstallAsync(cancellationToken);
            if (IsValidDedicatedServer64Directory(managedPath))
                return managedPath;
        }

        foreach (var candidate in EnumerateDedicatedServer64Candidates())
        {
            if (IsValidDedicatedServer64Directory(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "DedicatedServer64 path not found. Set QUASAR_DS64_PATH, install steamcmd for managed DS download, or point instance executable at SpaceEngineersDedicated once so Quasar can infer -ds64.");
    }

    private async Task<string> TryEnsureManagedDedicatedServerInstallAsync(CancellationToken cancellationToken)
    {
        var dedicatedServer64Path = Path.Combine(_options.DedicatedServerInstallDirectory, "DedicatedServer64");
        if (IsValidDedicatedServer64Directory(dedicatedServer64Path))
            return dedicatedServer64Path;

        var steamCmdPath = ResolveSteamCmdPath();
        if (string.IsNullOrWhiteSpace(steamCmdPath))
            return string.Empty;

        await _dedicatedServerInstallLock.WaitAsync(cancellationToken);
        try
        {
            if (IsValidDedicatedServer64Directory(dedicatedServer64Path))
                return dedicatedServer64Path;

            Directory.CreateDirectory(_options.DedicatedServerInstallDirectory);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = steamCmdPath,
                    Arguments = $"+force_install_dir {QuoteArgument(_options.DedicatedServerInstallDirectory)} +login anonymous +app_update {DedicatedServerAppId} validate +quit",
                    WorkingDirectory = Path.GetDirectoryName(steamCmdPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            try
            {
                if (!process.Start())
                    return string.Empty;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed starting steamcmd for managed DS install.");
                process.Dispose();
                return string.Empty;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "steamcmd failed installing managed DS. ExitCode={ExitCode}. Stdout={Stdout}. Stderr={Stderr}",
                    process.ExitCode,
                    TrimForLog(stdout),
                    TrimForLog(stderr));
                return string.Empty;
            }

            if (!IsValidDedicatedServer64Directory(dedicatedServer64Path))
            {
                _logger.LogWarning("steamcmd completed but DedicatedServer64 not found under {Path}.", _options.DedicatedServerInstallDirectory);
                return string.Empty;
            }

            _logger.LogInformation("Installed managed DedicatedServer64 into {Path}.", dedicatedServer64Path);
            return dedicatedServer64Path;
        }
        finally
        {
            _dedicatedServerInstallLock.Release();
        }
    }

    private string ResolveSteamCmdPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.SteamCmdPath))
            return File.Exists(_options.SteamCmdPath) ? _options.SteamCmdPath : string.Empty;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var fileNames = OperatingSystem.IsWindows()
            ? new[] { "steamcmd.exe", "steamcmd.bat" }
            : new[] { "steamcmd" };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeDedicatedServerExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fileName = Path.GetFileName(path.Trim());
        return DedicatedServerExecutableNames.Any(name => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string TryInferDedicatedServer64Path(string executablePath)
    {
        if (!LooksLikeDedicatedServerExecutable(executablePath))
            return string.Empty;

        var directory = Path.GetDirectoryName(executablePath.Trim()) ?? string.Empty;
        return IsValidDedicatedServer64Directory(directory) ? directory : string.Empty;
    }

    private static string TryFindAdjacentDedicatedServer64Directory(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
            return string.Empty;

        var candidate = Path.Combine(directory, "DedicatedServer64");
        return IsValidDedicatedServer64Directory(candidate) ? candidate : string.Empty;
    }

    private static IEnumerable<string> EnumerateDedicatedServer64Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineersDedicatedServer\DedicatedServer64";
            yield return @"C:\SteamCMD\steamapps\common\SpaceEngineersDedicatedServer\DedicatedServer64";
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "SpaceEngineersDedicatedServer", "DedicatedServer64");
            yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "SpaceEngineersDedicatedServer", "DedicatedServer64");
            yield return Path.Combine(home, "Steam", "steamapps", "common", "SpaceEngineersDedicatedServer", "DedicatedServer64");
            yield return Path.Combine(home, ".steam", "steamcmd", "steamapps", "common", "SpaceEngineersDedicatedServer", "DedicatedServer64");
        }
    }

    private static bool IsValidDedicatedServer64Directory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return DedicatedServerExecutableNames.Any(fileName => File.Exists(Path.Combine(path, fileName)));
    }

    private static void ExtractZipEntry(ZipArchiveEntry entry, string destinationRoot)
    {
        if (string.IsNullOrEmpty(entry.Name) && string.IsNullOrEmpty(entry.FullName))
            return;

        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(destinationRoot + Path.DirectorySeparatorChar);
        if (!destinationPath.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"Archive entry escapes extraction root: {entry.FullName}");

        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destinationPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static string? FindMagnetarSourceDirectory(string extractionRoot)
    {
        return Directory.GetFiles(extractionRoot, MagnetarLauncherName, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .FirstOrDefault(path => Directory.Exists(Path.Combine(path, "Bin")));
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void EnsureExecutableBit(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        try
        {
            File.SetUnixFileMode(path, ExecutableUnixFileMode);
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

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string TrimForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        const int maxLength = 1200;
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }
}

public sealed record ResolvedDedicatedServerRuntime(
    string ExecutablePath,
    string WorkingDirectory,
    string DedicatedServer64Path);
