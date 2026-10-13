using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarPluginCatalogService
{
    private const int CacheSchemaVersion = 9;
    // Core compatibility plugins Magnetar force-loads by id on start (see GetCorePlugins in
    // Magnetar's Pulsar/Legacy/Program.cs) from whatever source lists them; they never need
    // to appear in the profile. Magnetar 2.3.3.0 and later finds them in the hub source.
    public const string DotNetCompatPluginId = "dotnet-compat";
    public const string LinuxCompatPluginId = "linux-compat";
    public const string ErrorReportingPluginId = "error-reporting";
    public static readonly string[] CorePluginIds = [DotNetCompatPluginId, LinuxCompatPluginId, ErrorReportingPluginId];
    // LEGACY-MAGNETAR-COMPAT: Magnetar before 2.3.3.0 force-loads the se- prefixed ids,
    // which the hub keeps in the *LegacyId.xml manifests. Remove with the Legacy style in
    // the first 2027 Quasar release.
    public const string LegacyDotNetCompatPluginId = "se-dotnet-compat";
    public const string LegacyLinuxCompatPluginId = "se-linux-compat";
    public const string DefaultHubName = "MagnetarHub";
    public const string DefaultHubRepo = "CometWorks/magnetar-hub";
    public const string DefaultHubBranch = "main";
    // LEGACY-MAGNETAR-COMPAT: remove with the legacy ids above.
    public const string LegacyDotNetCompatManifestFile = "Plugins/DotNetCompatLegacyId.xml";
    public const string LegacyLinuxCompatManifestFile = "Plugins/LinuxCompatLegacyId.xml";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ILogger<QuasarPluginCatalogService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly QuasarDevFolderCatalog _devFolderCatalog;
    private readonly Func<IReadOnlyList<QuasarPluginHub>> _registeredHubs;
    private readonly Func<string?> _gitHubToken;
    private readonly string _cachePath;
    private Dictionary<string, HubState> _hubStates;

    public QuasarPluginCatalogService(
        ILogger<QuasarPluginCatalogService> logger,
        IHttpClientFactory httpClientFactory,
        QuasarDevFolderCatalog devFolderCatalog,
        QuasarPluginHubCatalog? hubCatalog = null,
        GitHubUpdateCredentialsCatalog? gitHubCredentials = null)
        : this(logger, httpClientFactory, devFolderCatalog, GetCachePath(),
            hubCatalog is null ? null : hubCatalog.GetHubs,
            gitHubCredentials is null ? null : () => gitHubCredentials.GetCredentials().Token) { }

    internal QuasarPluginCatalogService(ILogger<QuasarPluginCatalogService> logger,
        IHttpClientFactory httpClientFactory, QuasarDevFolderCatalog devFolderCatalog, string cachePath,
        Func<IReadOnlyList<QuasarPluginHub>>? registeredHubs = null, Func<string?>? gitHubToken = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _devFolderCatalog = devFolderCatalog;
        _registeredHubs = registeredHubs ?? (() => []);
        _gitHubToken = gitHubToken ?? (() => null);
        _cachePath = cachePath;
        _hubStates = LoadCache();
    }

    public DateTimeOffset? LastRefreshUtc { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    /// <summary>The operator's extra hubs, without MagnetarHub.</summary>
    public IReadOnlyList<QuasarPluginHub> GetRegisteredHubs() => _registeredHubs();

    // Update observations must use hub manifests, never a local dev-folder override.
    // MagnetarHub comes first, then the registered hubs in their order. An id that more
    // than one hub lists keeps the first hub's entry and names every hub in ConflictingHubs.
    public IReadOnlyList<QuasarPluginCatalogEntry> GetHubEntries()
    {
        var merged = new Dictionary<string, QuasarPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            foreach (var hub in GetAllHubs())
            {
                if (!_hubStates.TryGetValue(hub.Key, out var state))
                    continue;

                foreach (var entry in state.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.PluginId))
                        continue;

                    if (merged.TryGetValue(entry.PluginId, out var first))
                    {
                        if (first.ConflictingHubs.Count == 0)
                            first.ConflictingHubs.Add(first.HubName);
                        if (!first.ConflictingHubs.Contains(hub.Name, StringComparer.OrdinalIgnoreCase))
                            first.ConflictingHubs.Add(hub.Name);
                        continue;
                    }

                    var clone = Clone(entry);
                    clone.HubName = hub.Name;
                    clone.HubError = state.Error;
                    clone.ConflictingHubs = [];
                    merged[entry.PluginId] = clone;
                }
            }
        }

        return Sort(merged.Values);
    }

    // A dev folder overrides a hub entry with the same id, the way Magnetar prefers it.
    public IReadOnlyList<QuasarPluginCatalogEntry> GetEntries()
    {
        var entries = GetHubEntries().ToDictionary(entry => entry.PluginId, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in BuildDevFolderEntries())
            entries[entry.PluginId] = entry;

        return Sort(entries.Values);
    }

    public IReadOnlyList<QuasarPluginHubStatus> GetHubStatuses()
    {
        lock (_sync)
        {
            return GetAllHubs()
                .Select(hub => _hubStates.TryGetValue(hub.Key, out var state)
                    ? new QuasarPluginHubStatus(hub, IsDefaultHub(hub), state.Entries.Count, state.LastRefreshUtc, state.Error)
                    : new QuasarPluginHubStatus(hub, IsDefaultHub(hub), 0, null, string.Empty))
                .ToList();
        }
    }

    public static bool IsManualSelectionAllowed(string pluginId)
    {
        var id = pluginId?.Trim();
        return !string.Equals(id, DotNetCompatPluginId, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(id, ErrorReportingPluginId, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(id, LegacyDotNetCompatPluginId, StringComparison.OrdinalIgnoreCase);
    }

    // LEGACY-MAGNETAR-COMPAT: pre-2.3.3.0 Magnetar builds get the se- prefixed core plugins
    // as per-file RemotePlugin sources, the way Quasar always configured them. Remove with
    // the Legacy style in the first 2027 Quasar release.
    public static IReadOnlyList<CorePluginManifest> GetLegacyCorePluginManifests(bool isLinux)
    {
        var manifests = new List<CorePluginManifest>
        {
            new(LegacyDotNetCompatPluginId, LegacyDotNetCompatManifestFile),
        };
        if (isLinux)
            manifests.Add(new(LegacyLinuxCompatPluginId, LegacyLinuxCompatManifestFile));
        return manifests;
    }

    public sealed record CorePluginManifest(string PluginId, string ManifestFile);

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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try { await RefreshCoreAsync(cancellationToken); }
        finally { _refreshGate.Release(); }
    }

    // Every hub refreshes on its own. A hub that fails keeps its cached entries and records
    // the error on them, so one unreachable hub never hides the plugins of the others.
    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var hubs = GetAllHubs();
        Dictionary<string, HubState> previous;
        lock (_sync)
            previous = new(_hubStates, StringComparer.OrdinalIgnoreCase);

        var next = new Dictionary<string, HubState>(StringComparer.OrdinalIgnoreCase);
        foreach (var hub in hubs)
        {
            previous.TryGetValue(hub.Key, out var old);
            var state = hub.IsLocal
                ? RefreshLocalHub(hub, old)
                : await RefreshRemoteHubAsync(hub, old, cancellationToken);
            next[hub.Key] = state;
        }

        lock (_sync)
            _hubStates = next;

        var errors = next.Values.Where(state => state.Error.Length != 0).Select(state => state.Error).ToList();
        LastRefreshUtc = DateTimeOffset.UtcNow;
        LastError = string.Join(" ", errors);
        try
        {
            await SaveCacheAsync(next.Values.ToList(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to save the Quasar plugin catalog cache.");
        }
        _logger.LogInformation("Refreshed the Quasar plugin catalog from {Hubs} hubs with {Count} entries.",
            hubs.Count, next.Values.Sum(state => state.Entries.Count));
    }

    private async Task<HubState> RefreshRemoteHubAsync(QuasarPluginHub hub, HubState? old, CancellationToken cancellationToken)
    {
        // MagnetarHub is public and always downloads anonymously. Another hub may be a private
        // repository, so it goes through the API with the GitHub token when one is set.
        var token = IsDefaultHub(hub) ? null : _gitHubToken()?.Trim();
        var url = string.IsNullOrEmpty(token)
            ? $"https://github.com/{hub.Repo}/archive/refs/heads/{hub.Branch}.zip"
            : $"https://api.github.com/repos/{hub.Repo}/zipball/{hub.Branch}";
        var etag = old is not null && old.Url == url ? old.ETag : null;

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Headers.UserAgent.ParseAdd("Quasar");
            }
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && etag is not null)
                return old! with { LastRefreshUtc = DateTimeOffset.UtcNow, Error = string.Empty };
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new HttpRequestException(string.IsNullOrEmpty(token)
                    ? "Repository or branch not found. A private repository needs a GitHub token with read access in Updates."
                    : "Repository or branch not found, or the GitHub token can't read it.");
            response.EnsureSuccessStatusCode();

            var entries = new Dictionary<string, QuasarPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            await using (var archiveStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var entry in archive.Entries)
                {
                    // MagnetarHub keeps its manifests in Plugins/ and a sample at the top. Magnetar
                    // reads every manifest of any other hub.
                    if (IsDefaultHub(hub) && !entry.FullName.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        continue;

                    await using var entryStream = entry.Open();
                    AddManifest(entries, hub, entryStream, GetArchiveEntryRelativePath(entry.FullName));
                }
            }

            return new HubState
            {
                Key = hub.Key,
                Url = url,
                ETag = response.Headers.ETag?.ToString(),
                LastRefreshUtc = DateTimeOffset.UtcNow,
                Entries = Sort(entries.Values),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Failed to refresh plugin hub {Hub} ({Repo}).", hub.Name, hub.Repo);
            return Failed(hub, old, $"{hub.Name}: {exception.Message}");
        }
    }

    private HubState RefreshLocalHub(QuasarPluginHub hub, HubState? old)
    {
        if (!Directory.Exists(hub.Folder))
            return Failed(hub, old, $"{hub.Name}: folder {hub.Folder} is missing.");

        try
        {
            // Magnetar reads every manifest under a local hub folder, subfolders included.
            var entries = new Dictionary<string, QuasarPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(hub.Folder, "*.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(path);
                AddManifest(entries, hub, stream, Path.GetRelativePath(hub.Folder, path).Replace('\\', '/'));
            }

            return new HubState { Key = hub.Key, LastRefreshUtc = DateTimeOffset.UtcNow, Entries = Sort(entries.Values) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Failed to read local plugin hub {Hub} at {Folder}.", hub.Name, hub.Folder);
            return Failed(hub, old, $"{hub.Name}: {exception.Message}");
        }
    }

    private static HubState Failed(QuasarPluginHub hub, HubState? old, string error) =>
        old is null
            ? new HubState { Key = hub.Key, Error = error }
            : old with { Error = error };

    private void AddManifest(Dictionary<string, QuasarPluginCatalogEntry> entries, QuasarPluginHub hub, Stream stream, string relativePath)
    {
        try
        {
            var root = XDocument.Load(stream, LoadOptions.None).Root;
            if (root is null || root.Name.LocalName != "PluginData")
                return;

            var pluginId = root.Element("Id")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(pluginId))
                return;

            entries[pluginId] = new QuasarPluginCatalogEntry
            {
                PluginId = pluginId,
                SourceCommit = GetValue(root, "Commit").ToLowerInvariant(),
                FriendlyName = GetValue(root, "FriendlyName", pluginId),
                Author = GetValue(root, "Author"),
                Description = GetValue(root, "Description"),
                Tooltip = GetValue(root, "Tooltip"),
                Runtimes = GetValue(root, "Runtimes"),
                SourceRepo = GetValue(root, "RepoId", hub.Repo),
                ManifestRepo = hub.Repo,
                ManifestBranch = hub.Branch,
                ManifestFile = relativePath,
                Hidden = GetBoolean(root, "Hidden"),
                HubName = hub.Name,
            };
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException or InvalidDataException)
        {
            _logger.LogDebug(exception, "Failed to parse plugin manifest {File} of hub {Hub}", relativePath, hub.Name);
        }
    }

    private IReadOnlyList<QuasarPluginHub> GetAllHubs() =>
        [DefaultHub, .. _registeredHubs().Where(hub => !string.Equals(hub.Key, DefaultHubRepo, StringComparison.OrdinalIgnoreCase))];

    private static readonly QuasarPluginHub DefaultHub = new() { Name = DefaultHubName, Repo = DefaultHubRepo, Branch = DefaultHubBranch };

    public static bool IsDefaultHubEntry(QuasarPluginCatalogEntry entry) =>
        !entry.IsLocalDevFolder &&
        (string.IsNullOrEmpty(entry.HubName) || string.Equals(entry.HubName, DefaultHubName, StringComparison.Ordinal));

    public static bool IsDefaultHub(QuasarPluginHub hub) =>
        !hub.IsLocal && string.Equals(hub.Repo, DefaultHubRepo, StringComparison.OrdinalIgnoreCase);

    private static List<QuasarPluginCatalogEntry> Sort(IEnumerable<QuasarPluginCatalogEntry> entries) =>
        entries
            .OrderBy(item => item.Hidden)
            .ThenBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private Dictionary<string, HubState> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var cache = JsonSerializer.Deserialize<QuasarPluginCatalogCache>(File.ReadAllText(_cachePath), JsonOptions);
            if (cache?.SchemaVersion != CacheSchemaVersion)
                return new(StringComparer.OrdinalIgnoreCase);

            LastRefreshUtc = cache.LastRefreshUtc;
            return cache.Hubs
                .Where(state => !string.IsNullOrWhiteSpace(state.Key))
                .DistinctBy(state => state.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(state => state.Key, state => state with { Entries = Sort(state.Entries.Select(Clone)) },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to load Quasar plugin catalog cache.");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveCacheAsync(IReadOnlyList<HubState> hubs, CancellationToken cancellationToken)
    {
        var payload = new QuasarPluginCatalogCache
        {
            SchemaVersion = CacheSchemaVersion,
            LastRefreshUtc = LastRefreshUtc,
            Hubs = hubs.ToList(),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(_cachePath, json, cancellationToken);
    }

    private static string GetValue(XElement root, string name, string fallback = "") =>
        root.Element(name)?.Value?.Trim() ?? fallback;

    private static bool GetBoolean(XElement root, string name)
    {
        return bool.TryParse(root.Element(name)?.Value?.Trim(), out var value) && value;
    }

    private static string GetArchiveEntryRelativePath(string fullName)
    {
        var normalized = (fullName ?? string.Empty).Replace('\\', '/').Trim('/');
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static QuasarPluginCatalogEntry Clone(QuasarPluginCatalogEntry entry)
    {
        return new QuasarPluginCatalogEntry
        {
            PluginId = entry.PluginId,
            SourceCommit = entry.SourceCommit,
            FriendlyName = entry.FriendlyName,
            Author = entry.Author,
            Description = entry.Description,
            Tooltip = entry.Tooltip,
            Runtimes = entry.Runtimes,
            SourceRepo = entry.SourceRepo,
            ManifestRepo = entry.ManifestRepo,
            ManifestBranch = entry.ManifestBranch,
            ManifestFile = entry.ManifestFile,
            Hidden = entry.Hidden,
            IsLocalDevFolder = entry.IsLocalDevFolder,
            HubName = entry.HubName,
            ConflictingHubs = [.. entry.ConflictingHubs],
            HubError = entry.HubError,
        };
    }

    private IEnumerable<QuasarPluginCatalogEntry> BuildDevFolderEntries()
    {
        foreach (var devFolder in _devFolderCatalog.GetDevFolders())
        {
            var pluginId = GetDevFolderPluginId(devFolder);
            if (string.IsNullOrWhiteSpace(pluginId))
                continue;

            var metadata = PluginManifestReader.ReadMetadata(Path.Combine(devFolder.FolderPath, devFolder.DataFile));
            yield return new QuasarPluginCatalogEntry
            {
                PluginId = pluginId,
                FriendlyName = FirstNonEmpty(metadata.FriendlyName, devFolder.Name, pluginId),
                Author = metadata.Author,
                Description = FirstNonEmpty(metadata.Description, $"Local dev folder: {devFolder.FolderPath}"),
                Tooltip = metadata.Tooltip,
                Runtimes = metadata.Runtimes,
                IsLocalDevFolder = true,
            };
        }
    }

    public static string GetDevFolderPluginId(QuasarDevFolderSelection devFolder)
    {
        var pluginId = devFolder.PluginId?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(pluginId)
            ? devFolder.SourceFolderName
            : pluginId;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string GetCachePath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Caches", "plugin-catalog.json");

    private sealed class QuasarPluginCatalogCache
    {
        public int SchemaVersion { get; set; } = CacheSchemaVersion;

        public DateTimeOffset? LastRefreshUtc { get; set; }

        public List<HubState> Hubs { get; set; } = [];
    }

    private sealed record HubState
    {
        public string Key { get; init; } = string.Empty;   // QuasarPluginHub.Key

        public string Url { get; init; } = string.Empty;   // the ETag belongs to this download URL

        public string? ETag { get; init; }

        public DateTimeOffset? LastRefreshUtc { get; init; }

        public string Error { get; init; } = string.Empty;

        public List<QuasarPluginCatalogEntry> Entries { get; init; } = [];
    }
}

public sealed record QuasarPluginHubStatus(
    QuasarPluginHub Hub,
    bool IsDefault,
    int PluginCount,
    DateTimeOffset? LastRefreshUtc,
    string Error);
