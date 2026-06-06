using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;

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
            SupportedPlatform = OperatingSystem.IsLinux(),
            CurrentVersion = webOptions.Version,
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

        if (!OperatingSystem.IsLinux())
        {
            SetSnapshot(_snapshot with
            {
                Status = QuasarUpdateStatus.Idle,
                Message = "Linux updates are implemented first; this platform is not managed yet.",
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

            var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                SetSnapshot(_snapshot with
                {
                    Status = QuasarUpdateStatus.Idle,
                    Message = "No GitHub release found.",
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                });
                return;
            }

            var version = NormalizeVersion(release.TagName);
            var webAsset = FindAsset(release, _options.LinuxWebAssetName);
            var bootstrapAsset = FindAsset(release, _options.LinuxBootstrapAssetName);
            var checksums = await GetChecksumsAsync(release, cancellationToken).ConfigureAwait(false);
            var hasNewVersion = IsNewerVersion(version, _webOptions.Version);

            var web = webAsset is null || !hasNewVersion
                ? null
                : BuildCandidate(version, release.PublishedAt, webAsset, GetChecksum(checksums, webAsset.Name), requiresPrivilegedInstall: false);
            var bootstrap = bootstrapAsset is null || !hasNewVersion
                ? null
                : BuildCandidate(version, release.PublishedAt, bootstrapAsset, GetChecksum(checksums, bootstrapAsset.Name), requiresPrivilegedInstall: true);

            SetSnapshot(_snapshot with
            {
                Status = web is null && bootstrap is null ? QuasarUpdateStatus.Idle : QuasarUpdateStatus.UpdateQueued,
                Message = web is null && bootstrap is null
                    ? $"No newer Linux release found. Latest checked: {version}."
                    : $"Release {version} found.",
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

        if (candidate.IsStaged && !string.IsNullOrWhiteSpace(candidate.StagedDirectory))
            return;

        SetSnapshot(_snapshot with
        {
            Status = QuasarUpdateStatus.Staging,
            Message = $"Downloading Quasar UI {candidate.Version}.",
        });

        var stageDirectory = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), candidate.Version);
        var workerPath = Path.Combine(stageDirectory, "Quasar");
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

        if (!File.Exists(workerPath))
            throw new InvalidOperationException($"Staged Quasar UI does not contain executable '{workerPath}'.");

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

        var workerPath = Path.Combine(candidate.StagedDirectory, "Quasar");
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

    private async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar");

        var url = _options.IncludePrerelease
            ? $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/releases"
            : $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/releases/latest";
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        if (!_options.IncludePrerelease)
            return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return releases?.FirstOrDefault(release => !release.Draft);
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
        var isStaged = stageDirectory is not null && File.Exists(Path.Combine(stageDirectory, "Quasar"));
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

    private static bool IsNewerVersion(string candidate, string current)
    {
        if (Version.TryParse(NormalizeVersion(candidate), out var candidateVersion) &&
            Version.TryParse(NormalizeVersion(current), out var currentVersion))
        {
            return candidateVersion > currentVersion;
        }

        return !string.Equals(NormalizeVersion(candidate), NormalizeVersion(current), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string value)
    {
        value = value.Trim();
        return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
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

    private void SetSnapshot(QuasarUpdateSnapshot snapshot)
    {
        lock (_sync)
        {
            _snapshot = snapshot with
            {
                Enabled = _options.Enabled,
                SupportedPlatform = OperatingSystem.IsLinux(),
                CurrentVersion = _webOptions.Version,
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
