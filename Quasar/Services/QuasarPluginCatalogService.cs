using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarPluginCatalogService
{
    private const int CacheSchemaVersion = 7;
    // Core compatibility plugins Magnetar force-loads by id on start (see GetCorePlugins in
    // Magnetar's Pulsar/Legacy/Program.cs) from whatever source lists them; they never need
    // to appear in the profile. Magnetar 2.3.3.0 and later finds them in the hub source.
    public const string DotNetCompatPluginId = "dotnet-compat";
    public const string LinuxCompatPluginId = "linux-compat";
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
    private readonly ILogger<QuasarPluginCatalogService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly QuasarDevFolderCatalog _devFolderCatalog;
    private List<QuasarPluginCatalogEntry> _entries;

    public QuasarPluginCatalogService(
        ILogger<QuasarPluginCatalogService> logger,
        IHttpClientFactory httpClientFactory,
        QuasarDevFolderCatalog devFolderCatalog)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _devFolderCatalog = devFolderCatalog;
        _entries = LoadCache();
    }

    public DateTimeOffset? LastRefreshUtc { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<QuasarPluginCatalogEntry> GetEntries()
    {
        var entries = new Dictionary<string, QuasarPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            foreach (var entry in _entries)
            {
                if (string.IsNullOrWhiteSpace(entry.PluginId))
                    continue;

                entries[entry.PluginId] = Clone(entry);
            }
        }

        foreach (var entry in BuildDevFolderEntries())
            entries[entry.PluginId] = entry;

        return entries.Values
            .OrderBy(item => item.Hidden)
            .ThenBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsManualSelectionAllowed(string pluginId)
    {
        var id = pluginId?.Trim();
        return !string.Equals(id, DotNetCompatPluginId, StringComparison.OrdinalIgnoreCase) &&
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
        var entries = new Dictionary<string, QuasarPluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
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

                    var pluginId = root.Element("Id")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(pluginId))
                        continue;

                    entries[pluginId] = new QuasarPluginCatalogEntry
                    {
                        PluginId = pluginId,
                        FriendlyName = GetValue(root, "FriendlyName", pluginId),
                        Author = GetValue(root, "Author"),
                        Description = GetValue(root, "Description"),
                        Tooltip = GetValue(root, "Tooltip"),
                        Runtimes = GetValue(root, "Runtimes"),
                        SourceRepo = GetValue(root, "RepoId", DefaultHubRepo),
                        ManifestRepo = DefaultHubRepo,
                        ManifestBranch = DefaultHubBranch,
                        ManifestFile = GetArchiveEntryRelativePath(entry.FullName),
                        Hidden = GetBoolean(root, "Hidden"),
                    };
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Failed to parse plugin catalog entry {EntryName}", entry.FullName);
                }
            }

            var normalized = entries.Values
                .OrderBy(item => item.Hidden)
                .ThenBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_sync)
                _entries = normalized;

            LastRefreshUtc = DateTimeOffset.UtcNow;
            LastError = string.Empty;
            await SaveCacheAsync(normalized, cancellationToken);
            _logger.LogInformation("Downloaded Quasar plugin catalog with {Count} entries.", normalized.Count);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            _logger.LogWarning(exception, "Failed to refresh Quasar plugin catalog.");
            throw;
        }
    }

    private List<QuasarPluginCatalogEntry> LoadCache()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path))
                return [];

            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<QuasarPluginCatalogCache>(json, JsonOptions);
            if (cache?.SchemaVersion != CacheSchemaVersion)
                return [];

            LastRefreshUtc = cache?.LastRefreshUtc;
            return cache?.Entries?
                       .Select(Clone)
                       .OrderBy(item => item.Hidden)
                       .ThenBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase)
                       .ToList()
                   ?? [];
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to load Quasar plugin catalog cache.");
            return [];
        }
    }

    private async Task SaveCacheAsync(IReadOnlyList<QuasarPluginCatalogEntry> entries, CancellationToken cancellationToken)
    {
        var path = GetCachePath();
        var payload = new QuasarPluginCatalogCache
        {
            SchemaVersion = CacheSchemaVersion,
            LastRefreshUtc = LastRefreshUtc,
            Entries = entries.Select(Clone).ToList(),
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);
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

        public List<QuasarPluginCatalogEntry> Entries { get; set; } = [];
    }
}
