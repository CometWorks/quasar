using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Services;

namespace Quasar.Services.Updates;

public sealed class QuasarUpdateService : BackgroundService
{
    private const string AppSettingsFileName = "appsettings.json";
    private const string AppSettingsReleaseBaseFileName = ".quasar-appsettings-release-base.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly QuasarUpdateOptions _options;
    private readonly WebServiceOptions _webOptions;
    private readonly ILogger<QuasarUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private QuasarUpdateSnapshot _snapshot;

    public QuasarUpdateService(
        IHttpClientFactory httpClientFactory,
        QuasarUpdateOptions options,
        WebServiceOptions webOptions,
        ILogger<QuasarUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _webOptions = webOptions;
        _logger = logger;
        _snapshot = new QuasarUpdateSnapshot
        {
            Enabled = options.Enabled,
            SupportedPlatform = OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
            CurrentVersion = webOptions.Version,
            CurrentBootstrapVersion = webOptions.BootstrapVersion,
            IncludePrerelease = options.IncludePrerelease,
            AutoStageWebUpdates = options.AutoStageWebUpdates,
            Status = QuasarUpdateStatus.Idle,
            Message = options.Enabled
                ? "Update checks pending."
                : "Update checks disabled.",
            LastChangedUtc = DateTimeOffset.UtcNow,
        };
    }

    public event Action? Changed;

    public QuasarUpdateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot with
            {
                Web = _snapshot.Web is null ? null : _snapshot.Web with { },
                WebReleases = _snapshot.WebReleases.Select(candidate => candidate with { }).ToArray(),
                Bootstrap = _snapshot.Bootstrap is null ? null : _snapshot.Bootstrap with { },
            };
        }
    }

    public async Task SetIncludePrereleaseAsync(bool includePrerelease, CancellationToken cancellationToken = default)
    {
        _options.IncludePrerelease = includePrerelease;
        await PersistUpdateBooleanAsync("IncludePrerelease", includePrerelease, cancellationToken).ConfigureAwait(false);

        SetSnapshot(_snapshot with
        {
            Message = includePrerelease
                ? "Prerelease versions are enabled for selection."
                : "Stable update stream enabled.",
            Status = QuasarUpdateStatus.Idle,
        });
    }

    public async Task SetAutoStageWebUpdatesAsync(bool autoStageWebUpdates, CancellationToken cancellationToken = default)
    {
        _options.AutoStageWebUpdates = autoStageWebUpdates;
        await PersistUpdateBooleanAsync("AutoStageWebUpdates", autoStageWebUpdates, cancellationToken).ConfigureAwait(false);

        SetSnapshot(_snapshot with
        {
            Message = autoStageWebUpdates
                ? "Automatic UI update staging enabled."
                : "Manual UI update staging enabled.",
            Status = QuasarUpdateStatus.Idle,
        });
    }

    public Task SelectWebReleaseAsync(string version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedVersion = QuasarReleaseVersion.Normalize(version);
        var selected = _snapshot.WebReleases.FirstOrDefault(candidate =>
            string.Equals(candidate.Version, normalizedVersion, StringComparison.OrdinalIgnoreCase));

        SetSnapshot(_snapshot with
        {
            Web = selected,
            SelectedWebVersion = selected?.Version ?? string.Empty,
            Message = selected is null
                ? "No Quasar UI release selected."
                : BuildSelectedWebReleaseMessage(selected),
            Status = selected is null
                ? QuasarUpdateStatus.Idle
                : selected.IsStaged ? QuasarUpdateStatus.Staged : QuasarUpdateStatus.UpdateQueued,
        });

        return Task.CompletedTask;
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            SetSnapshot(_snapshot with
            {
                Status = QuasarUpdateStatus.Idle,
                Message = "Update checks disabled.",
            });
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            SetSnapshot(_snapshot with
            {
                Status = QuasarUpdateStatus.Idle,
                Message = "Automatic updates are not supported on this platform.",
            });
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetSnapshot(_snapshot with
            {
                Status = QuasarUpdateStatus.Checking,
                Message = "Checking GitHub releases.",
            });

            var releases = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            var webReleases = BuildCandidates(releases, _options.WebAssetName, _webOptions.Version, requiresPrivilegedInstall: false);
            var bootstrap = BuildCandidates(
                    releases,
                    _options.BootstrapAssetName,
                    string.IsNullOrWhiteSpace(_webOptions.BootstrapVersion) ? _webOptions.Version : _webOptions.BootstrapVersion,
                    requiresPrivilegedInstall: true)
                .FirstOrDefault(candidate => candidate.IsNewer);

            if (webReleases.Count == 0 && bootstrap is null)
            {
                SetSnapshot(_snapshot with
                {
                    Status = QuasarUpdateStatus.Idle,
                    Message = "No GitHub release found.",
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                    Web = null,
                    WebReleases = [],
                    SelectedWebVersion = string.Empty,
                    Bootstrap = null,
                });
                return;
            }

            var selectedWeb = SelectWebCandidate(webReleases, _snapshot.SelectedWebVersion);
            if (_options.AutoStageWebUpdates && selectedWeb?.IsNewer != true)
                selectedWeb = webReleases.FirstOrDefault(candidate => candidate.IsNewer) ?? selectedWeb;

            SetSnapshot(_snapshot with
            {
                Status = webReleases.Any(candidate => candidate.IsNewer) || bootstrap is not null
                    ? QuasarUpdateStatus.UpdateQueued
                    : QuasarUpdateStatus.Idle,
                Message = webReleases.All(candidate => !candidate.IsNewer) && bootstrap is null
                    ? "No newer release found."
                    : BuildReleaseFoundMessage(webReleases.FirstOrDefault(candidate => candidate.IsNewer), bootstrap),
                LastCheckedUtc = DateTimeOffset.UtcNow,
                Web = selectedWeb,
                WebReleases = webReleases,
                SelectedWebVersion = selectedWeb?.Version ?? string.Empty,
                Bootstrap = bootstrap,
            });

            if (_options.AutoStageWebUpdates && selectedWeb is not null && selectedWeb.IsNewer)
                await StageWebUpdateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Quasar update check failed.");
            SetSnapshot(_snapshot with
            {
                Status = QuasarUpdateStatus.Failed,
                Message = exception.Message,
                LastCheckedUtc = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StageWebUpdateAsync(CancellationToken cancellationToken = default) =>
        StageWebUpdateAsync(forceAppSettingsOverride: false, cancellationToken);

    public async Task StageWebUpdateAsync(bool forceAppSettingsOverride, CancellationToken cancellationToken = default)
    {
        var candidate = GetSnapshot().Web;
        if (candidate is null)
            return;

        if (candidate.IsCurrent)
            throw new InvalidOperationException($"Quasar UI {candidate.Version} is already active.");

        if (candidate.IsStaged && !string.IsNullOrWhiteSpace(candidate.StagedDirectory))
            return;

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Staging,
            Message = $"Downloading Quasar UI {candidate.Version}.",
        });

        var stageDirectory = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), candidate.Version);
        var workerPath = Path.Combine(stageDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName);
        var expectedSha256 = candidate.ExpectedSha256;
        if (!File.Exists(workerPath))
        {
            if (Directory.Exists(stageDirectory))
                Directory.Delete(stageDirectory, recursive: true);

            Directory.CreateDirectory(stageDirectory);
            var cacheDirectory = MagnetarPaths.GetQuasarManagedRuntimeCacheDirectory();
            Directory.CreateDirectory(cacheDirectory);
            var archivePath = Path.Combine(cacheDirectory, candidate.AssetName);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");
            using (var response = await client.GetAsync(candidate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var archive = File.Create(archivePath);
                await response.Content.CopyToAsync(archive, cancellationToken).ConfigureAwait(false);
            }

            expectedSha256 = await ResolveExpectedSha256Async(candidate, cancellationToken).ConfigureAwait(false);
            await VerifySha256Async(archivePath, expectedSha256, cancellationToken).ConfigureAwait(false);
            ExtractArchive(archivePath, stageDirectory);
            TryDeleteFile(archivePath);
        }

        QuasarWebReleaseLayout.ValidateDirectory(stageDirectory);
        var appSettingsResolution = await ResolveStagedAppSettingsAsync(
            stageDirectory,
            candidate.Version,
            forceAppSettingsOverride,
            cancellationToken).ConfigureAwait(false);

        if (appSettingsResolution.HasConflict)
        {
            SetSnapshot(_snapshot with
            {
                Status = QuasarUpdateStatus.Failed,
                Message = appSettingsResolution.Message,
                AppSettingsConflict = true,
                AppSettingsConflictPath = appSettingsResolution.Path,
                AppSettingsConflictMessage = appSettingsResolution.Message,
                Web = candidate with
                {
                    IsStaged = false,
                    StagedDirectory = stageDirectory,
                    ExpectedSha256 = expectedSha256,
                },
            });
            return;
        }

        EnsureExecutableBit(workerPath);
        var staged = candidate with
        {
            ExpectedSha256 = expectedSha256,
            IsStaged = true,
            StagedDirectory = stageDirectory,
        };

        var webReleases = _snapshot.WebReleases
            .Select(release => string.Equals(release.Version, staged.Version, StringComparison.OrdinalIgnoreCase)
                ? staged
                : release)
            .ToArray();

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Staged,
            Message = appSettingsResolution.Message,
            Web = staged,
            WebReleases = webReleases,
            SelectedWebVersion = staged.Version,
            AppSettingsConflict = false,
            AppSettingsConflictPath = string.Empty,
            AppSettingsConflictMessage = string.Empty,
        });
    }

    public async Task<string> ReadAppSettingsConflictTextAsync(CancellationToken cancellationToken = default)
    {
        var path = GetSnapshot().AppSettingsConflictPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveAppSettingsConflictAsync(string resolvedText, CancellationToken cancellationToken = default)
    {
        var snapshot = GetSnapshot();
        var candidate = snapshot.Web;
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.StagedDirectory))
            throw new InvalidOperationException("No staged appsettings.json conflict is available to resolve.");

        var appSettingsPath = snapshot.AppSettingsConflictPath;
        if (string.IsNullOrWhiteSpace(appSettingsPath))
            appSettingsPath = Path.Combine(candidate.StagedDirectory, AppSettingsFileName);

        if (ContainsConflictMarkers(resolvedText))
            throw new InvalidOperationException("Resolve all conflict markers before saving appsettings.json.");

        var resolved = ParseJsonObject(resolvedText, "resolved appsettings.json");
        await AtomicFileWriter.WriteTextAsync(appSettingsPath, FormatJson(resolved), cancellationToken).ConfigureAwait(false);

        var releaseBasePath = Path.Combine(candidate.StagedDirectory, AppSettingsReleaseBaseFileName);
        if (File.Exists(releaseBasePath))
        {
            var releaseBase = await ReadJsonObjectAsync(releaseBasePath, "release appsettings.json", cancellationToken).ConfigureAwait(false);
            await PersistAppSettingsBaseAsync(releaseBase, cancellationToken).ConfigureAwait(false);
            TryDeleteFile(releaseBasePath);
        }

        QuasarWebReleaseLayout.ValidateDirectory(candidate.StagedDirectory);
        EnsureExecutableBit(Path.Combine(candidate.StagedDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName));

        var staged = candidate with
        {
            IsStaged = true,
            StagedDirectory = candidate.StagedDirectory,
        };

        var webReleases = _snapshot.WebReleases
            .Select(release => string.Equals(release.Version, staged.Version, StringComparison.OrdinalIgnoreCase)
                ? staged
                : release)
            .ToArray();

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Staged,
            Message = $"Quasar UI {candidate.Version} is staged and ready to activate.",
            Web = staged,
            WebReleases = webReleases,
            SelectedWebVersion = staged.Version,
            AppSettingsConflict = false,
            AppSettingsConflictPath = string.Empty,
            AppSettingsConflictMessage = string.Empty,
        });
    }

    public Task ActivateStagedWebUpdateAsync(CancellationToken cancellationToken = default)
    {
        var candidate = GetSnapshot().Web;
        if (candidate is null || !candidate.IsStaged || string.IsNullOrWhiteSpace(candidate.StagedDirectory))
            throw new InvalidOperationException("No staged Quasar UI update is ready to activate.");

        if (candidate.IsCurrent)
            throw new InvalidOperationException($"Quasar UI {candidate.Version} is already active.");

        var workerPath = Path.Combine(candidate.StagedDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName);
        if (!File.Exists(workerPath))
            throw new InvalidOperationException($"Staged Quasar UI executable not found: {workerPath}");

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Activating,
            Message = $"Activating Quasar UI {candidate.Version}.",
            Web = null,
        });

        var activeDirectory = MagnetarPaths.GetQuasarManagedWebReleaseDirectory(candidate.Version);
        var activeWorkerPath = Path.Combine(activeDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName);
        PrepareActiveWebRelease(candidate.StagedDirectory, activeDirectory);
        TrySyncActiveAppSettingsToInstallDirectory(activeDirectory);

        var pointer = new QuasarActiveReleasePointer
        {
            Version = candidate.Version,
            FileName = activeWorkerPath,
            Arguments = string.Empty,
            WorkingDirectory = activeDirectory,
            ActivatedAtUtc = DateTimeOffset.UtcNow,
        };

        var path = MagnetarPaths.GetQuasarActiveReleasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(pointer, JsonOptions));
        CleanupStagedUpdates(activeDirectory);
        return Task.CompletedTask;
    }

    public async Task RequestBootstrapUpdateActivationAsync(CancellationToken cancellationToken = default)
    {
        var bootstrap = GetSnapshot().Bootstrap;
        if (bootstrap is null || !bootstrap.IsNewer)
            throw new InvalidOperationException("No Bootstrap update is available to activate.");

        if (string.IsNullOrWhiteSpace(_webOptions.LauncherToken))
            throw new InvalidOperationException("Quasar is not running under Bootstrap, so the launcher cannot be updated from the UI.");

        var path = MagnetarPaths.GetQuasarBootstrapUpdateRequestPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var request = new JsonObject
        {
            ["requestedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["version"] = bootstrap.Version,
            ["assetName"] = bootstrap.AssetName,
            ["workerVersion"] = _webOptions.Version,
        };

        await AtomicFileWriter.WriteTextAsync(path, request.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Activating,
            Message = $"Bootstrap {bootstrap.Version} activation requested. Quasar will restart shortly.",
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckNowAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(_options.CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private static string BuildReleaseFoundMessage(QuasarUpdateCandidate? web, QuasarUpdateCandidate? bootstrap)
    {
        if (web is not null && bootstrap is not null)
            return string.Equals(web.Version, bootstrap.Version, StringComparison.OrdinalIgnoreCase)
                ? $"Release {web.Version} found."
                : $"UI {web.Version} and Bootstrap {bootstrap.Version} found.";

        if (web is not null)
            return $"UI {web.Version} found.";

        return bootstrap is null ? "No newer release found." : $"Bootstrap {bootstrap.Version} found.";
    }

    private static string BuildSelectedWebReleaseMessage(QuasarUpdateCandidate selected)
    {
        if (selected.IsCurrent)
            return $"Quasar UI {selected.Version} is already active.";

        if (selected.IsStaged)
            return $"Quasar UI {selected.Version} is staged and ready to activate.";

        return selected.IsNewer
            ? $"Quasar UI {selected.Version} is ready to download."
            : $"Quasar UI {selected.Version} selected for rollback.";
    }

    private async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");

        var url = $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/releases?per_page=100";
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return releases?
            .Where(release => !release.Draft)
            .Where(release => _options.IncludePrerelease || !release.Prerelease)
            .ToArray() ?? [];
    }

    private static IReadOnlyList<QuasarUpdateCandidate> BuildCandidates(
        IReadOnlyList<GitHubRelease> releases,
        string assetName,
        string currentVersion,
        bool requiresPrivilegedInstall)
    {
        return releases
            .Select(release =>
            {
                var asset = FindAsset(release, assetName);
                return asset is null
                    ? null
                    : BuildCandidate(release, asset, currentVersion, requiresPrivilegedInstall);
            })
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static QuasarUpdateCandidate? SelectWebCandidate(
        IReadOnlyList<QuasarUpdateCandidate> webReleases,
        string selectedVersion)
    {
        if (!string.IsNullOrWhiteSpace(selectedVersion))
        {
            var selected = webReleases.FirstOrDefault(candidate =>
                string.Equals(candidate.Version, selectedVersion, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                return selected;
        }

        return webReleases.FirstOrDefault(candidate => candidate.IsNewer) ??
               webReleases.FirstOrDefault(candidate => !candidate.IsCurrent) ??
               webReleases.FirstOrDefault();
    }

    private static async Task PersistUpdateBooleanAsync(string settingName, bool value, CancellationToken cancellationToken)
    {
        var path = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "appsettings.json");
        JsonObject root;

        if (File.Exists(path))
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            root = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var quasar = GetOrCreateObject(root, "Quasar");
        var updates = GetOrCreateObject(quasar, "Updates");
        updates[settingName] = value;

        await AtomicFileWriter.WriteTextAsync(path, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private async Task<AppSettingsResolution> ResolveStagedAppSettingsAsync(
        string stageDirectory,
        string version,
        bool forceAppSettingsOverride,
        CancellationToken cancellationToken)
    {
        var appSettingsPath = Path.Combine(stageDirectory, AppSettingsFileName);
        if (!File.Exists(appSettingsPath))
            return AppSettingsResolution.Clean($"Quasar UI {version} is staged and ready to activate.", appSettingsPath);

        var releaseBasePath = Path.Combine(stageDirectory, AppSettingsReleaseBaseFileName);
        if (HasUnresolvedAppSettingsConflict(stageDirectory))
        {
            if (!File.Exists(releaseBasePath))
                return AppSettingsResolution.Conflict(
                    "appsettings.json rollover conflict is unresolved and the release base sidecar is missing. Resolve the staged file manually.",
                    appSettingsPath);

            var conflictRelease = await ReadJsonObjectAsync(releaseBasePath, "release appsettings.json", cancellationToken).ConfigureAwait(false);
            if (forceAppSettingsOverride)
            {
                await AtomicFileWriter.WriteTextAsync(appSettingsPath, FormatJson(conflictRelease), cancellationToken).ConfigureAwait(false);
                await PersistAppSettingsBaseAsync(conflictRelease, cancellationToken).ConfigureAwait(false);
                TryDeleteFile(releaseBasePath);
                return AppSettingsResolution.Clean($"Quasar UI {version} is staged with release appsettings.json.", appSettingsPath);
            }

            return AppSettingsResolution.Conflict(
                "appsettings.json rollover conflict is unresolved. Resolve it manually or force the release defaults.",
                appSettingsPath);
        }

        var release = await ReadJsonObjectAsync(appSettingsPath, "release appsettings.json", cancellationToken).ConfigureAwait(false);
        TryDeleteFile(releaseBasePath);

        if (forceAppSettingsOverride)
        {
            await AtomicFileWriter.WriteTextAsync(appSettingsPath, FormatJson(release), cancellationToken).ConfigureAwait(false);
            await PersistAppSettingsBaseAsync(release, cancellationToken).ConfigureAwait(false);
            return AppSettingsResolution.Clean($"Quasar UI {version} is staged with release appsettings.json.", appSettingsPath);
        }

        var currentPath = ResolveCurrentAppSettingsPath(stageDirectory);
        if (currentPath is null)
        {
            await PersistAppSettingsBaseAsync(release, cancellationToken).ConfigureAwait(false);
            return AppSettingsResolution.Clean($"Quasar UI {version} is staged with release appsettings.json.", appSettingsPath);
        }

        JsonObject current;
        try
        {
            current = await ReadJsonObjectAsync(currentPath, "current appsettings.json", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteConflictFileAsync(
                appSettingsPath,
                releaseBasePath,
                currentPath,
                $"Current appsettings.json could not be parsed: {exception.Message}",
                string.Empty,
                release,
                cancellationToken).ConfigureAwait(false);

            return AppSettingsResolution.Conflict(
                $"appsettings.json rollover conflict: current appsettings.json is invalid. Resolve it manually or force the release defaults.",
                appSettingsPath);
        }

        var basePath = MagnetarPaths.GetQuasarAppSettingsBasePath();
        var hasBase = TryReadJsonObject(basePath, out var mergeBase, out var baseError);
        if (!hasBase)
        {
            if (!string.IsNullOrWhiteSpace(baseError))
            {
                _logger.LogWarning(
                    "Ignoring invalid appsettings.json merge base at {Path}: {Error}",
                    basePath,
                    baseError);
            }

            var overlaid = (JsonObject)release.DeepClone();
            OverlayObject(overlaid, current);
            await AtomicFileWriter.WriteTextAsync(appSettingsPath, FormatJson(overlaid), cancellationToken).ConfigureAwait(false);
            await PersistAppSettingsBaseAsync(release, cancellationToken).ConfigureAwait(false);
            return AppSettingsResolution.Clean($"Quasar UI {version} is staged with current appsettings.json values.", appSettingsPath);
        }

        var conflicts = new List<string>();
        var merged = MergeObjects(mergeBase!, current, release, "root", conflicts);
        if (conflicts.Count > 0)
        {
            var message = $"appsettings.json rollover conflict at {string.Join(", ", conflicts.Take(5))}. Resolve it manually or force the release defaults.";
            await WriteConflictFileAsync(
                appSettingsPath,
                releaseBasePath,
                currentPath,
                message,
                FormatJson(current),
                release,
                cancellationToken).ConfigureAwait(false);

            return AppSettingsResolution.Conflict(message, appSettingsPath);
        }

        await AtomicFileWriter.WriteTextAsync(appSettingsPath, FormatJson(merged), cancellationToken).ConfigureAwait(false);
        await PersistAppSettingsBaseAsync(release, cancellationToken).ConfigureAwait(false);
        return AppSettingsResolution.Clean($"Quasar UI {version} is staged with current appsettings.json values.", appSettingsPath);
    }

    private static async Task WriteConflictFileAsync(
        string appSettingsPath,
        string releaseBasePath,
        string currentPath,
        string message,
        string currentText,
        JsonObject release,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentText) && File.Exists(currentPath))
            currentText = await File.ReadAllTextAsync(currentPath, cancellationToken).ConfigureAwait(false);

        var releaseText = FormatJson(release);
        var conflictText =
            "<<<<<<< CURRENT appsettings.json (" + currentPath + ")" + Environment.NewLine +
            currentText.TrimEnd() + Environment.NewLine +
            "=======" + Environment.NewLine +
            releaseText.TrimEnd() + Environment.NewLine +
            ">>>>>>> RELEASE appsettings.json" + Environment.NewLine +
            Environment.NewLine +
            "Conflict: " + message + Environment.NewLine;

        await AtomicFileWriter.WriteTextAsync(appSettingsPath, conflictText, cancellationToken).ConfigureAwait(false);
        await AtomicFileWriter.WriteTextAsync(releaseBasePath, releaseText, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject MergeObjects(JsonObject mergeBase, JsonObject current, JsonObject release, string path, ICollection<string> conflicts)
    {
        var result = new JsonObject();
        var names = mergeBase.Select(property => property.Key)
            .Concat(current.Select(property => property.Key))
            .Concat(release.Select(property => property.Key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var name in names)
        {
            var propertyPath = string.Equals(path, "root", StringComparison.Ordinal)
                ? name
                : $"{path}:{name}";
            var hasBase = mergeBase.TryGetPropertyValue(name, out var baseValue);
            var hasCurrent = current.TryGetPropertyValue(name, out var currentValue);
            var hasRelease = release.TryGetPropertyValue(name, out var releaseValue);

            if (hasCurrent && hasRelease && JsonNode.DeepEquals(currentValue, releaseValue))
            {
                result[name] = CloneNode(currentValue);
                continue;
            }

            if (hasBase && hasCurrent && JsonNode.DeepEquals(baseValue, currentValue))
            {
                if (hasRelease)
                    result[name] = CloneNode(releaseValue);
                continue;
            }

            if (hasBase && hasRelease && JsonNode.DeepEquals(baseValue, releaseValue))
            {
                if (hasCurrent)
                    result[name] = CloneNode(currentValue);
                continue;
            }

            if (!hasBase)
            {
                if (!hasCurrent && hasRelease)
                {
                    result[name] = CloneNode(releaseValue);
                    continue;
                }

                if (hasCurrent && !hasRelease)
                {
                    result[name] = CloneNode(currentValue);
                    continue;
                }
            }

            if (baseValue is JsonObject baseObject &&
                currentValue is JsonObject currentObject &&
                releaseValue is JsonObject releaseObject)
            {
                result[name] = MergeObjects(baseObject, currentObject, releaseObject, propertyPath, conflicts);
                continue;
            }

            conflicts.Add(propertyPath);
            if (hasRelease)
                result[name] = CloneNode(releaseValue);
            else if (hasCurrent)
                result[name] = CloneNode(currentValue);
        }

        return result;
    }

    private static void OverlayObject(JsonObject target, JsonObject source)
    {
        foreach (var property in source.ToArray())
        {
            if (property.Value is JsonObject sourceObject &&
                target[property.Key] is JsonObject targetObject)
            {
                OverlayObject(targetObject, sourceObject);
                continue;
            }

            target[property.Key] = CloneNode(property.Value);
        }
    }

    private static JsonNode? CloneNode(JsonNode? node) => node?.DeepClone();

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, string label, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseJsonObject(text, label);
    }

    private static bool TryReadJsonObject(string path, out JsonObject? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!File.Exists(path))
            return false;

        try
        {
            value = ParseJsonObject(File.ReadAllText(path), path);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static JsonObject ParseJsonObject(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(text)?.AsObject()
                   ?? throw new InvalidOperationException($"{label} must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{label} is invalid JSON: {exception.Message}", exception);
        }
    }

    private static string FormatJson(JsonObject value) => value.ToJsonString(JsonOptions);

    private static bool ContainsConflictMarkers(string text) =>
        text.Contains("<<<<<<<", StringComparison.Ordinal) ||
        text.Contains("=======", StringComparison.Ordinal) ||
        text.Contains(">>>>>>>", StringComparison.Ordinal);

    private static bool HasUnresolvedAppSettingsConflict(string stageDirectory)
    {
        var appSettingsPath = Path.Combine(stageDirectory, AppSettingsFileName);
        if (!File.Exists(appSettingsPath))
            return false;

        try
        {
            return ContainsConflictMarkers(File.ReadAllText(appSettingsPath));
        }
        catch
        {
            return true;
        }
    }

    private static string? ResolveCurrentAppSettingsPath(string stageDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("QUASAR_INSTALL_DIR") ?? string.Empty, AppSettingsFileName),
            Path.Combine(AppContext.BaseDirectory, AppSettingsFileName),
            Path.Combine(Directory.GetCurrentDirectory(), AppSettingsFileName),
            Path.Combine(AppContext.BaseDirectory, "WebService", AppSettingsFileName),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (!File.Exists(candidate) || IsSamePath(candidate, Path.Combine(stageDirectory, AppSettingsFileName)))
                continue;

            return candidate;
        }

        return null;
    }

    private static async Task PersistAppSettingsBaseAsync(JsonObject release, CancellationToken cancellationToken)
    {
        var path = MagnetarPaths.GetQuasarAppSettingsBasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFileWriter.WriteTextAsync(path, FormatJson(release), cancellationToken).ConfigureAwait(false);
    }

    private static void TrySyncActiveAppSettingsToInstallDirectory(string activeDirectory)
    {
        var installDirectory = Environment.GetEnvironmentVariable("QUASAR_INSTALL_DIR");
        if (string.IsNullOrWhiteSpace(installDirectory))
            return;

        var sourcePath = Path.Combine(activeDirectory, AppSettingsFileName);
        var destinationPath = Path.Combine(installDirectory, AppSettingsFileName);
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

    private static GitHubAsset? FindAsset(GitHubRelease release, string assetName) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));

    private async Task<string> ResolveExpectedSha256Async(QuasarUpdateCandidate candidate, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ExpectedSha256))
            return candidate.ExpectedSha256;

        if (string.IsNullOrWhiteSpace(candidate.ChecksumDownloadUrl))
            return string.Empty;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");
        var text = await client.GetStringAsync(candidate.ChecksumDownloadUrl, cancellationToken).ConfigureAwait(false);
        var checksums = ParseSha256Sums(text);
        return GetChecksum(checksums, candidate.AssetName);
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

    private static QuasarUpdateCandidate BuildCandidate(
        GitHubRelease release,
        GitHubAsset asset,
        string currentVersion,
        bool requiresPrivilegedInstall)
    {
        var version = QuasarReleaseVersion.Normalize(release.TagName);
        var normalizedCurrent = QuasarReleaseVersion.Normalize(currentVersion);
        var stageDirectory = requiresPrivilegedInstall
            ? null
            : Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), version);
        var isStaged = stageDirectory is not null &&
                       File.Exists(Path.Combine(stageDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName)) &&
                       !HasUnresolvedAppSettingsConflict(stageDirectory);
        var checksumAsset = FindAsset(release, "SHA256SUMS");

        return new QuasarUpdateCandidate
        {
            Version = version,
            AssetName = asset.Name,
            DownloadUrl = asset.BrowserDownloadUrl,
            SizeBytes = asset.Size,
            PublishedAtUtc = release.PublishedAt,
            ChecksumDownloadUrl = checksumAsset?.BrowserDownloadUrl ?? string.Empty,
            StagedDirectory = isStaged ? stageDirectory : null,
            IsAvailable = true,
            IsStaged = isStaged,
            RequiresPrivilegedInstall = requiresPrivilegedInstall,
            IsPrerelease = release.Prerelease,
            IsCurrent = string.Equals(version, normalizedCurrent, StringComparison.OrdinalIgnoreCase),
            IsNewer = IsNewerVersion(version, normalizedCurrent),
        };
    }

    private static bool IsNewerVersion(string candidate, string current) =>
        QuasarReleaseVersion.IsNewer(candidate, current);

    private static void PrepareActiveWebRelease(string stagedDirectory, string activeDirectory)
    {
        if (Directory.Exists(activeDirectory))
            Directory.Delete(activeDirectory, recursive: true);

        CopyDirectory(stagedDirectory, activeDirectory);
        TryDeleteFile(Path.Combine(activeDirectory, AppSettingsReleaseBaseFileName));
        QuasarWebReleaseLayout.ValidateDirectory(activeDirectory);
        EnsureExecutableBit(Path.Combine(activeDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName));
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

    private static bool IsSamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
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

        throw new InvalidOperationException($"Unsupported Quasar UI archive format: {archivePath}");
    }

    private static void EnsureExecutableBit(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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

    private void SetSnapshot(QuasarUpdateSnapshot snapshot)
    {
        lock (_sync)
        {
            _snapshot = snapshot with
            {
                Enabled = _options.Enabled,
                SupportedPlatform = OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
                CurrentVersion = _webOptions.Version,
                CurrentBootstrapVersion = _webOptions.BootstrapVersion,
                IncludePrerelease = _options.IncludePrerelease,
                AutoStageWebUpdates = _options.AutoStageWebUpdates,
                LastChangedUtc = DateTimeOffset.UtcNow,
            };
        }

        Changed?.Invoke();
    }

    private sealed record AppSettingsResolution(bool HasConflict, string Message, string Path)
    {
        public static AppSettingsResolution Clean(string message, string path) =>
            new(false, message, path);

        public static AppSettingsResolution Conflict(string message, string path) =>
            new(true, message, path);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        public bool Draft { get; set; }

        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        public IReadOnlyList<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = string.Empty;

        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
