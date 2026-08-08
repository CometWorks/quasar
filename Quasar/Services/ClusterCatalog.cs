using System.Text.Json;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class ClusterCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex UniqueNameRegex = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
    private readonly object _sync = new();
    private readonly ILogger<ClusterCatalog> _logger;
    private readonly string _directory;
    private List<ClusterDefinition> _clusters;
    private DebouncedFileWatcher? _watcher;

    public ClusterCatalog(ILogger<ClusterCatalog> logger, IConfiguration configuration)
    {
        _logger = logger;
        _directory = configuration["Quasar:ClusterCatalogPath"] ?? MagnetarPaths.GetQuasarClustersDirectory();
        _clusters = Load();
        StartWatching();
    }

    public event Action? Changed;

    public IReadOnlyList<ClusterDefinition> GetClusters()
    {
        lock (_sync)
            return _clusters.Select(cluster => cluster.Clone())
                .OrderBy(cluster => cluster.UniqueName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ClusterDefinition? GetCluster(string uniqueName)
    {
        lock (_sync)
            return _clusters.FirstOrDefault(cluster =>
                string.Equals(cluster.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    public void Dispose() => _watcher?.Dispose();

    private List<ClusterDefinition> Load()
    {
        if (!Directory.Exists(_directory))
            return [];
        var clusters = new List<ClusterDefinition>();
        foreach (string path in Directory.GetFiles(_directory, "cluster.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ClusterDefinition cluster = JsonSerializer.Deserialize<ClusterDefinition>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Cluster definition is empty.");
                Normalize(cluster);
                if (clusters.Any(existing => string.Equals(existing.UniqueName, cluster.UniqueName,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Duplicate cluster name '{cluster.UniqueName}'.");
                clusters.Add(cluster);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to load cluster definition from {Path}", path);
            }
        }
        return clusters;
    }

    private static void Normalize(ClusterDefinition cluster)
    {
        cluster.UniqueName = (cluster.UniqueName ?? string.Empty).Trim();
        if (!UniqueNameRegex.IsMatch(cluster.UniqueName))
            throw new InvalidDataException("Cluster unique name must contain only letters, digits, underscores, and hyphens.");
        cluster.DisplayName = string.IsNullOrWhiteSpace(cluster.DisplayName)
            ? cluster.UniqueName
            : cluster.DisplayName.Trim();
        cluster.GatewayUrl = (cluster.GatewayUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(cluster.GatewayUrl, UriKind.Absolute, out Uri? gateway)
            || gateway.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Cluster Gateway URL must be an absolute HTTP or HTTPS URL.");
        cluster.GatewayAdminTokenEnvironmentVariable =
            (cluster.GatewayAdminTokenEnvironmentVariable ?? string.Empty).Trim();
        cluster.HostCommandUrl = (cluster.HostCommandUrl ?? string.Empty).Trim().TrimEnd('/');
        cluster.HostCommandTokenEnvironmentVariable =
            (cluster.HostCommandTokenEnvironmentVariable ?? string.Empty).Trim();
        if (cluster.HostCommandUrl.Length != 0
            && (!Uri.TryCreate(cluster.HostCommandUrl, UriKind.Absolute, out Uri? hostCommand)
                || hostCommand.Scheme is not ("http" or "https")))
            throw new InvalidDataException("Cluster Host command URL must be an absolute HTTP or HTTPS URL.");
        if (cluster.HostCommandUrl.Length != 0 && cluster.HostCommandTokenEnvironmentVariable.Length == 0)
            throw new InvalidDataException("Cluster Host command credential environment variable is required.");
        cluster.ConfigProfileId = (cluster.ConfigProfileId ?? string.Empty).Trim();
        cluster.WorldTemplateId = (cluster.WorldTemplateId ?? string.Empty).Trim();
    }

    private void StartWatching()
    {
        _watcher = DebouncedFileWatcher.WatchDirectory(_directory, "cluster.json", includeSubdirectories: true,
            path => string.Equals(Path.GetFileName(path), "cluster.json", StringComparison.OrdinalIgnoreCase), Reload);
    }

    private void Reload()
    {
        List<ClusterDefinition> loaded = Load();
        lock (_sync)
            _clusters = loaded;
        Changed?.Invoke();
    }
}
