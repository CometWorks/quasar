using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarPluginCatalogService
{
    private const int CacheSchemaVersion = 5;
    public const string DotNetCompatPluginId = "se-dotnet-compat";
    public const string LinuxCompatPluginId = "se-linux-compat";
    public const string DefaultHubName = "MagnetarHub";
    public const string DefaultHubRepo = "viktor-ferenczi/MagnetarHub";
    public const string DefaultHubBranch = "main";
    public const string DotNetCompatManifestFile = "Plugins/DotNetCompat.xml";
    public const string LinuxCompatManifestFile = "Plugins/LinuxCompat.xml";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarPluginCatalogService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private List<QuasarPluginCatalogEntry> _entries;

    public QuasarPluginCatalogService(
        ILogger<QuasarPluginCatalogService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _entries = LoadCache();
    }

    public DateTimeOffset? LastRefreshUtc { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<QuasarPluginCatalogEntry> GetEntries()
    {
        lock (_sync)
            return _entries.Select(Clone).ToList();
    }

    public static bool IsManualSelectionAllowed(string pluginId) =>
        !string.Equals(pluginId?.Trim(), DotNetCompatPluginId, StringComparison.OrdinalIgnoreCase);

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
        };
    }

    private static string GetCachePath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Caches", "plugin-catalog.json");

    private sealed class QuasarPluginCatalogCache
    {
        public int SchemaVersion { get; set; } = CacheSchemaVersion;

        public DateTimeOffset? LastRefreshUtc { get; set; }

        public List<QuasarPluginCatalogEntry> Entries { get; set; } = [];
    }
}
