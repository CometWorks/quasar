using System.Text.Json;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class ClusterCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly Regex UniqueNameRegex = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
    private readonly object _sync = new();
    private readonly ILogger<ClusterCatalog> _logger;
    private readonly string _directory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
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

    public Task<ClusterDefinition> SetGoalStateAsync(string uniqueName, DedicatedServerGoalState goal,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(uniqueName, cluster => cluster.GoalState = goal, cancellationToken);

    public Task<ClusterDefinition> SetGatewayAsync(string uniqueName,
        Quasar.Host.Contract.V1.GatewaySpec gateway, CancellationToken cancellationToken = default) =>
        UpdateAsync(uniqueName, cluster => cluster.Gateway = NormalizeGatewaySpec(cluster.UniqueName, gateway),
            cancellationToken);

    public void Dispose() => _watcher?.Dispose();

    private async Task<ClusterDefinition> UpdateAsync(string uniqueName, Action<ClusterDefinition> update,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ClusterDefinition cluster = GetCluster(uniqueName)
                ?? throw new KeyNotFoundException($"Unknown cluster '{uniqueName}'.");
            update(cluster);
            cluster.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Normalize(cluster);
            string path = ResolvePath(cluster.UniqueName);
            await AtomicFileWriter.WriteTextAsync(path, JsonSerializer.Serialize(cluster, JsonOptions), cancellationToken);
            lock (_sync)
            {
                int index = _clusters.FindIndex(existing => string.Equals(existing.UniqueName,
                    cluster.UniqueName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) _clusters[index] = cluster.Clone();
            }
            Changed?.Invoke();
            return cluster.Clone();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string ResolvePath(string uniqueName)
    {
        string conventional = Path.Combine(_directory, uniqueName, "cluster.json");
        if (File.Exists(conventional)) return conventional;
        foreach (string path in Directory.EnumerateFiles(_directory, "cluster.json", SearchOption.AllDirectories))
        {
            try
            {
                ClusterDefinition? candidate = JsonSerializer.Deserialize<ClusterDefinition>(File.ReadAllText(path), JsonOptions);
                if (string.Equals(candidate?.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase)) return path;
            }
            catch (JsonException) { }
        }
        return conventional;
    }

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
        if (cluster.ShutdownGracePeriodSeconds is < 0 or > 3600)
            throw new InvalidDataException("Cluster shutdown grace period must be between 0 and 3600 seconds.");
        if (cluster.Gateway != null)
            cluster.Gateway = NormalizeGatewaySpec(cluster.UniqueName, cluster.Gateway);
    }

    internal static Quasar.Host.Contract.V1.GatewaySpec NormalizeGatewaySpec(string uniqueName,
        Quasar.Host.Contract.V1.GatewaySpec gateway)
    {
        string clusterId = gateway.ClusterId?.Trim() ?? string.Empty;
        if (!string.Equals(clusterId, uniqueName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Gateway spec cluster ID must match the cluster unique name.");
        string manifest = gateway.BundleManifestPath?.Trim() ?? string.Empty;
        string runRoot = gateway.RunRoot?.Trim() ?? string.Empty;
        string revision = gateway.ConfigRevision?.Trim() ?? string.Empty;
        string hash = gateway.BundleManifestSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        int[] ports = gateway.Ports ?? [];
        if (manifest.Length == 0 || runRoot.Length == 0)
            throw new ArgumentException("Gateway bundle manifest and run root are required.");
        if (revision.Length is 0 or > 256)
            throw new ArgumentException("Gateway config revision is required and cannot exceed 256 characters.");
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Gateway bundle manifest SHA-256 must contain 64 hexadecimal characters.");
        if (ports.Length == 0 || ports.Any(port => port is < 1 or > 65535)
            || ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("Gateway ports must contain unique values between 1 and 65535.");
        return gateway with
        {
            ClusterId = uniqueName,
            Goal = Quasar.Host.Contract.V1.GatewayGoal.On,
            BundleManifestPath = manifest,
            BundleManifestSha256 = hash,
            ConfigRevision = revision,
            Ports = ports.Order().ToArray(),
            RunRoot = runRoot,
        };
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
