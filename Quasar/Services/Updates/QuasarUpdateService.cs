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
                Bootstrap = _snapshot.Bootstrap is null ? null : _snapshot.Bootstrap with { },
            };
        }
    }

    public async Task SetIncludePrereleaseAsync(bool includePrerelease, CancellationToken cancellationToken = default)
    {
        _options.IncludePrerelease = includePrerelease;
        await PersistIncludePrereleaseAsync(includePrerelease, cancellationToken).ConfigureAwait(false);

        SetSnapshot(_snapshot with
        {
            Message = includePrerelease
                ? "Prerelease update stream enabled. This is intended only for testing and may install unstable builds."
                : "Stable update stream enabled.",
            Status = QuasarUpdateStatus.Idle,
        });
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

            var webRelease = await GetLatestReleaseWithAssetAsync(_options.WebAssetName, cancellationToken).ConfigureAwait(false);
            var bootstrapRelease = await GetLatestReleaseWithAssetAsync(_options.BootstrapAssetName, cancellationToken).ConfigureAwait(false);
            if (webRelease is null && bootstrapRelease is null)
            {
                SetSnapshot(_snapshot with
                {
                    Status = QuasarUpdateStatus.Idle,
                    Message = "No GitHub release found.",
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                });
                return;
            }

            var web = webRelease is null
                ? null
                : await BuildCandidateAsync(webRelease, _options.WebAssetName, _webOptions.Version, requiresPrivilegedInstall: false, cancellationToken)
                    .ConfigureAwait(false);
            var bootstrap = bootstrapRelease is null
                ? null
                : await BuildCandidateAsync(
                    bootstrapRelease,
                    _options.BootstrapAssetName,
                    string.IsNullOrWhiteSpace(_webOptions.BootstrapVersion) ? _webOptions.Version : _webOptions.BootstrapVersion,
                    requiresPrivilegedInstall: true,
                    cancellationToken)
                    .ConfigureAwait(false);

            web = DiscardCurrentOrOlderCandidate(web, _webOptions.Version);
            bootstrap = DiscardCurrentOrOlderCandidate(
                bootstrap,
                string.IsNullOrWhiteSpace(_webOptions.BootstrapVersion) ? _webOptions.Version : _webOptions.BootstrapVersion);

            SetSnapshot(_snapshot with
            {
                Status = web is null && bootstrap is null ? QuasarUpdateStatus.Idle : QuasarUpdateStatus.UpdateQueued,
                Message = web is null && bootstrap is null
                    ? "No newer release found."
                    : BuildReleaseFoundMessage(web, bootstrap),
                LastCheckedUtc = DateTimeOffset.UtcNow,
                Web = web,
                Bootstrap = bootstrap,
            });

            if (web is not null)
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

    public async Task StageWebUpdateAsync(CancellationToken cancellationToken = default)
    {
        var candidate = GetSnapshot().Web;
        if (candidate is null)
            return;

        if (!IsNewerVersion(candidate.Version, _webOptions.Version))
        {
            var bootstrap = _snapshot.Bootstrap;
            SetSnapshot(_snapshot with
            {
                Status = bootstrap is null ? QuasarUpdateStatus.Idle : QuasarUpdateStatus.UpdateQueued,
                Message = bootstrap is null
                    ? "No newer Quasar UI release found."
                    : BuildReleaseFoundMessage(web: null, bootstrap),
                Web = null,
            });
            return;
        }

        if (candidate.IsStaged && !string.IsNullOrWhiteSpace(candidate.StagedDirectory))
            return;

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Staging,
            Message = $"Downloading Quasar UI {candidate.Version}.",
        });

        var stageDirectory = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), candidate.Version);
        var workerPath = Path.Combine(stageDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName);
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

            await VerifySha256Async(archivePath, candidate.ExpectedSha256, cancellationToken).ConfigureAwait(false);
            ExtractArchive(archivePath, stageDirectory);
            TryDeleteFile(archivePath);
        }

        QuasarWebReleaseLayout.ValidateDirectory(stageDirectory);

        EnsureExecutableBit(workerPath);
        var staged = candidate with
        {
            IsStaged = true,
            StagedDirectory = stageDirectory,
        };

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Staged,
            Message = $"Quasar UI {candidate.Version} is staged and ready to activate.",
            Web = staged,
        });
    }

    public Task ActivateStagedWebUpdateAsync(CancellationToken cancellationToken = default)
    {
        var candidate = GetSnapshot().Web;
        if (candidate is null || !candidate.IsStaged || string.IsNullOrWhiteSpace(candidate.StagedDirectory))
            throw new InvalidOperationException("No staged Quasar UI update is ready to activate.");

        if (!IsNewerVersion(candidate.Version, _webOptions.Version))
            throw new InvalidOperationException($"Staged Quasar UI {candidate.Version} is not newer than the current version {_webOptions.Version}.");

        var workerPath = Path.Combine(candidate.StagedDirectory, QuasarWebReleaseLayout.WorkerExecutableName);
        if (!File.Exists(workerPath))
            throw new InvalidOperationException($"Staged Quasar UI executable not found: {workerPath}");

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Activating,
            Message = $"Activating Quasar UI {candidate.Version}.",
        });

        var pointer = new QuasarActiveReleasePointer
        {
            Version = candidate.Version,
            FileName = workerPath,
            Arguments = string.Empty,
            WorkingDirectory = candidate.StagedDirectory,
            ActivatedAtUtc = DateTimeOffset.UtcNow,
        };

        var path = MagnetarPaths.GetQuasarActiveReleasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(pointer, JsonOptions));
        return Task.CompletedTask;
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

    private async Task<QuasarUpdateCandidate?> BuildCandidateAsync(
        GitHubRelease release,
        string assetName,
        string currentVersion,
        bool requiresPrivilegedInstall,
        CancellationToken cancellationToken)
    {
        var version = QuasarReleaseVersion.Normalize(release.TagName);
        if (!IsNewerVersion(version, currentVersion))
            return null;

        var asset = FindAsset(release, assetName);
        if (asset is null)
            return null;

        var checksums = await GetChecksumsAsync(release, cancellationToken).ConfigureAwait(false);
        return BuildCandidate(version, release.PublishedAt, asset, GetChecksum(checksums, asset.Name), requiresPrivilegedInstall);
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

    private async Task<GitHubRelease?> GetLatestReleaseWithAssetAsync(string assetName, CancellationToken cancellationToken)
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
            .FirstOrDefault(release => FindAsset(release, assetName) is not null);
    }

    private static async Task PersistIncludePrereleaseAsync(bool includePrerelease, CancellationToken cancellationToken)
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
        updates["IncludePrerelease"] = includePrerelease;

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

    private static GitHubAsset? FindAsset(GitHubRelease release, string assetName) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyDictionary<string, string>> GetChecksumsAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        var asset = FindAsset(release, "SHA256SUMS");
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");
        var text = await client.GetStringAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
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

    private static QuasarUpdateCandidate BuildCandidate(string version, DateTimeOffset? publishedAt, GitHubAsset asset, string expectedSha256, bool requiresPrivilegedInstall)
    {
        var stageDirectory = requiresPrivilegedInstall
            ? null
            : Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), version);
        var isStaged = stageDirectory is not null && File.Exists(Path.Combine(stageDirectory, QuasarWebReleaseLayout.WorkerExecutableFileName));
        return new QuasarUpdateCandidate
        {
            Version = version,
            AssetName = asset.Name,
            DownloadUrl = asset.BrowserDownloadUrl,
            ExpectedSha256 = expectedSha256,
            SizeBytes = asset.Size,
            PublishedAtUtc = publishedAt,
            StagedDirectory = isStaged ? stageDirectory : null,
            IsAvailable = true,
            IsStaged = isStaged,
            RequiresPrivilegedInstall = requiresPrivilegedInstall,
        };
    }

    private static bool IsNewerVersion(string candidate, string current) =>
        QuasarReleaseVersion.IsNewer(candidate, current);

    private static QuasarUpdateCandidate? DiscardCurrentOrOlderCandidate(
        QuasarUpdateCandidate? candidate,
        string currentVersion) =>
        candidate is not null && IsNewerVersion(candidate.Version, currentVersion)
            ? candidate
            : null;

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
                LastChangedUtc = DateTimeOffset.UtcNow,
            };
        }

        Changed?.Invoke();
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
