using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services.Plugins;

public sealed class QuasarManagedDotNetSdkService
{
    // Update the version, URLs, and SHA-512 values together from Microsoft's
    // https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json
    public const int RequiredSdkMajor = 10;
    public const string PinnedSdkVersion = "10.0.111";

    private readonly object _sync = new();
    private readonly ILogger<QuasarManagedDotNetSdkService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private QuasarDotNetSdkStatus _status;
    private bool _installInProgress;

    public QuasarManagedDotNetSdkService(
        ILogger<QuasarManagedDotNetSdkService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _status = DetectStatus();
    }

    public bool InstallInProgress
    {
        get
        {
            lock (_sync)
                return _installInProgress;
        }
    }

    public QuasarDotNetSdkStatus GetStatus()
    {
        lock (_sync)
            return _status;
    }

    public QuasarDotNetSdkStatus RefreshStatus()
    {
        var status = DetectStatus();
        lock (_sync)
            _status = status;
        return status;
    }

    public QuasarDotNetSdkInstallAvailability GetInstallAvailability()
    {
        var asset = GetDownloadAsset();
        return asset is null
            ? new QuasarDotNetSdkInstallAvailability(
                CanInstall: false,
                $"Managed .NET SDK installation is not supported on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}.",
                GetManagedInstallDirectory())
            : new QuasarDotNetSdkInstallAvailability(
                CanInstall: true,
                $"Download verified .NET SDK {PinnedSdkVersion} into Quasar's managed data directory. System packages and PATH are not changed.",
                GetManagedInstallDirectory());
    }

    public async Task<QuasarDotNetSdkInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var status = RefreshStatus();
        if (status.CanBuildUiPlugins)
            return new QuasarDotNetSdkInstallResult(true, status.Message, string.Empty);

        var asset = GetDownloadAsset();
        if (asset is null)
        {
            var availability = GetInstallAvailability();
            return new QuasarDotNetSdkInstallResult(false, availability.Message, string.Empty);
        }

        if (!await _installLock.WaitAsync(0, cancellationToken))
            return new QuasarDotNetSdkInstallResult(false, "SDK install is already running.", string.Empty);

        SetInstallInProgress(true);
        var stagingDirectory = Path.Combine(
            MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory(),
            "DotNetSdk",
            $"staging-{Guid.NewGuid():N}");
        try
        {
            var archivePath = Path.Combine(stagingDirectory, asset.FileName);
            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            Directory.CreateDirectory(stagingDirectory);
            Directory.CreateDirectory(extractedDirectory);

            _logger.LogInformation(
                "Downloading pinned .NET SDK {Version} from {Url} into Quasar managed storage.",
                PinnedSdkVersion,
                asset.Url);
            await DownloadAsync(asset.Url, archivePath, cancellationToken);
            await VerifyHashAsync(archivePath, asset.Sha512, cancellationToken);
            ExtractArchive(archivePath, extractedDirectory, asset.ArchiveKind);

            var stagedExecutable = GetDotNetExecutablePath(extractedDirectory);
            EnsureExecutable(stagedExecutable);
            var stagedProbe = Probe(stagedExecutable, extractedDirectory);
            if (!stagedProbe.HasVersion(PinnedSdkVersion))
            {
                throw new InvalidOperationException(
                    $"Downloaded SDK did not expose the pinned {PinnedSdkVersion} toolchain. {stagedProbe.Error}".Trim());
            }

            var installDirectory = GetManagedInstallDirectory();
            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);
            if (Directory.Exists(installDirectory))
                Directory.Delete(installDirectory, recursive: true);
            Directory.Move(extractedDirectory, installDirectory);

            var refreshedStatus = RefreshStatus();
            if (!refreshedStatus.CanBuildUiPlugins)
                throw new InvalidOperationException(refreshedStatus.Message);

            _logger.LogInformation(
                "Installed managed .NET SDK {Version} into {Directory}.",
                PinnedSdkVersion,
                installDirectory);
            return new QuasarDotNetSdkInstallResult(
                true,
                refreshedStatus.Message,
                $"Installed .NET SDK {PinnedSdkVersion} in {installDirectory}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to install the managed .NET SDK.");
            return new QuasarDotNetSdkInstallResult(false, exception.Message, string.Empty);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            SetInstallInProgress(false);
            _installLock.Release();
        }
    }

    public ProcessStartInfo CreateBuildProcessStartInfo()
    {
        var status = RefreshStatus();
        if (!status.CanBuildUiPlugins)
            throw new InvalidOperationException(status.Message);

        var startInfo = new ProcessStartInfo(status.DotNetExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (status.Source == QuasarDotNetSdkSource.Managed)
        {
            startInfo.Environment["DOTNET_ROOT"] = status.ManagedInstallDirectory;
            startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }

        return startInfo;
    }

    internal static string GetManagedInstallDirectory() =>
        Path.Combine(
            MagnetarPaths.GetQuasarManagedRuntimeToolsDirectory(),
            "DotNetSdk",
            PinnedSdkVersion);

    internal static ManagedDotNetSdkDownload? GetDownloadAsset()
    {
        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => new ManagedDotNetSdkDownload(
                    $"dotnet-sdk-{PinnedSdkVersion}-linux-x64.tar.gz",
                    new Uri($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{PinnedSdkVersion}/dotnet-sdk-{PinnedSdkVersion}-linux-x64.tar.gz"),
                    "aae221be96a3b510d5b6fffefc69d8ad2fa595a1430299419316bb71c65f260a457ca9af24d044e1709b28a9118798caafec535ccfe58f7767c5acb735c00392",
                    ManagedDotNetSdkArchiveKind.TarGzip),
                Architecture.Arm64 => new ManagedDotNetSdkDownload(
                    $"dotnet-sdk-{PinnedSdkVersion}-linux-arm64.tar.gz",
                    new Uri($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{PinnedSdkVersion}/dotnet-sdk-{PinnedSdkVersion}-linux-arm64.tar.gz"),
                    "1e115ddb850950d4514d6a3b32b2d17b240a4f0f40b37202df4e5bdf6832a0e546722e6bf9b9ed7df7cccb34df5f5e48bcb075322fb01815bffc6e9c23999f0e",
                    ManagedDotNetSdkArchiveKind.TarGzip),
                _ => null,
            };
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => new ManagedDotNetSdkDownload(
                    $"dotnet-sdk-{PinnedSdkVersion}-win-x64.zip",
                    new Uri($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{PinnedSdkVersion}/dotnet-sdk-{PinnedSdkVersion}-win-x64.zip"),
                    "e874ffe330b1d2ab27a72bdb882e4bf6c965d9fd4f4cba3018c45cc8d5badd6e62d82949e616fa1e896961f86fa99ca46aa0751f4403a25351f3c23890b7cc9d",
                    ManagedDotNetSdkArchiveKind.Zip),
                Architecture.Arm64 => new ManagedDotNetSdkDownload(
                    $"dotnet-sdk-{PinnedSdkVersion}-win-arm64.zip",
                    new Uri($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{PinnedSdkVersion}/dotnet-sdk-{PinnedSdkVersion}-win-arm64.zip"),
                    "3f7241c46c8536f41c6b297bdb7d01a952bfd7e64cbbf53a04d57a3b405c5005e53bfb01caec9f0fd7bc1a7a2a6766bf1961e606f85cbe1d7e3dbf6f4f1e4d2d",
                    ManagedDotNetSdkArchiveKind.Zip),
                _ => null,
            };
        }

        return null;
    }

    private QuasarDotNetSdkStatus DetectStatus()
    {
        var globalProbe = Probe("dotnet", null);
        if (globalProbe.HasMajorVersion(RequiredSdkMajor))
        {
            return CreateStatus(
                QuasarDotNetSdkSource.Global,
                dotNetOnPath: true,
                globalProbe.Versions,
                "dotnet",
                $".NET {RequiredSdkMajor} SDK {globalProbe.FirstMajorVersion(RequiredSdkMajor)} is available globally for Quasar UI plugin builds.");
        }

        var managedDirectory = GetManagedInstallDirectory();
        var managedExecutable = GetDotNetExecutablePath(managedDirectory);
        var managedProbe = File.Exists(managedExecutable)
            ? Probe(managedExecutable, managedDirectory)
            : DotNetSdkProbe.NotFound("Managed SDK is not installed.");
        if (managedProbe.HasVersion(PinnedSdkVersion))
        {
            return CreateStatus(
                QuasarDotNetSdkSource.Managed,
                globalProbe.Started,
                managedProbe.Versions,
                managedExecutable,
                $"Managed .NET SDK {PinnedSdkVersion} is ready for Quasar UI plugin builds.");
        }

        var globalVersions = globalProbe.Versions.Count == 0 ? "none" : string.Join(", ", globalProbe.Versions);
        return CreateStatus(
            QuasarDotNetSdkSource.None,
            globalProbe.Started,
            globalProbe.Versions,
            string.Empty,
            $".NET {RequiredSdkMajor} SDK was not found globally or in Quasar managed storage. Global SDKs: {globalVersions}.");
    }

    private static QuasarDotNetSdkStatus CreateStatus(
        QuasarDotNetSdkSource source,
        bool dotNetOnPath,
        IReadOnlyList<string> versions,
        string executablePath,
        string message) =>
        new(
            RequiredSdkMajor,
            PinnedSdkVersion,
            source,
            dotNetOnPath,
            source != QuasarDotNetSdkSource.None,
            versions,
            executablePath,
            GetManagedInstallDirectory(),
            message);

    private static DotNetSdkProbe Probe(string executablePath, string? dotNetRoot)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--list-sdks");
        if (!string.IsNullOrWhiteSpace(dotNetRoot))
        {
            startInfo.Environment["DOTNET_ROOT"] = dotNetRoot;
            startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return DotNetSdkProbe.NotFound($"Failed to start {executablePath} --list-sdks.");

            if (!process.WaitForExit(5000))
            {
                TryKillProcess(process);
                return DotNetSdkProbe.NotFound($"{executablePath} --list-sdks did not finish within 5 seconds.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                var error = string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()]).Trim();
                return new DotNetSdkProbe(true, [], string.IsNullOrWhiteSpace(error)
                    ? $"{executablePath} --list-sdks failed with exit code {process.ExitCode}."
                    : error);
            }

            var versions = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(GetSdkVersion)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .ToArray();
            return new DotNetSdkProbe(true, versions, string.Empty);
        }
        catch (Exception exception)
        {
            return DotNetSdkProbe.NotFound(exception.Message);
        }
    }

    private async Task DownloadAsync(Uri url, string destination, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(20);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        await source.CopyToAsync(output, cancellationToken);
    }

    private static async Task VerifyHashAsync(string path, string expectedSha512, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expectedSha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded .NET SDK archive failed SHA-512 verification.");
    }

    private static void ExtractArchive(string archivePath, string destination, ManagedDotNetSdkArchiveKind archiveKind)
    {
        if (archiveKind == ManagedDotNetSdkArchiveKind.Zip)
        {
            ZipFile.ExtractToDirectory(archivePath, destination);
            return;
        }

        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    private static string GetDotNetExecutablePath(string rootDirectory) =>
        Path.Combine(rootDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded SDK archive did not contain the dotnet host.", path);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private void SetInstallInProgress(bool value)
    {
        lock (_sync)
            _installInProgress = value;
    }

    private static string GetSdkVersion(string sdkLine)
    {
        var index = sdkLine.IndexOf(' ', StringComparison.Ordinal);
        return index < 0 ? sdkLine : sdkLine[..index];
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
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

    private sealed record DotNetSdkProbe(bool Started, IReadOnlyList<string> Versions, string Error)
    {
        public static DotNetSdkProbe NotFound(string error) => new(false, [], error);

        public bool HasVersion(string version) =>
            Versions.Any(candidate => string.Equals(candidate, version, StringComparison.OrdinalIgnoreCase));

        public bool HasMajorVersion(int majorVersion) =>
            Versions.Any(version => version.StartsWith($"{majorVersion}.", StringComparison.OrdinalIgnoreCase));

        public string FirstMajorVersion(int majorVersion) =>
            Versions.First(version => version.StartsWith($"{majorVersion}.", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record QuasarDotNetSdkInstallAvailability(
    bool CanInstall,
    string Message,
    string InstallDirectory);

public sealed record QuasarDotNetSdkInstallResult(
    bool Succeeded,
    string Message,
    string Output);

public sealed record QuasarDotNetSdkStatus(
    int RequiredMajorVersion,
    string PinnedManagedSdkVersion,
    QuasarDotNetSdkSource Source,
    bool DotNetOnPath,
    bool RequiredSdkAvailable,
    IReadOnlyList<string> InstalledSdkVersions,
    string DotNetExecutablePath,
    string ManagedInstallDirectory,
    string Message)
{
    public bool CanBuildUiPlugins => RequiredSdkAvailable && !string.IsNullOrWhiteSpace(DotNetExecutablePath);
}

public enum QuasarDotNetSdkSource
{
    None,
    Global,
    Managed,
}

internal sealed record ManagedDotNetSdkDownload(
    string FileName,
    Uri Url,
    string Sha512,
    ManagedDotNetSdkArchiveKind ArchiveKind);

internal enum ManagedDotNetSdkArchiveKind
{
    Zip,
    TarGzip,
}
