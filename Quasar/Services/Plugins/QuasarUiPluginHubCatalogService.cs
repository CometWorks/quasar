using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Quasar.Plugin.Abstractions.Manifests;
using Quasar.Services;

namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginHubCatalogService
{
    private const int CacheSchemaVersion = 1;
    private const string InstallMetadataFileName = "quasar-ui-plugin-install.json";
    private const string PluginAbstractionsAssemblyFileName = "Quasar.Plugin.Abstractions.dll";
    private const string MagnetarProtocolAssemblyFileName = "Magnetar.Protocol.dll";
    private const string CompanionOutputRelativeDirectory = ".quasar/companions";
    private static readonly int RequiredDotNetSdkMajor = Environment.Version.Major;

    public const string DefaultHubName = "QuasarHub";
    public const string DefaultHubRepo = "CometWorks/quasar-hub";
    public const string DefaultHubBranch = "main";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarUiPluginHubCatalogService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly QuasarUiPluginStateStore _pluginStates;
    private readonly QuasarUiPluginCatalog _uiPluginCatalog;
    private readonly SemaphoreSlim _dotNetSdkInstallLock = new(1, 1);
    private QuasarDotNetSdkStatus _dotNetSdkStatus;
    private bool _dotNetSdkInstallInProgress;
    private List<QuasarUiPluginHubEntry> _entries;

    public QuasarUiPluginHubCatalogService(
        ILogger<QuasarUiPluginHubCatalogService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        QuasarUiPluginStateStore pluginStates,
        QuasarUiPluginCatalog uiPluginCatalog)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _environment = environment;
        _pluginStates = pluginStates;
        _uiPluginCatalog = uiPluginCatalog;
        _dotNetSdkStatus = DetectDotNetSdkStatus();
        _entries = LoadCache();
    }

    public event Action? Changed;

    public DateTimeOffset? LastRefreshUtc { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public bool DotNetSdkInstallInProgress
    {
        get
        {
            lock (_sync)
                return _dotNetSdkInstallInProgress;
        }
    }

    public IReadOnlyList<QuasarUiPluginHubEntry> GetEntries()
    {
        lock (_sync)
        {
            return _entries
                .Select(Clone)
                .OrderBy(entry => entry.Hidden)
                .ThenBy(entry => entry.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.CatalogId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public QuasarDotNetSdkStatus GetDotNetSdkStatus()
    {
        lock (_sync)
            return _dotNetSdkStatus;
    }

    public QuasarDotNetSdkStatus RefreshDotNetSdkStatus()
    {
        var status = DetectDotNetSdkStatus();
        lock (_sync)
            _dotNetSdkStatus = status;

        Changed?.Invoke();
        return status;
    }

    public QuasarDotNetSdkInstallAvailability GetDotNetSdkInstallAvailability()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new QuasarDotNetSdkInstallAvailability(
                CanInstall: false,
                "Automatic SDK install is only available on Linux through install.sh.",
                string.Empty);
        }

        var scriptPath = GetInstallScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return new QuasarDotNetSdkInstallAvailability(
                CanInstall: false,
                "install.sh was not found beside Quasar. Install the .NET SDK manually, then refresh the SDK check.",
                string.Empty);
        }

        return new QuasarDotNetSdkInstallAvailability(
            CanInstall: true,
            "Run install.sh to install the .NET SDK required for source-built QuasarHub UI plugins.",
            scriptPath);
    }

    public async Task<QuasarDotNetSdkInstallResult> InstallDotNetSdkAsync(CancellationToken cancellationToken = default)
    {
        var sdkStatus = RefreshDotNetSdkStatus();
        if (sdkStatus.CanBuildUiPlugins)
        {
            return new QuasarDotNetSdkInstallResult(
                Succeeded: true,
                sdkStatus.Message,
                string.Empty);
        }

        var availability = GetDotNetSdkInstallAvailability();
        if (!availability.CanInstall)
        {
            return new QuasarDotNetSdkInstallResult(
                Succeeded: false,
                availability.Message,
                string.Empty);
        }

        if (!await _dotNetSdkInstallLock.WaitAsync(0, cancellationToken))
        {
            return new QuasarDotNetSdkInstallResult(
                Succeeded: false,
                "SDK install is already running.",
                string.Empty);
        }

        SetDotNetSdkInstallInProgress(true);
        try
        {
            _logger.LogInformation(
                "Running {ScriptPath} to install the .NET SDK for QuasarHub UI plugin builds.",
                availability.InstallScriptPath);

            var scriptDirectory = Path.GetDirectoryName(availability.InstallScriptPath);
            var startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrWhiteSpace(scriptDirectory) ? AppContext.BaseDirectory : scriptDirectory,
            };
            startInfo.ArgumentList.Add(availability.InstallScriptPath);
            startInfo.ArgumentList.Add("--install-ui-plugin-sdk-only");
            startInfo.ArgumentList.Add("--yes");

            var processResult = await RunProcessCaptureAsync(
                startInfo,
                "install.sh --install-ui-plugin-sdk-only",
                cancellationToken);
            var refreshedStatus = RefreshDotNetSdkStatus();
            if (processResult.ExitCode == 0 && refreshedStatus.CanBuildUiPlugins)
            {
                return new QuasarDotNetSdkInstallResult(
                    Succeeded: true,
                    refreshedStatus.Message,
                    processResult.Output);
            }

            var message = processResult.ExitCode == 0
                ? refreshedStatus.Message
                : $"SDK install script failed with exit code {processResult.ExitCode}.";
            return new QuasarDotNetSdkInstallResult(
                Succeeded: false,
                message,
                processResult.Output);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to run install.sh for .NET SDK installation.");
            return new QuasarDotNetSdkInstallResult(
                Succeeded: false,
                exception.Message,
                string.Empty);
        }
        finally
        {
            SetDotNetSdkInstallInProgress(false);
            _dotNetSdkInstallLock.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, QuasarUiPluginHubEntry>(StringComparer.OrdinalIgnoreCase);
        var archiveUrl = $"https://github.com/{DefaultHubRepo}/archive/refs/heads/{DefaultHubBranch}.zip";

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            await using var archiveStream = await client.GetStreamAsync(archiveUrl, cancellationToken);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                await using var entryStream = entry.Open();
                try
                {
                    var document = XDocument.Load(entryStream, LoadOptions.None);
                    var root = document.Root;
                    if (root is null)
                        continue;

                    if (!string.Equals(GetValue(root, "PluginKind"), "QuasarUiPlugin", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var catalogId = GetValue(root, "Id");
                    if (string.IsNullOrWhiteSpace(catalogId))
                        continue;

                    entries[catalogId] = new QuasarUiPluginHubEntry
                    {
                        CatalogId = catalogId,
                        RepoId = GetValue(root, "RepoId"),
                        FriendlyName = GetValue(root, "FriendlyName", catalogId),
                        Author = GetValue(root, "Author"),
                        Tooltip = GetValue(root, "Tooltip"),
                        Description = GetValue(root, "Description"),
                        PluginKind = GetValue(root, "PluginKind"),
                        ProjectPath = GetValue(root, "ProjectPath"),
                        PackageManifest = GetValue(root, "PackageManifest", QuasarPluginPackageManifestReader.ManifestFileName),
                        QuasarVersion = GetValue(root, "QuasarVersion"),
                        Commit = GetValue(root, "Commit"),
                        ManifestRepo = DefaultHubRepo,
                        ManifestBranch = DefaultHubBranch,
                        ManifestFile = GetArchiveEntryRelativePath(entry.FullName),
                        Hidden = GetBoolean(root, "Hidden"),
                        ImplicitLoading = GetBoolean(root, "ImplicitLoading"),
                        DependencyIds = GetList(root, "DependencyIds", "DependencyId"),
                        CompanionPluginIds = GetList(root, "CompanionPluginIds", "CompanionPluginId"),
                        Platforms = GetList(root, "Platforms", "Platform"),
                    };
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Failed to parse QuasarHub entry {EntryName}", entry.FullName);
                }
            }

            var normalized = entries.Values
                .OrderBy(entry => entry.Hidden)
                .ThenBy(entry => entry.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.CatalogId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_sync)
                _entries = normalized;

            LastRefreshUtc = DateTimeOffset.UtcNow;
            LastError = string.Empty;
            await SaveCacheAsync(normalized, cancellationToken);
            await InstallOrUpdateImplicitPluginsAsync(normalized, cancellationToken);
            Changed?.Invoke();
            _logger.LogInformation("Downloaded QuasarHub catalog with {Count} entries.", normalized.Count);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Changed?.Invoke();
            _logger.LogWarning(exception, "Failed to refresh QuasarHub catalog.");
            throw;
        }
    }

    public QuasarUiPluginInstallState GetInstallState(QuasarUiPluginHubEntry entry)
    {
        var installDirectory = GetInstallDirectory(entry);
        var manifestPath = Path.Combine(installDirectory, QuasarPluginPackageManifestReader.ManifestFileName);
        var metadataPath = Path.Combine(installDirectory, InstallMetadataFileName);
        var metadata = ReadInstallMetadata(metadataPath);

        QuasarPluginManifest? manifest = null;
        string error = string.Empty;
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = QuasarPluginPackageManifestReader.Read(manifestPath);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }

        var runtimePluginId = manifest?.Id ?? string.Empty;
        var pluginState = string.IsNullOrWhiteSpace(runtimePluginId)
            ? new QuasarUiPluginPackageState { Enabled = true }
            : _pluginStates.GetState(runtimePluginId);

        return new QuasarUiPluginInstallState
        {
            InstallDirectory = installDirectory,
            Installed = Directory.Exists(installDirectory),
            ManifestPath = manifestPath,
            RuntimePluginId = runtimePluginId,
            RuntimeDisplayName = manifest?.DisplayName ?? string.Empty,
            InstalledCommit = metadata?.Commit ?? string.Empty,
            InstalledAtUtc = metadata?.InstalledAtUtc,
            Enabled = pluginState.Enabled,
            Error = error,
        };
    }

    public async Task InstallOrUpdateAsync(
        QuasarUiPluginHubEntry entry,
        CancellationToken cancellationToken = default,
        bool enableAfterInstall = true)
    {
        var sdkStatus = RefreshDotNetSdkStatus();
        if (!sdkStatus.CanBuildUiPlugins)
            throw new InvalidOperationException(sdkStatus.Message);

        if (string.IsNullOrWhiteSpace(entry.RepoId))
            throw new InvalidOperationException("QuasarHub entry has no repository.");

        if (string.IsNullOrWhiteSpace(entry.Commit))
            throw new InvalidOperationException("QuasarHub entry has no pinned commit.");

        var packageManifest = string.IsNullOrWhiteSpace(entry.PackageManifest)
            ? QuasarPluginPackageManifestReader.ManifestFileName
            : entry.PackageManifest.Trim().Replace('\\', '/');
        if (!string.Equals(packageManifest, QuasarPluginPackageManifestReader.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only root quasar-plugin.json package manifests are supported in this installer.");

        var installDirectory = GetInstallDirectory(entry);
        var installerRoot = GetInstallerCacheDirectory();
        var stagingDirectory = Path.Combine(installerRoot, $"staging-{SanitizePathSegment(entry.CatalogId)}-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(installerRoot, $"backup-{SanitizePathSegment(entry.CatalogId)}-{Guid.NewGuid():N}");
        var installDirectoryReplaced = false;

        Directory.CreateDirectory(installerRoot);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var repositoryRoot = await PrepareRepositoryRootAsync(entry, stagingDirectory, cancellationToken);

            var manifestPath = Path.Combine(repositoryRoot, QuasarPluginPackageManifestReader.ManifestFileName);
            var manifest = QuasarPluginPackageManifestReader.Read(manifestPath);

            var projectPath = Path.GetFullPath(Path.Combine(repositoryRoot, manifest.ProjectPath));
            if (!File.Exists(projectPath))
                throw new FileNotFoundException("Quasar UI plugin project file was not found.", projectPath);

            await RunQuasarUiPluginBuildAsync(projectPath, cancellationToken);
            await BuildOwnedCompanionPluginsAsync(repositoryRoot, manifest, cancellationToken);

            if (Directory.Exists(installDirectory))
                Directory.Move(installDirectory, backupDirectory);

            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);
            Directory.Move(repositoryRoot, installDirectory);
            installDirectoryReplaced = true;
            await WriteInstallMetadataAsync(installDirectory, entry, manifest, cancellationToken);
            await _pluginStates.SetEnabledAsync(manifest.Id, enableAfterInstall, cancellationToken);

            if (Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, recursive: true);

            _logger.LogInformation(
                "Installed Quasar UI plugin {Plugin} from {Repo}@{Commit} into {Directory}.",
                entry.FriendlyName,
                entry.RepoId,
                entry.Commit,
                installDirectory);
        }
        catch
        {
            if (Directory.Exists(backupDirectory))
            {
                if (installDirectoryReplaced)
                    TryDeleteDirectory(installDirectory);
                Directory.Move(backupDirectory, installDirectory);
            }
            else if (installDirectoryReplaced && Directory.Exists(installDirectory))
            {
                TryDeleteDirectory(installDirectory);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteDirectory(backupDirectory);
            Changed?.Invoke();
        }
    }

    private async Task InstallOrUpdateImplicitPluginsAsync(
        IReadOnlyList<QuasarUiPluginHubEntry> entries,
        CancellationToken cancellationToken)
    {
        if (_uiPluginCatalog.SafeMode)
            return;

        foreach (var entry in entries.Where(entry => entry.ImplicitLoading))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var installState = GetInstallState(entry);
            var currentCommitInstalled = installState.Installed &&
                string.Equals(installState.InstalledCommit, entry.Commit, StringComparison.OrdinalIgnoreCase);
            if (currentCommitInstalled && string.IsNullOrWhiteSpace(installState.Error))
                continue;

            if (string.IsNullOrWhiteSpace(entry.RepoId) || string.IsNullOrWhiteSpace(entry.Commit))
            {
                _logger.LogWarning(
                    "Implicit Quasar UI plugin {Plugin} cannot be installed because its QuasarHub entry has no repository or commit.",
                    GetEntryLogName(entry));
                continue;
            }

            var enableAfterInstall = !installState.Installed || installState.Enabled;
            try
            {
                await InstallOrUpdateAsync(entry, cancellationToken, enableAfterInstall);
                _logger.LogInformation(
                    "Implicit Quasar UI plugin {Plugin} installed or updated from QuasarHub.",
                    GetEntryLogName(entry));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Implicit Quasar UI plugin {Plugin} install or update failed.",
                    GetEntryLogName(entry));
            }
        }
    }

    public async Task RemoveAsync(QuasarUiPluginHubEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installState = GetInstallState(entry);
        var installDirectory = GetInstallDirectory(entry);
        if (Directory.Exists(installDirectory))
            Directory.Delete(installDirectory, recursive: true);

        if (!string.IsNullOrWhiteSpace(installState.RuntimePluginId))
            await _pluginStates.RemoveAsync(installState.RuntimePluginId, cancellationToken);

        Changed?.Invoke();
        _logger.LogInformation("Removed Quasar UI plugin package {Plugin} from {Directory}.", entry.FriendlyName, installDirectory);
    }

    public string GetInstallDirectory(QuasarUiPluginHubEntry entry) =>
        Path.Combine(GetPluginRootDirectory(), SanitizePathSegment(GetInstallKey(entry)));

    public static string GetRepositoryUrl(string sourceRepo)
    {
        var repo = sourceRepo?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(repo))
            return string.Empty;

        if (Uri.TryCreate(repo, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return uri.ToString();
        }

        return repo.Contains('/', StringComparison.Ordinal)
            ? $"https://github.com/{repo.Trim('/')}"
            : string.Empty;
    }

    private async Task DownloadArchiveAsync(QuasarUiPluginHubEntry entry, string archivePath, CancellationToken cancellationToken)
    {
        var archiveUrl = $"https://github.com/{entry.RepoId.Trim().Trim('/')}/archive/{entry.Commit.Trim()}.zip";
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        await using var stream = await client.GetStreamAsync(archiveUrl, cancellationToken);
        await using var output = File.Create(archivePath);
        await stream.CopyToAsync(output, cancellationToken);
    }

    private async Task<string> PrepareRepositoryRootAsync(
        QuasarUiPluginHubEntry entry,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PrepareRepositoryRootFromGitAsync(entry, stagingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Git checkout failed for Quasar UI plugin {Plugin}; falling back to GitHub archive download.", entry.FriendlyName);
            return await PrepareRepositoryRootFromArchiveAsync(entry, stagingDirectory, cancellationToken);
        }
    }

    private async Task<string> PrepareRepositoryRootFromGitAsync(
        QuasarUiPluginHubEntry entry,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var repositoryUrl = GetRepositoryUrl(entry.RepoId);
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            throw new InvalidOperationException("QuasarHub entry has an invalid repository.");

        var sourceCacheRoot = GetSourceCacheDirectory();
        var sourceCacheDirectory = Path.Combine(sourceCacheRoot, SanitizePathSegment(entry.RepoId.Replace('/', '_').Replace('\\', '_')));
        Directory.CreateDirectory(sourceCacheRoot);

        if (Directory.Exists(Path.Combine(sourceCacheDirectory, ".git")))
        {
            await RunGitAsync(sourceCacheDirectory, cancellationToken, "fetch", "--tags", "--prune", "origin");
        }
        else
        {
            TryDeleteDirectory(sourceCacheDirectory);
            await RunGitAsync(sourceCacheRoot, cancellationToken, "clone", "--no-checkout", repositoryUrl, sourceCacheDirectory);
        }

        await RunGitAsync(sourceCacheDirectory, cancellationToken, "checkout", "--force", entry.Commit.Trim());
        await RunGitAsync(sourceCacheDirectory, cancellationToken, "clean", "-xdf");

        var sourceCopyDirectory = Path.Combine(stagingDirectory, "source");
        CopyDirectory(sourceCacheDirectory, sourceCopyDirectory, excludeGitDirectory: true);
        return sourceCopyDirectory;
    }

    private async Task<string> PrepareRepositoryRootFromArchiveAsync(
        QuasarUiPluginHubEntry entry,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(stagingDirectory, "source.zip");
        await DownloadArchiveAsync(entry, archivePath, cancellationToken);

        var extractDirectory = Path.Combine(stagingDirectory, "extract");
        ZipFile.ExtractToDirectory(archivePath, extractDirectory);
        return GetExtractedRepositoryRoot(extractDirectory);
    }

    private static Task RunGitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return RunProcessAsync(startInfo, "git", cancellationToken);
    }

    private async Task BuildOwnedCompanionPluginsAsync(
        string repositoryRoot,
        QuasarPluginManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var companion in manifest.CompanionPluginManifests.Where(companion => companion.IsOwned))
        {
            var projectPath = Path.GetFullPath(Path.Combine(repositoryRoot, companion.ProjectPath!));
            EnsurePathInside(repositoryRoot, projectPath, "Companion plugin project path escapes the plugin repository.");
            if (!File.Exists(projectPath))
                throw new FileNotFoundException($"Companion plugin project file was not found for '{companion.Id}'.", projectPath);

            var outputDirectory = Path.Combine(
                repositoryRoot,
                CompanionOutputRelativeDirectory,
                SanitizePathSegment(companion.Id));
            TryDeleteDirectory(outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            await RunCompanionPluginBuildAsync(projectPath, outputDirectory, cancellationToken);
        }
    }

    private async Task RunQuasarUiPluginBuildAsync(string projectPath, CancellationToken cancellationToken)
    {
        var buildConfiguration = GetBuildConfiguration();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(buildConfiguration);
        startInfo.ArgumentList.Add("-v:minimal");

        var abstractionsAssemblyPath = GetPluginAbstractionsAssemblyPath();
        if (string.IsNullOrWhiteSpace(abstractionsAssemblyPath))
        {
            throw new InvalidOperationException(
                "Quasar.Plugin.Abstractions.dll was not found on disk. Quasar UI plugin installs require the abstraction assembly beside the running worker so plugin adapters can build against the active contract.");
        }

        startInfo.ArgumentList.Add($"-p:QuasarPluginAbstractionsAssembly={abstractionsAssemblyPath}");

        await RunProcessAsync(startInfo, "dotnet build", cancellationToken);
    }

    private async Task RunCompanionPluginBuildAsync(
        string projectPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var buildConfiguration = GetBuildConfiguration();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(buildConfiguration);
        startInfo.ArgumentList.Add("-v:minimal");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-p:Platform=x64");
        startInfo.ArgumentList.Add("-p:CopyLocalLockFileAssemblies=true");

        var protocolAssemblyPath = GetMagnetarProtocolAssemblyPath();
        if (string.IsNullOrWhiteSpace(protocolAssemblyPath))
        {
            throw new InvalidOperationException(
                "Magnetar.Protocol.dll was not found on disk. Quasar UI plugin companion builds require the protocol assembly from the running Quasar worker or staged Agent folder.");
        }

        startInfo.ArgumentList.Add($"-p:MagnetarProtocolAssembly={protocolAssemblyPath}");

        await RunProcessAsync(startInfo, "dotnet build", cancellationToken);
    }

    private static string GetPluginAbstractionsAssemblyPath()
    {
        var baseDirectoryPath = Path.Combine(AppContext.BaseDirectory, PluginAbstractionsAssemblyFileName);
        return File.Exists(baseDirectoryPath) ? baseDirectoryPath : string.Empty;
    }

    private static string GetMagnetarProtocolAssemblyPath()
    {
        var baseDirectoryPath = Path.Combine(AppContext.BaseDirectory, MagnetarProtocolAssemblyFileName);
        if (File.Exists(baseDirectoryPath))
            return baseDirectoryPath;

        var agentDirectoryPath = Path.Combine(AppContext.BaseDirectory, "Agent", MagnetarProtocolAssemblyFileName);
        return File.Exists(agentDirectoryPath) ? agentDirectoryPath : string.Empty;
    }

    private static async Task RunProcessAsync(ProcessStartInfo startInfo, string label, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Failed to start {label}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var output = string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()])
                .Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"{label} failed with exit code {process.ExitCode}."
                : output);
        }
    }

    private static async Task<ProcessCaptureResult> RunProcessCaptureAsync(
        ProcessStartInfo startInfo,
        string label,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Failed to start {label}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()])
            .Trim();
        return new ProcessCaptureResult(process.ExitCode, output);
    }

    private string GetBuildConfiguration() =>
        Environment.GetEnvironmentVariable("QUASAR_UI_PLUGIN_BUILD_CONFIGURATION")
        ?? _configuration["Quasar:Plugins:BuildConfiguration"]
        ?? (_environment.IsDevelopment() ? "Debug" : "Release");

    private List<QuasarUiPluginHubEntry> LoadCache()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path))
                return [];

            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<QuasarUiPluginHubCatalogCache>(json, JsonOptions);
            if (cache?.SchemaVersion != CacheSchemaVersion)
                return [];

            LastRefreshUtc = cache.LastRefreshUtc;
            return cache.Entries
                .Select(Clone)
                .OrderBy(entry => entry.Hidden)
                .ThenBy(entry => entry.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.CatalogId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to load QuasarHub catalog cache.");
            return [];
        }
    }

    private async Task SaveCacheAsync(IReadOnlyList<QuasarUiPluginHubEntry> entries, CancellationToken cancellationToken)
    {
        var payload = new QuasarUiPluginHubCatalogCache
        {
            SchemaVersion = CacheSchemaVersion,
            LastRefreshUtc = LastRefreshUtc,
            Entries = entries.Select(Clone).ToList(),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(GetCachePath(), json, cancellationToken);
    }

    private static async Task WriteInstallMetadataAsync(
        string installDirectory,
        QuasarUiPluginHubEntry entry,
        QuasarPluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var metadata = new QuasarUiPluginInstallMetadata
        {
            CatalogId = entry.CatalogId,
            RepoId = entry.RepoId,
            Commit = entry.Commit,
            RuntimePluginId = manifest.Id,
            RuntimeDisplayName = manifest.DisplayName,
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(Path.Combine(installDirectory, InstallMetadataFileName), json, cancellationToken);
    }

    private static QuasarUiPluginInstallMetadata? ReadInstallMetadata(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<QuasarUiPluginInstallMetadata>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GetExtractedRepositoryRoot(string extractDirectory)
    {
        var childDirectories = Directory.EnumerateDirectories(extractDirectory).ToList();
        return childDirectories.Count == 1
            ? childDirectories[0]
            : extractDirectory;
    }

    private static string GetValue(XElement root, string name, string fallback = "") =>
        root.Element(name)?.Value?.Trim() ?? fallback;

    private static bool GetBoolean(XElement root, string name)
    {
        return bool.TryParse(root.Element(name)?.Value?.Trim(), out var value) && value;
    }

    private static IReadOnlyList<string> GetList(XElement root, string containerName, string itemName)
    {
        var container = root.Element(containerName);
        if (container is null)
            return [];

        var values = container.Elements(itemName)
            .Select(element => element.Value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (values.Count > 0)
            return values;

        return (container.Value ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static string GetArchiveEntryRelativePath(string fullName)
    {
        var normalized = (fullName ?? string.Empty).Replace('\\', '/').Trim('/');
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static QuasarUiPluginHubEntry Clone(QuasarUiPluginHubEntry entry) =>
        new()
        {
            CatalogId = entry.CatalogId,
            RepoId = entry.RepoId,
            FriendlyName = entry.FriendlyName,
            Author = entry.Author,
            Tooltip = entry.Tooltip,
            Description = entry.Description,
            PluginKind = entry.PluginKind,
            ProjectPath = entry.ProjectPath,
            PackageManifest = entry.PackageManifest,
            QuasarVersion = entry.QuasarVersion,
            Commit = entry.Commit,
            ManifestRepo = entry.ManifestRepo,
            ManifestBranch = entry.ManifestBranch,
            ManifestFile = entry.ManifestFile,
            Hidden = entry.Hidden,
            ImplicitLoading = entry.ImplicitLoading,
            DependencyIds = entry.DependencyIds.ToList(),
            CompanionPluginIds = entry.CompanionPluginIds.ToList(),
            Platforms = entry.Platforms.ToList(),
        };

    private static string GetEntryLogName(QuasarUiPluginHubEntry entry) =>
        string.IsNullOrWhiteSpace(entry.FriendlyName) ? entry.CatalogId : entry.FriendlyName;

    private static string GetInstallKey(QuasarUiPluginHubEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.CatalogId))
            return entry.CatalogId;

        return string.IsNullOrWhiteSpace(entry.RepoId)
            ? entry.FriendlyName
            : entry.RepoId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? entry.RepoId;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "plugin" : sanitized;
    }

    private static void EnsurePathInside(string rootDirectory, string path, string message)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException(message);
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

    private static QuasarDotNetSdkStatus DetectDotNetSdkStatus()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--list-sdks");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return CreateMissingSdkStatus("Failed to start dotnet --list-sdks.");

            if (!process.WaitForExit(5000))
            {
                TryKillProcess(process);
                return CreateMissingSdkStatus("dotnet --list-sdks did not finish within 5 seconds.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                var output = string.Join(Environment.NewLine, [stdout.Trim(), stderr.Trim()]).Trim();
                return CreateMissingSdkStatus(string.IsNullOrWhiteSpace(output)
                    ? $"dotnet --list-sdks failed with exit code {process.ExitCode}."
                    : output);
            }

            var sdkVersions = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(GetSdkVersion)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .ToArray();
            var requiredSdk = sdkVersions.FirstOrDefault(version =>
                version.StartsWith($"{RequiredDotNetSdkMajor}.", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(requiredSdk))
            {
                return new QuasarDotNetSdkStatus(
                    RequiredDotNetSdkMajor,
                    DotNetOnPath: true,
                    RequiredSdkAvailable: true,
                    sdkVersions,
                    $".NET {RequiredDotNetSdkMajor} SDK {requiredSdk} is available for Quasar UI plugin builds.");
            }

            var installed = sdkVersions.Length == 0 ? "none" : string.Join(", ", sdkVersions);
            return new QuasarDotNetSdkStatus(
                RequiredDotNetSdkMajor,
                DotNetOnPath: true,
                RequiredSdkAvailable: false,
                sdkVersions,
                $".NET {RequiredDotNetSdkMajor} SDK is required to build Quasar UI plugins. Installed SDKs: {installed}.");
        }
        catch (Exception exception)
        {
            return CreateMissingSdkStatus(
                $"dotnet command was not found or could not be started. .NET {RequiredDotNetSdkMajor} SDK is required to build Quasar UI plugins. {exception.Message}");
        }
    }

    private static QuasarDotNetSdkStatus CreateMissingSdkStatus(string message) =>
        new(
            RequiredDotNetSdkMajor,
            DotNetOnPath: false,
            RequiredSdkAvailable: false,
            [],
            message);

    private void SetDotNetSdkInstallInProgress(bool inProgress)
    {
        lock (_sync)
            _dotNetSdkInstallInProgress = inProgress;

        Changed?.Invoke();
    }

    private string GetInstallScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "install.sh"),
            Path.Combine(_environment.ContentRootPath, "install.sh"),
            Path.Combine(_environment.ContentRootPath, "..", "install.sh"),
            Path.Combine(Directory.GetCurrentDirectory(), "install.sh"),
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists)
            ?? string.Empty;
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

    private static string GetPluginRootDirectory() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Plugins");

    private static string GetInstallerCacheDirectory() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Caches", "ui-plugin-installer");

    private static string GetCachePath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Caches", "ui-plugin-hub-catalog.json");

    private static string GetSourceCacheDirectory() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Caches", "ui-plugin-sources");

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, bool excludeGitDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }

        foreach (var sourceChildDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            if (excludeGitDirectory &&
                string.Equals(Path.GetFileName(sourceChildDirectory), ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationChildDirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceChildDirectory));
            CopyDirectory(sourceChildDirectory, destinationChildDirectory, excludeGitDirectory);
        }
    }

    private sealed class QuasarUiPluginHubCatalogCache
    {
        public int SchemaVersion { get; set; } = CacheSchemaVersion;

        public DateTimeOffset? LastRefreshUtc { get; set; }

        public List<QuasarUiPluginHubEntry> Entries { get; set; } = [];
    }

    private sealed record ProcessCaptureResult(int ExitCode, string Output);
}

public sealed record QuasarDotNetSdkInstallAvailability(
    bool CanInstall,
    string Message,
    string InstallScriptPath);

public sealed record QuasarDotNetSdkInstallResult(
    bool Succeeded,
    string Message,
    string Output);

public sealed record QuasarDotNetSdkStatus(
    int RequiredMajorVersion,
    bool DotNetOnPath,
    bool RequiredSdkAvailable,
    IReadOnlyList<string> InstalledSdkVersions,
    string Message)
{
    public bool CanBuildUiPlugins => DotNetOnPath && RequiredSdkAvailable;
}

public sealed class QuasarUiPluginHubEntry
{
    public string CatalogId { get; set; } = string.Empty;

    public string RepoId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Tooltip { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string PluginKind { get; set; } = string.Empty;

    public string ProjectPath { get; set; } = string.Empty;

    public string PackageManifest { get; set; } = QuasarPluginPackageManifestReader.ManifestFileName;

    public string QuasarVersion { get; set; } = string.Empty;

    public string Commit { get; set; } = string.Empty;

    public string ManifestRepo { get; set; } = string.Empty;

    public string ManifestBranch { get; set; } = string.Empty;

    public string ManifestFile { get; set; } = string.Empty;

    public bool Hidden { get; set; }

    public bool ImplicitLoading { get; set; }

    public IReadOnlyList<string> DependencyIds { get; set; } = [];

    public IReadOnlyList<string> CompanionPluginIds { get; set; } = [];

    public IReadOnlyList<string> Platforms { get; set; } = [];
}

public sealed class QuasarUiPluginInstallState
{
    public string InstallDirectory { get; set; } = string.Empty;

    public bool Installed { get; set; }

    public bool Enabled { get; set; } = true;

    public string ManifestPath { get; set; } = string.Empty;

    public string RuntimePluginId { get; set; } = string.Empty;

    public string RuntimeDisplayName { get; set; } = string.Empty;

    public string InstalledCommit { get; set; } = string.Empty;

    public DateTimeOffset? InstalledAtUtc { get; set; }

    public string Error { get; set; } = string.Empty;
}

public sealed class QuasarUiPluginInstallMetadata
{
    public string CatalogId { get; set; } = string.Empty;

    public string RepoId { get; set; } = string.Empty;

    public string Commit { get; set; } = string.Empty;

    public string RuntimePluginId { get; set; } = string.Empty;

    public string RuntimeDisplayName { get; set; } = string.Empty;

    public DateTimeOffset InstalledAtUtc { get; set; }
}
