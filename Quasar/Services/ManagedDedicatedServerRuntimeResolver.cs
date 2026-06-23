using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using Quasar.Models;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Quasar.Services;

public sealed class ManagedDedicatedServerRuntimeResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MagnetarReleaseCheckCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SteamCmdKillWaitTimeout = TimeSpan.FromSeconds(10);
    private const string MagnetarLauncherName = "MagnetarInterim";
    private const string MagnetarReleaseMarkerFileName = ".quasar-magnetar-release.json";
    private const string DedicatedServerAppId = "298740";
    private const string DedicatedServerExecutableName = "SpaceEngineersDedicated";
    private const int DedicatedServerInstallMaxAttempts = 3;
    private static readonly string[] MagnetarLauncherFileNames =
    [
        "MagnetarInterim",
        "MagnetarInterim.exe",
    ];

    private static readonly string[] DedicatedServerExecutableNames =
    [
        "SpaceEngineersDedicated",
        "SpaceEngineersDedicated.exe",
    ];

    private static readonly string[] SteamCmdFileNames =
    [
        "steamcmd",
        "steamcmd.sh",
        "steamcmd.exe",
        "steamcmd.bat",
    ];

    private static readonly string[] DedicatedServerRequiredFileNames =
    [
        "SpaceEngineers.Game.dll",
        "VRage.dll",
        "Sandbox.Game.dll",
    ];

    private static readonly string[] SteamGameServerRuntimeFileNames =
    [
        "steamclient.so",
        "libtier0_s.so",
        "libvstdlib_s.so",
    ];

    private static readonly UnixFileMode ExecutableUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly ILogger<ManagedDedicatedServerRuntimeResolver> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ManagedRuntimeOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SemaphoreSlim _magnetarInstallLock = new(1, 1);
    private readonly SemaphoreSlim _steamCmdInstallLock = new(1, 1);
    private readonly SemaphoreSlim _dedicatedServerInstallLock = new(1, 1);
    private readonly object _magnetarReleaseCheckSync = new();
    private MagnetarArchiveReference? _cachedMagnetarArchiveReference;
    private DateTimeOffset _cachedMagnetarArchiveReferenceCheckedAtUtc;

    public ManagedDedicatedServerRuntimeResolver(
        ILogger<ManagedDedicatedServerRuntimeResolver> logger,
        IHttpClientFactory httpClientFactory,
        ManagedRuntimeOptions options,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _lifetime = lifetime;
    }

    public async Task<ResolvedDedicatedServerRuntime> ResolveAsync(
        DedicatedServerDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var configuredExecutablePath = definition.ExecutablePath?.Trim() ?? string.Empty;
        var inferredDedicatedServer64Path = TryInferDedicatedServer64Path(configuredExecutablePath);

        // Only Windows ships both Magnetar builds; every other host runs the .NET 10
        // (Interim) build, so a Legacy selection on a server.json moved from Windows to
        // Linux is silently downgraded here rather than failing to launch.
        var runtimeFlavor = OperatingSystem.IsWindows()
            ? definition.ManagedRuntime
            : ManagedServerRuntime.DotNet10;

        string launcherExecutablePath;
        if (LooksLikeDedicatedServerExecutable(configuredExecutablePath) || string.IsNullOrWhiteSpace(configuredExecutablePath))
        {
            launcherExecutablePath = await EnsureManagedMagnetarInstallAsync(runtimeFlavor, progress: null, cancellationToken);
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
        var nativeLibrarySearchPaths = ResolveNativeLibrarySearchPaths();

        return new ResolvedDedicatedServerRuntime(
            launcherExecutablePath,
            workingDirectory,
            dedicatedServer64Path,
            nativeLibrarySearchPaths);
    }

    public async Task<ManagedRuntimeReadiness> EnsureManagedRuntimeReadyAsync(
        IProgress<ManagedRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.SteamCmd,
            ManagedRuntimeInstallPhase.Checking,
            "Checking SteamCMD install."));

        var steamCmdPath = await EnsureManagedSteamCmdInstallAsync(progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(steamCmdPath))
        {
            return ManagedRuntimeReadiness.Failed(
                "SteamCMD is not installed and could not be downloaded.",
                steamCmdPath,
                string.Empty,
                string.Empty);
        }

        var steamCmdRuntimePath = TryGetSteamCmdRuntimeDirectory(steamCmdPath);
        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.SteamCmd,
            ManagedRuntimeInstallPhase.Ready,
            "SteamCMD installed.",
            Path: steamCmdPath,
            Version: GetFileVersion(steamCmdPath)));

        var dedicatedServer64Path = Path.Combine(_options.DedicatedServerInstallDirectory, "DedicatedServer64");
        var dedicatedServerReady = IsValidDedicatedServer64Directory(dedicatedServer64Path);
        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.DedicatedServer,
            ManagedRuntimeInstallPhase.Checking,
            "Checking Space Engineers Dedicated Server install.",
            Path: dedicatedServer64Path));

        if (!dedicatedServerReady)
        {
            dedicatedServer64Path = await TryEnsureManagedDedicatedServerInstallAsync(
                cancellationToken,
                steamCmdPath,
                progress);
            dedicatedServerReady = IsValidDedicatedServer64Directory(dedicatedServer64Path);
        }

        if (!dedicatedServerReady)
        {
            return ManagedRuntimeReadiness.Failed(
                "Space Engineers Dedicated Server is not installed and could not be downloaded.",
                steamCmdPath,
                steamCmdRuntimePath,
                dedicatedServer64Path);
        }

        if (OperatingSystem.IsLinux() && !IsValidSteamGameServerRuntimeDirectory(steamCmdRuntimePath))
        {
            progress?.Report(new ManagedRuntimeInstallProgress(
                ManagedRuntimeInstallComponent.SteamCmd,
                ManagedRuntimeInstallPhase.Installing,
                "Preparing SteamCMD native runtime."));

            await RunSteamCmdAsync(
                steamCmdPath,
                "+quit",
                "preparing SteamCMD native runtime",
                cancellationToken);
        }

        steamCmdRuntimePath = TryGetSteamCmdRuntimeDirectory(steamCmdPath);
        if (OperatingSystem.IsLinux() && !IsValidSteamGameServerRuntimeDirectory(steamCmdRuntimePath))
        {
            return ManagedRuntimeReadiness.Failed(
                $"SteamCMD native runtime not found under {steamCmdRuntimePath}.",
                steamCmdPath,
                steamCmdRuntimePath,
                dedicatedServer64Path);
        }

        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.SteamCmd,
            ManagedRuntimeInstallPhase.Ready,
            OperatingSystem.IsLinux()
                ? "SteamCMD and linux64 native runtime ready."
                : "SteamCMD ready.",
            Path: OperatingSystem.IsLinux() ? steamCmdRuntimePath : steamCmdPath,
            Version: GetFileVersion(steamCmdPath)));
        var dedicatedServerVersion = GetDedicatedServerVersion(dedicatedServer64Path);
        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.DedicatedServer,
            ManagedRuntimeInstallPhase.Ready,
            BuildDedicatedServerReadyMessage(dedicatedServerVersion),
            Path: dedicatedServer64Path,
            Version: dedicatedServerVersion));

        await EnsureManagedMagnetarInstallAsync(ManagedServerRuntime.DotNet10, progress, cancellationToken);

        return new ManagedRuntimeReadiness(
            true,
            steamCmdPath,
            steamCmdRuntimePath,
            dedicatedServer64Path,
            string.Empty);
    }

    public Task EnsureManagedMagnetarCurrentAsync(
        IProgress<ManagedRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        EnsureManagedMagnetarInstallAsync(ManagedServerRuntime.DotNet10, progress, cancellationToken);

    public async Task EnsureManagedDedicatedServerCurrentAsync(
        IProgress<ManagedRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.SteamCmd,
            ManagedRuntimeInstallPhase.Checking,
            "Checking SteamCMD install."));

        var steamCmdPath = await EnsureManagedSteamCmdInstallAsync(progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(steamCmdPath))
            throw new InvalidOperationException("SteamCMD is not installed and could not be downloaded.");

        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.SteamCmd,
            ManagedRuntimeInstallPhase.Ready,
            "SteamCMD installed.",
            Path: steamCmdPath,
            Version: GetFileVersion(steamCmdPath)));

        var dedicatedServer64Path = await TryEnsureManagedDedicatedServerInstallAsync(
            cancellationToken,
            steamCmdPath,
            progress);
        if (!IsValidDedicatedServer64Directory(dedicatedServer64Path))
            throw new InvalidOperationException("Space Engineers Dedicated Server is not installed and could not be updated.");

        var version = GetDedicatedServerVersion(dedicatedServer64Path);
        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.DedicatedServer,
            ManagedRuntimeInstallPhase.Ready,
            BuildDedicatedServerReadyMessage(version),
            Path: dedicatedServer64Path,
            Version: version));
    }

    public ManagedRuntimeVersionSnapshot GetInstalledVersions()
    {
        var steamCmdPath = ResolveInstalledSteamCmdPath();
        var magnetarPath = ResolveInstalledMagnetarPath();
        var dedicatedServer64Path = ResolveInstalledDedicatedServer64Path();
        var installedMagnetar = ReadInstalledMagnetarRelease(_options.MagnetarInstallDirectory);

        return new ManagedRuntimeVersionSnapshot(
            SteamCmdPath: steamCmdPath,
            SteamCmdVersion: GetFileVersion(steamCmdPath),
            MagnetarPath: magnetarPath,
            MagnetarVersion: installedMagnetar?.DisplayName ?? GetFileVersion(magnetarPath),
            DedicatedServer64Path: dedicatedServer64Path,
            DedicatedServerVersion: GetDedicatedServerVersion(dedicatedServer64Path));
    }

    private Task<string> EnsureManagedMagnetarInstallAsync(
        ManagedServerRuntime runtime,
        IProgress<ManagedRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Windows ships both Magnetar builds side-by-side (exes + per-runtime Libraries
        // subfolders, no Bin/ wrapper); Linux ships a single Interim build behind a
        // top-level wrapper with the apphost under Bin/. The two layouts need different
        // install logic, but both paths compare the installed marker against latest first.
        return OperatingSystem.IsWindows()
            ? EnsureWindowsManagedMagnetarInstallAsync(runtime, progress, cancellationToken)
            : EnsureLinuxManagedMagnetarInstallAsync(progress, cancellationToken);
    }

    private async Task<string> EnsureLinuxManagedMagnetarInstallAsync(
        IProgress<ManagedRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installDirectory = _options.MagnetarInstallDirectory;

        await _magnetarInstallLock.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new ManagedRuntimeInstallProgress(
                ManagedRuntimeInstallComponent.Magnetar,
                ManagedRuntimeInstallPhase.Checking,
                "Checking Magnetar runtime install.",
                Path: installDirectory));

            // Resolve to the actual apphost binary under Bin/, never the top-level
            // MagnetarInterim wrapper script. The wrapper only `cd`s into Bin/ and execs
            // this binary; Quasar runs the binary directly with Bin/ as the working
            // directory (Path.GetDirectoryName of the returned path), so no extra shell
            // is spawned to set up the environment and the tracked PID is the server's.
            var binaryLauncherPath = FindImmediateFile(Path.Combine(installDirectory, "Bin"), MagnetarLauncherFileNames);
            var archive = await ResolveMagnetarArchiveReferenceOrUseExistingAsync(
                binaryLauncherPath ?? string.Empty,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(binaryLauncherPath) && IsCurrentMagnetarInstall(installDirectory, archive))
            {
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Ready,
                    BuildMagnetarReadyMessage(archive),
                    Path: binaryLauncherPath,
                    Version: GetMagnetarVersion(archive)));
                return binaryLauncherPath;
            }

            if (!string.IsNullOrWhiteSpace(binaryLauncherPath))
            {
                _logger.LogInformation(
                    "Updating managed Magnetar runtime from {InstalledRelease} to {LatestRelease}.",
                    DescribeInstalledMagnetarRelease(installDirectory),
                    archive.DisplayName);
            }

            Directory.CreateDirectory(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory());
            var extractRoot = Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory(), $"magnetar-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractRoot);

            try
            {
                await DownloadAndExtractMagnetarArchiveAsync(archive, extractRoot, progress, cancellationToken);

                var source = FindMagnetarSource(extractRoot)
                             ?? throw new InvalidOperationException("Downloaded Magnetar archive did not contain MagnetarInterim with a Bin payload.");

                if (!File.Exists(source.LauncherPath))
                    throw new InvalidOperationException($"Magnetar launcher not found in extracted archive: {source.LauncherPath}");
                if (!Directory.Exists(source.BinDirectory))
                    throw new InvalidOperationException($"Magnetar Bin directory not found in extracted archive: {source.BinDirectory}");

                if (Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);

                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Installing,
                    $"Installing Magnetar runtime {archive.DisplayName}.",
                    Path: installDirectory));
                Directory.CreateDirectory(installDirectory);
                CopyDirectory(source.BinDirectory, Path.Combine(installDirectory, "Bin"));
                binaryLauncherPath = FindImmediateFile(Path.Combine(installDirectory, "Bin"), MagnetarLauncherFileNames)
                    ?? throw new InvalidOperationException(
                        $"Magnetar apphost binary not found under {Path.Combine(installDirectory, "Bin")} after install.");
                EnsureExecutableBit(binaryLauncherPath);
                await WriteInstalledMagnetarReleaseAsync(installDirectory, archive, cancellationToken);

                _logger.LogInformation("Installed managed Magnetar runtime {Release} into {Path}.", archive.DisplayName, installDirectory);
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Ready,
                    BuildMagnetarReadyMessage(archive),
                    Path: binaryLauncherPath,
                    Version: GetMagnetarVersion(archive)));
                return binaryLauncherPath;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                             !string.IsNullOrWhiteSpace(binaryLauncherPath) &&
                                             File.Exists(binaryLauncherPath))
            {
                _logger.LogWarning(
                    exception,
                    "Failed updating managed Magnetar runtime to {Release}. Continuing with existing runtime at {Path}.",
                    archive.DisplayName,
                    binaryLauncherPath);
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Ready,
                    "Using existing Magnetar runtime after update failure.",
                    Path: binaryLauncherPath,
                    Version: DescribeInstalledMagnetarRelease(installDirectory)));
                return binaryLauncherPath;
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

    private async Task<string> EnsureWindowsManagedMagnetarInstallAsync(
        ManagedServerRuntime runtime,
        IProgress<ManagedRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installDirectory = _options.MagnetarInstallDirectory;
        var launcherFileName = GetWindowsMagnetarLauncherFileName(runtime);

        await _magnetarInstallLock.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new ManagedRuntimeInstallProgress(
                ManagedRuntimeInstallComponent.Magnetar,
                ManagedRuntimeInstallPhase.Checking,
                "Checking Magnetar runtime install.",
                Path: installDirectory));

            // Both builds (Interim + Legacy) install together into a single folder. Resolve
            // to the requested exe; its containing folder is the working directory (where
            // the Libraries payload lives).
            var launcherPath = Path.Combine(installDirectory, launcherFileName);
            var archive = await ResolveMagnetarArchiveReferenceOrUseExistingAsync(
                File.Exists(launcherPath) ? launcherPath : string.Empty,
                cancellationToken);
            if (File.Exists(launcherPath) && IsCurrentMagnetarInstall(installDirectory, archive))
            {
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Ready,
                    BuildMagnetarReadyMessage(archive),
                    Path: launcherPath,
                    Version: GetMagnetarVersion(archive)));
                return launcherPath;
            }

            if (File.Exists(launcherPath))
            {
                _logger.LogInformation(
                    "Updating managed Magnetar runtime from {InstalledRelease} to {LatestRelease}.",
                    DescribeInstalledMagnetarRelease(installDirectory),
                    archive.DisplayName);
            }

            Directory.CreateDirectory(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory());
            var extractRoot = Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory(), $"magnetar-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractRoot);

            try
            {
                await DownloadAndExtractMagnetarArchiveAsync(archive, extractRoot, progress, cancellationToken);

                var source = FindWindowsMagnetarSource(extractRoot)
                             ?? throw new InvalidOperationException(
                                 "Downloaded Magnetar archive did not contain MagnetarInterim.exe with a Libraries payload.");

                if (Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);

                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Installing,
                    $"Installing Magnetar runtime {archive.DisplayName}.",
                    Path: installDirectory));
                Directory.CreateDirectory(installDirectory);
                CopyDirectory(source, installDirectory);

                launcherPath = Path.Combine(installDirectory, launcherFileName);
                if (!File.Exists(launcherPath))
                    throw new InvalidOperationException(
                        $"Magnetar launcher {launcherFileName} not found under {installDirectory} after install.");

                await WriteInstalledMagnetarReleaseAsync(installDirectory, archive, cancellationToken);

                _logger.LogInformation("Installed managed Magnetar runtime {Release} into {Path}.", archive.DisplayName, installDirectory);
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Ready,
                    BuildMagnetarReadyMessage(archive),
                    Path: launcherPath,
                    Version: GetMagnetarVersion(archive)));
                return launcherPath;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && File.Exists(launcherPath))
            {
                _logger.LogWarning(
                    exception,
                    "Failed updating managed Magnetar runtime to {Release}. Continuing with existing runtime at {Path}.",
                    archive.DisplayName,
                    launcherPath);
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.Magnetar,
                    ManagedRuntimeInstallPhase.Ready,
                    "Using existing Magnetar runtime after update failure.",
                    Path: launcherPath,
                    Version: DescribeInstalledMagnetarRelease(installDirectory)));
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

    private async Task DownloadAndExtractMagnetarArchiveAsync(
        MagnetarArchiveReference archive,
        string extractRoot,
        IProgress<ManagedRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");

        var archivePath = Path.Combine(extractRoot, "magnetar-download" + InferArchiveExtension(archive.ArchiveUrl));
        _logger.LogInformation("Downloading Magnetar runtime {Release}...", archive.DisplayName);
        using var response = await client.GetAsync(archive.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed downloading Magnetar archive from {archive.ArchiveUrl}. Status={(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await CopyToFileWithProgressAsync(
            response.Content,
            archivePath,
            percent => progress?.Report(new ManagedRuntimeInstallProgress(
                ManagedRuntimeInstallComponent.Magnetar,
                ManagedRuntimeInstallPhase.Downloading,
                $"Downloading Magnetar runtime {archive.DisplayName}.",
                percent)),
            cancellationToken);

        progress?.Report(new ManagedRuntimeInstallProgress(
            ManagedRuntimeInstallComponent.Magnetar,
            ManagedRuntimeInstallPhase.Installing,
            $"Extracting Magnetar runtime {archive.DisplayName}."));
        ExtractArchive(archivePath, extractRoot);
    }

    private async Task<MagnetarArchiveReference> ResolveMagnetarArchiveReferenceOrUseExistingAsync(
        string existingLauncherPath,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedMagnetarArchiveReference(out var cachedArchive))
            return cachedArchive;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");

        try
        {
            var archive = await ResolveMagnetarArchiveReferenceAsync(client, cancellationToken);
            CacheMagnetarArchiveReference(archive);
            return archive;
        }
        catch (Exception exception) when (!string.IsNullOrWhiteSpace(existingLauncherPath) && !cancellationToken.IsCancellationRequested)
        {
            var installDirectory = _options.MagnetarInstallDirectory;
            var installed = ReadInstalledMagnetarRelease(installDirectory);
            _logger.LogWarning(
                exception,
                "Failed checking latest Magnetar release. Continuing with installed runtime {Release} at {Path}.",
                installed?.DisplayName ?? "unknown",
                existingLauncherPath);

            return installed is null
                ? MagnetarArchiveReference.UnknownExisting
                : new MagnetarArchiveReference(
                    installed.SourceKind,
                    installed.ReleaseTagName,
                    installed.AssetName,
                    installed.ArchiveUrl);
        }
    }

    private bool TryGetCachedMagnetarArchiveReference(out MagnetarArchiveReference archive)
    {
        archive = null!;
        if (!string.IsNullOrWhiteSpace(_options.MagnetarArchiveUrl))
            return false;

        lock (_magnetarReleaseCheckSync)
        {
            if (_cachedMagnetarArchiveReference is null)
                return false;

            if (DateTimeOffset.UtcNow - _cachedMagnetarArchiveReferenceCheckedAtUtc >= MagnetarReleaseCheckCooldown)
                return false;

            archive = _cachedMagnetarArchiveReference;
            return true;
        }
    }

    private void CacheMagnetarArchiveReference(MagnetarArchiveReference archive)
    {
        if (!string.Equals(archive.SourceKind, MagnetarArchiveSourceKinds.GitHubRelease, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_magnetarReleaseCheckSync)
        {
            _cachedMagnetarArchiveReference = archive;
            _cachedMagnetarArchiveReferenceCheckedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<MagnetarArchiveReference> ResolveMagnetarArchiveReferenceAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.MagnetarArchiveUrl))
        {
            return new MagnetarArchiveReference(
                MagnetarArchiveSourceKinds.DirectUrl,
                string.Empty,
                InferArchiveAssetName(_options.MagnetarArchiveUrl),
                _options.MagnetarArchiveUrl);
        }

        using var response = await client.GetAsync(_options.MagnetarReleaseApiUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed resolving latest Magnetar release from {_options.MagnetarReleaseApiUrl}. Status={(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
                      ?? throw new InvalidOperationException("GitHub latest Magnetar release response was empty.");

        if (release.Draft || release.Prerelease)
            throw new InvalidOperationException("GitHub latest Magnetar release is not a full release.");

        var matches = release.Assets
            .Where(asset => MatchesAssetPattern(asset.Name, _options.MagnetarArchiveAssetPattern))
            .ToList();

        if (matches.Count != 1)
        {
            var names = string.Join(", ", release.Assets.Select(asset => asset.Name));
            throw new InvalidOperationException(
                $"Expected one Magnetar archive asset matching '{_options.MagnetarArchiveAssetPattern}' in latest release {release.TagName}, found {matches.Count}. Assets: {names}");
        }

        var asset = matches[0];
        if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            throw new InvalidOperationException($"Magnetar archive asset '{asset.Name}' has no browser_download_url.");

        return new MagnetarArchiveReference(
            MagnetarArchiveSourceKinds.GitHubRelease,
            release.TagName,
            asset.Name,
            asset.BrowserDownloadUrl);
    }

    private static bool IsCurrentMagnetarInstall(string installDirectory, MagnetarArchiveReference archive)
    {
        if (archive.SourceKind == MagnetarArchiveSourceKinds.ExistingUnknown)
            return true;

        var installed = ReadInstalledMagnetarRelease(installDirectory);
        if (installed is null ||
            !string.Equals(installed.SourceKind, archive.SourceKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(archive.SourceKind, MagnetarArchiveSourceKinds.GitHubRelease, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(installed.ReleaseTagName, archive.ReleaseTagName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(installed.AssetName, archive.AssetName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(installed.ReleaseTagName, archive.ReleaseTagName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(installed.AssetName, archive.AssetName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(installed.ArchiveUrl, archive.ArchiveUrl, StringComparison.Ordinal);
    }

    private static string DescribeInstalledMagnetarRelease(string installDirectory) =>
        ReadInstalledMagnetarRelease(installDirectory)?.DisplayName ?? "unknown";

    private static string BuildMagnetarReadyMessage(MagnetarArchiveReference archive) =>
        archive.SourceKind == MagnetarArchiveSourceKinds.ExistingUnknown
            ? "Magnetar runtime ready."
            : $"Magnetar runtime {archive.DisplayName} ready.";

    private static string GetMagnetarVersion(MagnetarArchiveReference archive) =>
        archive.SourceKind == MagnetarArchiveSourceKinds.ExistingUnknown
            ? string.Empty
            : archive.DisplayName;

    private static string BuildDedicatedServerReadyMessage(string version) =>
        string.IsNullOrWhiteSpace(version)
            ? "Space Engineers Dedicated Server ready."
            : $"Space Engineers Dedicated Server {version} ready.";

    private string ResolveInstalledSteamCmdPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.SteamCmdPath) && File.Exists(_options.SteamCmdPath))
            return _options.SteamCmdPath;

        var managedPath = FindSteamCmdExecutable(_options.SteamCmdInstallDirectory);
        if (!string.IsNullOrWhiteSpace(managedPath))
            return managedPath;

        return ResolveSteamCmdPathFromEnvironment();
    }

    private string ResolveInstalledMagnetarPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var interimPath = Path.Combine(_options.MagnetarInstallDirectory, GetWindowsMagnetarLauncherFileName(ManagedServerRuntime.DotNet10));
            if (File.Exists(interimPath))
                return interimPath;

            var legacyPath = Path.Combine(_options.MagnetarInstallDirectory, GetWindowsMagnetarLauncherFileName(ManagedServerRuntime.NetFramework48));
            return File.Exists(legacyPath) ? legacyPath : string.Empty;
        }

        return FindImmediateFile(Path.Combine(_options.MagnetarInstallDirectory, "Bin"), MagnetarLauncherFileNames) ?? string.Empty;
    }

    private string ResolveInstalledDedicatedServer64Path()
    {
        if (IsValidDedicatedServer64Directory(_options.DedicatedServer64OverridePath))
            return _options.DedicatedServer64OverridePath;

        var managedPath = Path.Combine(_options.DedicatedServerInstallDirectory, "DedicatedServer64");
        if (IsValidDedicatedServer64Directory(managedPath))
            return managedPath;

        return EnumerateDedicatedServer64Candidates().FirstOrDefault(IsValidDedicatedServer64Directory) ?? string.Empty;
    }

    private static string GetDedicatedServerVersion(string dedicatedServer64Path)
    {
        if (!IsValidDedicatedServer64Directory(dedicatedServer64Path))
            return string.Empty;

        return FirstNonEmpty(
            GetSpaceEngineersGameVersion(dedicatedServer64Path),
            GetNonPlaceholderFileVersion(Path.Combine(dedicatedServer64Path, DedicatedServerExecutableName + ".exe")),
            GetNonPlaceholderFileVersion(Path.Combine(dedicatedServer64Path, DedicatedServerExecutableName)),
            GetNonPlaceholderFileVersion(Path.Combine(dedicatedServer64Path, "SpaceEngineers.Game.dll")),
            GetNonPlaceholderFileVersion(Path.Combine(dedicatedServer64Path, "Sandbox.Game.dll")));
    }

    private static string GetSpaceEngineersGameVersion(string dedicatedServer64Path)
    {
        var gameAssemblyPath = Path.Combine(dedicatedServer64Path, "SpaceEngineers.Game.dll");
        if (!TryReadInt32Constant(
                gameAssemblyPath,
                "SpaceEngineers.Game",
                "SpaceEngineersGame",
                "SE_VERSION",
                out var gameVersion))
        {
            return string.Empty;
        }

        var version = FormatSpaceEngineersVersion(gameVersion);
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        return TryReadInt32Constant(
                   gameAssemblyPath,
                   "SpaceEngineers.Game",
                   "SpaceEngineersGame",
                   "SERVER_BUILD_NUMBER",
                   out var serverBuildNumber) &&
               serverBuildNumber > 0
            ? $"{version} b{serverBuildNumber}"
            : version;
    }

    private static bool TryReadInt32Constant(
        string assemblyPath,
        string typeNamespace,
        string typeName,
        string fieldName,
        out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return false;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var metadata = peReader.GetMetadataReader();
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                if (!string.Equals(metadata.GetString(type.Namespace), typeNamespace, StringComparison.Ordinal) ||
                    !string.Equals(metadata.GetString(type.Name), typeName, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var fieldHandle in type.GetFields())
                {
                    var field = metadata.GetFieldDefinition(fieldHandle);
                    if (!string.Equals(metadata.GetString(field.Name), fieldName, StringComparison.Ordinal) ||
                        (field.Attributes & FieldAttributes.Literal) == 0)
                    {
                        continue;
                    }

                    var constantHandle = field.GetDefaultValue();
                    if (constantHandle.IsNil)
                        return false;

                    var constant = metadata.GetConstant(constantHandle);
                    if (constant.TypeCode != ConstantTypeCode.Int32)
                        return false;

                    value = metadata.GetBlobReader(constant.Value).ReadInt32();
                    return true;
                }

                return false;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string FormatSpaceEngineersVersion(int version)
    {
        if (version <= 0)
            return string.Empty;

        var text = version.ToString(CultureInfo.InvariantCulture).PadLeft(7, '0');
        return $"{text[..1]}.{text.Substring(1, 3)}.{text.Substring(4, 3)}";
    }

    private static string GetNonPlaceholderFileVersion(string path)
    {
        var version = GetFileVersion(path);
        return IsPlaceholderAssemblyVersion(version) ? string.Empty : version;
    }

    private static bool IsPlaceholderAssemblyVersion(string version)
    {
        var normalized = version.Trim();
        return string.Equals(normalized, "1.0.0", StringComparison.Ordinal) ||
               string.Equals(normalized, "1.0.0.0", StringComparison.Ordinal);
    }

    private static string GetFileVersion(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            var fileVersion = FirstNonEmpty(version.ProductVersion, version.FileVersion);
            if (!string.IsNullOrWhiteSpace(fileVersion))
                return fileVersion;
        }
        catch
        {
        }

        try
        {
            return AssemblyName.GetAssemblyName(path).Version?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static InstalledMagnetarRelease? ReadInstalledMagnetarRelease(string installDirectory)
    {
        var markerPath = GetMagnetarReleaseMarkerPath(installDirectory);
        if (!File.Exists(markerPath))
            return null;

        try
        {
            var json = File.ReadAllText(markerPath);
            return JsonSerializer.Deserialize<InstalledMagnetarRelease>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteInstalledMagnetarReleaseAsync(
        string installDirectory,
        MagnetarArchiveReference archive,
        CancellationToken cancellationToken)
    {
        var installed = new InstalledMagnetarRelease
        {
            SourceKind = archive.SourceKind,
            ReleaseTagName = archive.ReleaseTagName,
            AssetName = archive.AssetName,
            ArchiveUrl = archive.ArchiveUrl,
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(installed, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(GetMagnetarReleaseMarkerPath(installDirectory), json, cancellationToken);
    }

    private static string GetMagnetarReleaseMarkerPath(string installDirectory) =>
        Path.Combine(installDirectory, MagnetarReleaseMarkerFileName);

    private static string InferArchiveAssetName(string archiveUrl)
    {
        if (Uri.TryCreate(archiveUrl, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }

        var queryIndex = archiveUrl.IndexOf('?', StringComparison.Ordinal);
        var trimmed = queryIndex < 0 ? archiveUrl : archiveUrl[..queryIndex];
        return Path.GetFileName(trimmed);
    }

    private static bool MatchesAssetPattern(string assetName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        if (!pattern.Contains('*', StringComparison.Ordinal))
            return string.Equals(assetName, pattern, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(assetName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GetWindowsMagnetarLauncherFileName(ManagedServerRuntime runtime) => runtime switch
    {
        ManagedServerRuntime.NetFramework48 => "MagnetarLegacy.exe",
        _ => "MagnetarInterim.exe",
    };

    // The Windows archive root is a single Magnetar/ folder holding both launcher exes
    // and a Libraries/ subfolder. Locate it by the Interim exe with a sibling Libraries/
    // directory, then copy the whole folder so both runtimes are available.
    private static string? FindWindowsMagnetarSource(string extractionRoot)
    {
        return Directory.GetFiles(extractionRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), "MagnetarInterim.exe", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .FirstOrDefault(directory => FindImmediateDirectory(directory!, "Libraries") is not null);
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
            "DedicatedServer64 path not found. Set QUASAR_DS64_PATH, install steamcmd for managed DS download, or point server executable at SpaceEngineersDedicated once so Quasar can infer -ds64.");
    }

    private Task<string> TryEnsureManagedDedicatedServerInstallAsync(CancellationToken cancellationToken) =>
        TryEnsureManagedDedicatedServerInstallAsync(cancellationToken, steamCmdPath: null, progress: null);

    private async Task<string> TryEnsureManagedDedicatedServerInstallAsync(
        CancellationToken cancellationToken,
        string? steamCmdPath,
        IProgress<ManagedRuntimeInstallProgress>? progress)
    {
        var dedicatedServer64Path = Path.Combine(_options.DedicatedServerInstallDirectory, "DedicatedServer64");
        var hadValidInstall = IsValidDedicatedServer64Directory(dedicatedServer64Path);

        steamCmdPath = string.IsNullOrWhiteSpace(steamCmdPath)
            ? await ResolveSteamCmdPathAsync(cancellationToken)
            : steamCmdPath.Trim();
        if (string.IsNullOrWhiteSpace(steamCmdPath))
            return hadValidInstall ? dedicatedServer64Path : string.Empty;

        await _dedicatedServerInstallLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; attempt <= DedicatedServerInstallMaxAttempts; attempt++)
            {
                hadValidInstall = IsValidDedicatedServer64Directory(dedicatedServer64Path);

                Directory.CreateDirectory(_options.DedicatedServerInstallDirectory);

                _logger.LogInformation(
                    "{Action} Space Engineers Dedicated Server via SteamCMD (attempt {Attempt}/{MaxAttempts})...",
                    hadValidInstall ? "Updating" : "Downloading",
                    attempt,
                    DedicatedServerInstallMaxAttempts);
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.DedicatedServer,
                    hadValidInstall ? ManagedRuntimeInstallPhase.Installing : ManagedRuntimeInstallPhase.Downloading,
                    $"{(hadValidInstall ? "Updating" : "Downloading")} Space Engineers Dedicated Server via SteamCMD (attempt {attempt}/{DedicatedServerInstallMaxAttempts}).",
                    Path: dedicatedServer64Path));
                using var process = new Process
                {
                    StartInfo = CreateSteamCmdStartInfo(
                        steamCmdPath,
                        BuildDedicatedServerUpdateArguments(_options.DedicatedServerInstallDirectory)),
                };

                try
                {
                    if (!process.Start())
                    {
                        if (attempt < DedicatedServerInstallMaxAttempts)
                            continue;

                        return string.Empty;
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed starting steamcmd for managed DS install attempt {Attempt}/{MaxAttempts}.",
                        attempt,
                        DedicatedServerInstallMaxAttempts);
                    if (attempt < DedicatedServerInstallMaxAttempts)
                        continue;

                    return string.Empty;
                }

                var result = await WaitForSteamCmdProcessAsync(
                    process,
                    $"managed DS install attempt {attempt}/{DedicatedServerInstallMaxAttempts}",
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "steamcmd failed installing/updating managed DS on attempt {Attempt}/{MaxAttempts}. ExitCode={ExitCode}. Stdout={Stdout}. Stderr={Stderr}",
                        attempt,
                        DedicatedServerInstallMaxAttempts,
                        result.ExitCode,
                        TrimForLog(result.Stdout),
                        TrimForLog(result.Stderr));
                    if (attempt < DedicatedServerInstallMaxAttempts)
                        continue;

                    return hadValidInstall ? dedicatedServer64Path : string.Empty;
                }

                if (!IsValidDedicatedServer64Directory(dedicatedServer64Path))
                {
                    _logger.LogWarning(
                        "steamcmd completed but DedicatedServer64 not found under {Path} on attempt {Attempt}/{MaxAttempts}.",
                        _options.DedicatedServerInstallDirectory,
                        attempt,
                        DedicatedServerInstallMaxAttempts);
                    if (attempt < DedicatedServerInstallMaxAttempts)
                        continue;

                    return string.Empty;
                }

                _logger.LogInformation("{Action} managed DedicatedServer64 into {Path}.", hadValidInstall ? "Updated" : "Installed", dedicatedServer64Path);
                return dedicatedServer64Path;
            }

            return hadValidInstall ? dedicatedServer64Path : string.Empty;
        }
        finally
        {
            _dedicatedServerInstallLock.Release();
        }
    }

    private async Task<string> ResolveSteamCmdPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.SteamCmdPath))
            return File.Exists(_options.SteamCmdPath) ? _options.SteamCmdPath : string.Empty;

        var managedPath = FindSteamCmdExecutable(_options.SteamCmdInstallDirectory);
        if (!string.IsNullOrWhiteSpace(managedPath))
        {
            EnsureSteamCmdExecutableBits(_options.SteamCmdInstallDirectory);
            return managedPath;
        }

        var pathCandidate = ResolveSteamCmdPathFromEnvironment();
        if (!string.IsNullOrWhiteSpace(pathCandidate))
            return pathCandidate;

        return await TryEnsureManagedSteamCmdInstallAsync(cancellationToken);
    }

    private static string ResolveSteamCmdPathFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in SteamCmdFileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return string.Empty;
    }

    private Task<string> TryEnsureManagedSteamCmdInstallAsync(CancellationToken cancellationToken) =>
        EnsureManagedSteamCmdInstallAsync(progress: null, cancellationToken);

    private async Task<string> EnsureManagedSteamCmdInstallAsync(
        IProgress<ManagedRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _steamCmdInstallLock.WaitAsync(cancellationToken);
        try
        {
            var steamCmdPath = FindSteamCmdExecutable(_options.SteamCmdInstallDirectory);
            if (!string.IsNullOrWhiteSpace(steamCmdPath))
            {
                EnsureSteamCmdExecutableBits(_options.SteamCmdInstallDirectory);
                return steamCmdPath;
            }

            Directory.CreateDirectory(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory());
            var extractRoot = Path.Combine(MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory(), $"steamcmd-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractRoot);

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);

                var archivePath = Path.Combine(extractRoot, "steamcmd-download" + InferArchiveExtension(_options.SteamCmdArchiveUrl));
                _logger.LogInformation("Downloading SteamCMD...");
                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.SteamCmd,
                    ManagedRuntimeInstallPhase.Downloading,
                    "Downloading SteamCMD archive."));
                using var response = await client.GetAsync(_options.SteamCmdArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Failed downloading SteamCMD archive from {Url}. Status={Status} {Reason}.",
                        _options.SteamCmdArchiveUrl,
                        (int)response.StatusCode,
                        response.ReasonPhrase);
                    return string.Empty;
                }

                await CopyToFileWithProgressAsync(
                    response.Content,
                    archivePath,
                    percent => progress?.Report(new ManagedRuntimeInstallProgress(
                        ManagedRuntimeInstallComponent.SteamCmd,
                        ManagedRuntimeInstallPhase.Downloading,
                        "Downloading SteamCMD archive.",
                        percent)),
                    cancellationToken);

                progress?.Report(new ManagedRuntimeInstallProgress(
                    ManagedRuntimeInstallComponent.SteamCmd,
                    ManagedRuntimeInstallPhase.Installing,
                    "Installing SteamCMD."));
                ExtractArchive(archivePath, extractRoot);
                TryDeleteFile(archivePath);

                if (Directory.Exists(_options.SteamCmdInstallDirectory))
                    Directory.Delete(_options.SteamCmdInstallDirectory, recursive: true);

                Directory.CreateDirectory(_options.SteamCmdInstallDirectory);
                CopyDirectory(extractRoot, _options.SteamCmdInstallDirectory);
                EnsureSteamCmdExecutableBits(_options.SteamCmdInstallDirectory);

                steamCmdPath = FindSteamCmdExecutable(_options.SteamCmdInstallDirectory);
                if (string.IsNullOrWhiteSpace(steamCmdPath))
                {
                    _logger.LogWarning("Downloaded SteamCMD archive did not contain a SteamCMD executable.");
                    return string.Empty;
                }

                _logger.LogInformation("Installed managed SteamCMD into {Path}.", _options.SteamCmdInstallDirectory);
                return steamCmdPath;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Failed installing managed SteamCMD.");
                return string.Empty;
            }
            finally
            {
                TryDeleteDirectory(extractRoot);
            }
        }
        finally
        {
            _steamCmdInstallLock.Release();
        }
    }

    private static string FindSteamCmdExecutable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return string.Empty;

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        var preferredFileNames = OperatingSystem.IsWindows()
            ? new[] { "steamcmd.exe", "steamcmd.bat" }
            : new[] { "steamcmd.sh", "steamcmd" };

        foreach (var preferredFileName in preferredFileNames)
        {
            var match = files.FirstOrDefault(file => string.Equals(
                Path.GetFileName(file),
                preferredFileName,
                StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
                return match;
        }

        return files.FirstOrDefault(file => SteamCmdFileNames.Any(fileName => string.Equals(
            Path.GetFileName(file),
            fileName,
            StringComparison.OrdinalIgnoreCase))) ?? string.Empty;
    }

    private async Task RunSteamCmdAsync(
        string steamCmdPath,
        string arguments,
        string action,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateSteamCmdStartInfo(steamCmdPath, arguments),
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"steamcmd did not start while {action}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed starting steamcmd while {action}.", exception);
        }

        var result = await WaitForSteamCmdProcessAsync(process, action, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"steamcmd failed while {action}. ExitCode={result.ExitCode}. Stdout={TrimForLog(result.Stdout)}. Stderr={TrimForLog(result.Stderr)}");
        }
    }

    private async Task<SteamCmdProcessResult> WaitForSteamCmdProcessAsync(
        Process process,
        string action,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.ApplicationStopping);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            KillSteamCmdProcessTree(process, action);
            await WaitForKilledSteamCmdAsync(process, action);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new SteamCmdProcessResult(process.ExitCode, stdout, stderr);
    }

    private void KillSteamCmdProcessTree(Process process, string action)
    {
        try
        {
            if (process.HasExited)
                return;

            _logger.LogInformation("Stopping steamcmd after cancellation while {Action}.", action);
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed stopping steamcmd after cancellation while {Action}.", action);
        }
    }

    private async Task WaitForKilledSteamCmdAsync(Process process, string action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SteamCmdKillWaitTimeout);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out waiting for killed steamcmd while {Action}.", action);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed waiting for killed steamcmd while {Action}.", action);
        }
    }

    private static string BuildDedicatedServerUpdateArguments(string installDirectory)
    {
        var forcePlatform = OperatingSystem.IsWindows()
            ? string.Empty
            : "+@sSteamCmdForcePlatformType windows ";

        return $"+force_install_dir {QuoteArgument(installDirectory)} {forcePlatform}+login anonymous +app_update {DedicatedServerAppId} validate +quit";
    }

    private static ProcessStartInfo CreateSteamCmdStartInfo(string steamCmdPath, string arguments)
    {
        var workingDirectory = Path.GetDirectoryName(steamCmdPath) ?? AppContext.BaseDirectory;
        var fileName = steamCmdPath;
        var processArguments = arguments;
        var extension = Path.GetExtension(steamCmdPath);

        if (OperatingSystem.IsWindows() &&
            (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            processArguments = $"/d /s /c \"\"{steamCmdPath}\" {arguments}\"";
        }
        else if (!OperatingSystem.IsWindows() &&
                 string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase))
        {
            fileName = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            processArguments = $"{QuoteArgument(steamCmdPath)} {arguments}";
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = processArguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
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

        return DedicatedServerExecutableNames.Any(fileName => File.Exists(Path.Combine(path, fileName))) &&
               DedicatedServerRequiredFileNames.All(fileName => File.Exists(Path.Combine(path, fileName)));
    }

    private IReadOnlyList<string> ResolveNativeLibrarySearchPaths()
    {
        if (!OperatingSystem.IsLinux())
            return [];

        var steamGameServerRuntimePath = ResolveSteamGameServerRuntimePath();
        return string.IsNullOrWhiteSpace(steamGameServerRuntimePath)
            ? []
            : [steamGameServerRuntimePath];
    }

    private string ResolveSteamGameServerRuntimePath()
    {
        foreach (var candidate in EnumerateSteamGameServerRuntimePathCandidates())
        {
            if (IsValidSteamGameServerRuntimeDirectory(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private IEnumerable<string> EnumerateSteamGameServerRuntimePathCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_options.SteamCmdInstallDirectory))
            yield return Path.Combine(_options.SteamCmdInstallDirectory, "linux64");

        var managedSteamCmdPath = FindSteamCmdExecutable(_options.SteamCmdInstallDirectory);
        var managedRuntimePath = TryGetSteamCmdRuntimeDirectory(managedSteamCmdPath);
        if (!string.IsNullOrWhiteSpace(managedRuntimePath))
            yield return managedRuntimePath;

        if (!string.IsNullOrWhiteSpace(_options.SteamCmdPath))
        {
            var configuredRuntimePath = TryGetSteamCmdRuntimeDirectory(_options.SteamCmdPath);
            if (!string.IsNullOrWhiteSpace(configuredRuntimePath))
                yield return configuredRuntimePath;
        }

        var environmentSteamCmdPath = ResolveSteamCmdPathFromEnvironment();
        var environmentRuntimePath = TryGetSteamCmdRuntimeDirectory(environmentSteamCmdPath);
        if (!string.IsNullOrWhiteSpace(environmentRuntimePath))
            yield return environmentRuntimePath;
    }

    private static string TryGetSteamCmdRuntimeDirectory(string steamCmdPath)
    {
        if (string.IsNullOrWhiteSpace(steamCmdPath))
            return string.Empty;

        var steamCmdDirectory = Path.GetDirectoryName(steamCmdPath.Trim());
        return string.IsNullOrWhiteSpace(steamCmdDirectory)
            ? string.Empty
            : Path.Combine(steamCmdDirectory, "linux64");
    }

    private static bool IsValidSteamGameServerRuntimeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return SteamGameServerRuntimeFileNames.All(fileName => File.Exists(Path.Combine(path, fileName)));
    }

    private static async Task CopyToFileWithProgressAsync(
        HttpContent content,
        string path,
        Action<int?> reportProgress,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        long totalRead = 0;
        var lastPercent = -1;
        var buffer = new byte[81920];

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (contentLength is > 0)
            {
                var percent = (int)Math.Clamp(totalRead * 100 / contentLength.Value, 0, 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    reportProgress(percent);
                }
            }
            else
            {
                reportProgress(null);
            }
        }

        reportProgress(100);
    }

    private static void ExtractArchive(string archivePath, string destinationRoot)
    {
        var kind = DetectArchiveKind(archivePath);
        switch (kind)
        {
            case ArchiveKind.Zip:
                ExtractZipArchive(archivePath, destinationRoot);
                return;
            case ArchiveKind.TarGz:
                ExtractReaderArchive(archivePath, destinationRoot);
                return;
            case ArchiveKind.SevenZip:
                ExtractSevenZipArchive(archivePath, destinationRoot);
                return;
            default:
                throw new InvalidOperationException($"Unsupported Magnetar archive format: {archivePath}");
        }
    }

    private static void ExtractReaderArchive(string archivePath, string destinationRoot)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        foreach (var entry in archive.Entries)
        {
            ExtractSharpCompressEntry(entry, destinationRoot);
        }
    }

    private static void ExtractSevenZipArchive(string archivePath, string destinationRoot)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        foreach (var entry in archive.Entries)
        {
            ExtractSharpCompressEntry(entry, destinationRoot);
        }
    }

    private static void ExtractZipArchive(string archivePath, string destinationRoot)
    {
        using var fileStream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            ExtractZipEntry(entry, destinationRoot);
        }
    }

    private static void ExtractSharpCompressEntry(IArchiveEntry entry, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(entry.Key))
            return;

        var destinationPath = ResolveArchiveEntryPath(entry.Key, destinationRoot);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(destinationPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var entryStream = entry.OpenEntryStream();
        using var destinationStream = File.Create(destinationPath);
        entryStream.CopyTo(destinationStream);
    }

    private static void ExtractZipEntry(ZipArchiveEntry entry, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(entry.FullName))
            return;

        var destinationPath = ResolveArchiveEntryPath(entry.FullName, destinationRoot);
        if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
            entry.FullName.EndsWith("\\", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(destinationPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static string ResolveArchiveEntryPath(string entryPath, string destinationRoot)
    {
        var normalizedEntryPath = entryPath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntryPath));
        var fullRoot = Path.GetFullPath(destinationRoot + Path.DirectorySeparatorChar);
        if (!destinationPath.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"Archive entry escapes extraction root: {entryPath}");

        return destinationPath;
    }

    private static ArchiveKind DetectArchiveKind(string archivePath)
    {
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveKind.TarGz;
        }

        Span<byte> header = stackalloc byte[8];
        using (var stream = File.OpenRead(archivePath))
        {
            var read = stream.Read(header);
            header = header[..read];
        }

        if (header.Length >= 6 &&
            header[0] == 0x37 &&
            header[1] == 0x7A &&
            header[2] == 0xBC &&
            header[3] == 0xAF &&
            header[4] == 0x27 &&
            header[5] == 0x1C)
        {
            return ArchiveKind.SevenZip;
        }

        if (header.Length >= 4 &&
            header[0] == 0x50 &&
            header[1] == 0x4B &&
            header[2] is 0x03 or 0x05 or 0x07 &&
            header[3] is 0x04 or 0x06 or 0x08)
        {
            return ArchiveKind.Zip;
        }

        var extension = Path.GetExtension(archivePath);
        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
            return ArchiveKind.TarGz;
        if (string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase))
            return ArchiveKind.SevenZip;
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            return ArchiveKind.Zip;

        return ArchiveKind.Unknown;
    }

    private static string InferArchiveExtension(string archiveUrl)
    {
        try
        {
            if (Uri.TryCreate(archiveUrl, UriKind.Absolute, out var uri))
            {
                var extension = Path.GetExtension(uri.AbsolutePath);
                if (uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    return ".tar.gz";
                if (string.Equals(extension, ".tgz", StringComparison.OrdinalIgnoreCase))
                    return ".tgz";
                if (string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return extension;
                }

                if (uri.Query.Contains("accept=zip", StringComparison.OrdinalIgnoreCase))
                    return ".zip";
            }
        }
        catch
        {
        }

        return ".archive";
    }

    private static MagnetarSource? FindMagnetarSource(string extractionRoot)
    {
        return Directory.GetFiles(extractionRoot, "*", SearchOption.AllDirectories)
            .Where(IsMagnetarLauncherFileName)
            .Select(path => new { LauncherPath = path, Directory = Path.GetDirectoryName(path) })
            .Where(path => !string.IsNullOrWhiteSpace(path.Directory))
            .Select(path => new
            {
                path.LauncherPath,
                Directory = path.Directory!,
                BinDirectory = FindImmediateDirectory(path.Directory!, "Bin"),
            })
            .Where(path => !string.IsNullOrWhiteSpace(path.BinDirectory))
            .Select(path => new MagnetarSource(path.Directory, path.LauncherPath, path.BinDirectory!))
            .FirstOrDefault();
    }

    private static bool IsMagnetarLauncherFileName(string path)
    {
        var fileName = Path.GetFileName(path);
        return MagnetarLauncherFileNames.Any(name => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindImmediateDirectory(string parentDirectory, string directoryName)
    {
        if (!Directory.Exists(parentDirectory))
            return null;

        return Directory.EnumerateDirectories(parentDirectory)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                directoryName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindImmediateFile(string parentDirectory, IReadOnlyCollection<string> fileNames)
    {
        if (!Directory.Exists(parentDirectory))
            return null;

        return Directory.EnumerateFiles(parentDirectory)
            .FirstOrDefault(path => fileNames.Any(fileName => string.Equals(
                Path.GetFileName(path),
                fileName,
                StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record MagnetarSource(
        string Directory,
        string LauncherPath,
        string BinDirectory);

    private sealed record MagnetarArchiveReference(
        string SourceKind,
        string ReleaseTagName,
        string AssetName,
        string ArchiveUrl)
    {
        public static MagnetarArchiveReference UnknownExisting { get; } = new(
            MagnetarArchiveSourceKinds.ExistingUnknown,
            string.Empty,
            string.Empty,
            string.Empty);

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ReleaseTagName) && !string.IsNullOrWhiteSpace(AssetName))
                    return $"{ReleaseTagName}/{AssetName}";
                if (!string.IsNullOrWhiteSpace(AssetName))
                    return AssetName;
                if (!string.IsNullOrWhiteSpace(ArchiveUrl))
                    return ArchiveUrl;
                return SourceKind;
            }
        }
    }

    private sealed class InstalledMagnetarRelease
    {
        public string SourceKind { get; set; } = string.Empty;

        public string ReleaseTagName { get; set; } = string.Empty;

        public string AssetName { get; set; } = string.Empty;

        public string ArchiveUrl { get; set; } = string.Empty;

        public DateTimeOffset InstalledAtUtc { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ReleaseTagName) && !string.IsNullOrWhiteSpace(AssetName))
                    return $"{ReleaseTagName}/{AssetName}";
                if (!string.IsNullOrWhiteSpace(AssetName))
                    return AssetName;
                if (!string.IsNullOrWhiteSpace(ArchiveUrl))
                    return ArchiveUrl;
                return SourceKind;
            }
        }
    }

    private static class MagnetarArchiveSourceKinds
    {
        public const string DirectUrl = "direct-url";
        public const string ExistingUnknown = "existing-unknown";
        public const string GitHubRelease = "github-release";
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

    private static void EnsureSteamCmdExecutableBits(string directory)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (SteamCmdFileNames.Any(fileName => string.Equals(
                    Path.GetFileName(file),
                    fileName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                EnsureExecutableBit(file);
            }
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

    private sealed record SteamCmdProcessResult(int ExitCode, string Stdout, string Stderr);
}

internal enum ArchiveKind
{
    Unknown,
    Zip,
    TarGz,
    SevenZip,
}

public sealed record ResolvedDedicatedServerRuntime(
    string ExecutablePath,
    string WorkingDirectory,
    string DedicatedServer64Path,
    IReadOnlyList<string> NativeLibrarySearchPaths);

public sealed record ManagedRuntimeReadiness(
    bool IsReady,
    string SteamCmdPath,
    string SteamCmdRuntimePath,
    string DedicatedServer64Path,
    string FailureMessage)
{
    public static ManagedRuntimeReadiness Failed(
        string failureMessage,
        string steamCmdPath,
        string steamCmdRuntimePath,
        string dedicatedServer64Path) =>
        new(false, steamCmdPath, steamCmdRuntimePath, dedicatedServer64Path, failureMessage);
}

public sealed record ManagedRuntimeVersionSnapshot(
    string SteamCmdPath,
    string SteamCmdVersion,
    string MagnetarPath,
    string MagnetarVersion,
    string DedicatedServer64Path,
    string DedicatedServerVersion);

public sealed record ManagedRuntimeInstallProgress(
    ManagedRuntimeInstallComponent Component,
    ManagedRuntimeInstallPhase Phase,
    string Message,
    int? Percent = null,
    string Path = "",
    string Version = "");

public enum ManagedRuntimeInstallComponent
{
    SteamCmd = 0,
    DedicatedServer = 1,
    Magnetar = 2,
}

public enum ManagedRuntimeInstallPhase
{
    Pending = 0,
    Checking = 1,
    Downloading = 2,
    Installing = 3,
    Ready = 4,
    Failed = 5,
}
