using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new(StringComparer.OrdinalIgnoreCase);
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

    // Caller holds the lifecycle gate and either verified stopped processes or confirmed explicit forget.
    internal async Task DeleteDefinitionAsync(ClusterDefinition expected, CancellationToken token, bool forget = false)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var current = GetCluster(expected.UniqueName) ?? throw new KeyNotFoundException(expected.UniqueName);
            if (current.GetLifecycleId() != expected.GetLifecycleId()
                || JsonSerializer.Serialize(current.ActiveDeployment, JsonOptions) != JsonSerializer.Serialize(expected.ActiveDeployment, JsonOptions)
                || !forget && (current.PendingDeploymentHash is not null || current.PendingRestoreHash is not null
                    || current.Update is { Phase: not ClusterUpdatePhase.Complete }))
                throw new InvalidOperationException("Cluster changed while deletion was being checked.");
            string path = ResolvePath(current.UniqueName);
            string history = Path.Combine(Path.GetDirectoryName(path)!, "History");
            Directory.CreateDirectory(history);
            await AtomicFileWriter.WriteTextAsync(RemovedPath(current.UniqueName), current.UniqueName, token);
            File.Move(path, Path.Combine(history, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-deleted.json"));
            lock (_sync) _clusters.RemoveAll(c => string.Equals(c.UniqueName, current.UniqueName, StringComparison.OrdinalIgnoreCase));
            _logger.LogInformation("Deleted cluster definition {Cluster}; runtime data retained", current.UniqueName);
        }
        finally { _writeGate.Release(); }
        Changed?.Invoke();
    }

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

    public async Task<ClusterDefinition> CreateAsync(ClusterCreateRequest request, CancellationToken token)
    {
        var candidate = new ClusterDefinition { UniqueName = request.UniqueName, DisplayName = request.DisplayName,
            GatewayUrl = request.GatewayUrl, GatewayAdminTokenEnvironmentVariable = request.GatewayAdminTokenEnvironmentVariable };
        Normalize(candidate);
        await _writeGate.WaitAsync(token);
        try
        {
            if (GetCluster(candidate.UniqueName) is { } existing)
            {
                if (existing.DisplayName == candidate.DisplayName && existing.GatewayUrl == candidate.GatewayUrl
                    && existing.GatewayAdminTokenEnvironmentVariable == candidate.GatewayAdminTokenEnvironmentVariable) return existing;
                throw new InvalidOperationException("A different cluster already uses this name.");
            }
            if (File.Exists(RemovedPath(candidate.UniqueName)) || Directory.Exists(Path.Combine(_directory, candidate.UniqueName, "History"))
                && Directory.EnumerateFiles(Path.Combine(_directory, candidate.UniqueName, "History"), "*-deleted.json").Any())
                throw new InvalidOperationException("This cluster ID was removed. Choose a new ID to avoid reusing retained Host state or operation history.");
            await SaveAsync(candidate, token);
            return candidate;
        }
        finally { _writeGate.Release(); }
    }

    public Task<ClusterDefinition> SetGoalStateAsync(string uniqueName, DedicatedServerGoalState goal,
        CancellationToken cancellationToken = default) =>
        WithLifecycleAsync(uniqueName, cluster => cluster.GoalState == goal
            ? Task.FromResult(cluster)
            : UpdateCoreAsync(uniqueName, current =>
            {
                if (current.PendingDeploymentHash is not null || current.PendingRestoreHash is not null || current.Update is { Phase: not ClusterUpdatePhase.Complete })
                    throw new InvalidOperationException("Complete the interrupted deployment before changing the lifecycle goal.");
                current.GoalState = goal;
                if (goal == DedicatedServerGoalState.On && current.Gateway?.StartGeneration is not null)
                    current.Gateway = current.Gateway with { StartGeneration = Guid.NewGuid(), Recover = false };
            }, cancellationToken), cancellationToken);

    public Task<ClusterDefinition> SetGatewayAsync(string uniqueName,
        Quasar.Host.Contract.V1.GatewaySpec gateway, CancellationToken cancellationToken = default) =>
        UpdateAsync(uniqueName, cluster =>
        {
            if (cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null || cluster.ActiveDeployment is not null)
                throw new InvalidOperationException("Managed Gateway specs change through deployment activation.");
            cluster.Gateway = NormalizeGatewaySpec(cluster.UniqueName, gateway);
        },
            cancellationToken);

    // Caller holds the lifecycle gate while remote Hosts converge. Commit active identity only
    // after every Host confirms the same revision; retries replay their durable activation receipts.
    internal Task<ClusterDefinition> RecordActiveDeploymentAsync(ClusterDefinition expected,
        ClusterActiveRevision active, CancellationToken token) => UpdateCoreAsync(expected.UniqueName, cluster =>
        {
            if (cluster.GetLifecycleId() != expected.GetLifecycleId())
                throw new InvalidOperationException("Cluster lifecycle changed during deployment activation.");
            var host = active.Hosts.Single(host => host.Deployment.Gateway is not null);
            cluster.PreviousDeployment = cluster.ActiveDeployment;
            cluster.ActiveDeployment = active;
            cluster.PreparedDeployment = null;
            cluster.PreparedForSelectedRelease = false;
            cluster.PendingDeploymentHash = null;
            cluster.LastRestoreHash = cluster.PendingRestoreHash ?? cluster.LastRestoreHash;
            cluster.PendingRestoreHash = null;
            cluster.Gateway = host.Deployment.Gateway;
            cluster.HostCommandUrl = host.CommandUrl;
            cluster.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
            cluster.GatewayUrl = host.Deployment.Attachment.GatewayUrl;
        }, token);

    internal async Task RecordPreparationAsync(ClusterDefinition expected, ClusterPreparationRequest request,
        ClusterDeploymentRequest deployment, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var current = GetCluster(expected.UniqueName) ?? throw new KeyNotFoundException(expected.UniqueName);
            if (current.ActiveDeployment?.Revision != expected.ActiveDeployment?.Revision
                || current.PackageSelection != expected.PackageSelection
                || current.DependencyManifestSha256 != expected.DependencyManifestSha256)
                throw new InvalidOperationException("Cluster package or deployment changed during preparation. Prepare the selected inputs again.");
            current.Preparation = request;
            current.PreparedDeployment = deployment;
            current.PreparedForSelectedRelease = false;
            await SaveAsync(current, token);
        }
        finally { _writeGate.Release(); }
    }

    internal async Task RecordSelectedReleasePreparationAsync(ClusterDefinition expected,
        ClusterDeploymentRequest deployment, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var current = GetCluster(expected.UniqueName) ?? throw new KeyNotFoundException(expected.UniqueName);
            if (current.PackageSelection != expected.PackageSelection
                || current.DependencyManifestSha256 != expected.DependencyManifestSha256
                || current.ActiveDeployment?.Revision != expected.ActiveDeployment?.Revision
                || JsonSerializer.Serialize(current.PreparedDeployment, JsonOptions) != JsonSerializer.Serialize(deployment, JsonOptions))
                throw new InvalidOperationException("Cluster inputs changed during preparation. Prepare the selected release again.");
            current.PreparedForSelectedRelease = true;
            await SaveAsync(current, token);
        }
        finally { _writeGate.Release(); }
    }

    // Workflow checkpoints do not invalidate shutdown proof unless they change the lifecycle goal.
    internal async Task<ClusterDefinition> RecordUpdateAsync(ClusterDefinition expected, ClusterUpdate workflow,
        CancellationToken token, DedicatedServerGoalState? goal = null)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var current = GetCluster(expected.UniqueName) ?? throw new KeyNotFoundException(expected.UniqueName);
            if (current.GetLifecycleId() != expected.GetLifecycleId()
                || JsonSerializer.Serialize(current.Update, JsonOptions) != JsonSerializer.Serialize(expected.Update, JsonOptions))
                throw new InvalidOperationException("Cluster changed during update checkpoint.");
            current.Update = workflow;
            if (goal is { } requested && current.GoalState != requested)
            {
                current.GoalState = requested;
                current.UpdatedAtUtc = DateTimeOffset.UtcNow;
                current.ShutdownProof = null;
                if (requested == DedicatedServerGoalState.On && current.Gateway is not null)
                    current.Gateway = current.Gateway with { StartGeneration = Guid.NewGuid(), Recover = false };
            }
            await SaveAsync(current, token);
            return current;
        }
        finally { _writeGate.Release(); }
    }

    internal async Task RecordPendingDeploymentAsync(ClusterDefinition expected, string hash, CancellationToken token, bool restore = false)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var current = GetCluster(expected.UniqueName) ?? throw new KeyNotFoundException(expected.UniqueName);
            if (current.GetLifecycleId() != expected.GetLifecycleId()
                || (restore ? current.PendingRestoreHash : current.PendingDeploymentHash) is { } pending && pending != hash)
                throw new InvalidOperationException("Another lifecycle or deployment change is pending.");
            if (restore) current.PendingRestoreHash = hash;
            else current.PendingDeploymentHash = hash;
            await SaveAsync(current, token);
        }
        finally { _writeGate.Release(); }
    }

    internal Task<ClusterDefinition> RecordConversionProfileAsync(ClusterDefinition expected, string profileId, CancellationToken token, string? worldTemplateId = null) =>
        UpdateCoreAsync(expected.UniqueName, cluster =>
        {
            if (cluster.GetLifecycleId() != expected.GetLifecycleId()) throw new InvalidOperationException("Cluster changed during conversion.");
            cluster.ConfigProfileId = profileId;
            if (worldTemplateId is not null) cluster.WorldTemplateId = worldTemplateId;
        }, token);

    internal Task<ClusterDefinition> RecordRecoveryAsync(ClusterDefinition expected, Guid generation, CancellationToken token) =>
        UpdateCoreAsync(expected.UniqueName, cluster =>
        {
            if (cluster.GetLifecycleId() != expected.GetLifecycleId() || cluster.Gateway is null)
                throw new InvalidOperationException("Cluster changed during recovery.");
            cluster.Gateway = cluster.Gateway with { StartGeneration = generation, Recover = true, StopFence = null };
            cluster.GoalState = DedicatedServerGoalState.On;
        }, token);

    internal Task<ClusterDefinition> RecordStartGenerationAsync(ClusterDefinition expected, Guid? generation,
        CancellationToken token) => UpdateCoreAsync(expected.UniqueName, cluster =>
        {
            if (cluster.GetLifecycleId() != expected.GetLifecycleId() || cluster.Gateway is null)
                throw new InvalidOperationException("Cluster lifecycle changed while adopting its running generation.");
            cluster.Gateway = cluster.Gateway with { StartGeneration = generation };
        }, token);

    public void Dispose() => _watcher?.Dispose();

    // Keep goal/spec changes and the reconciler's external effects in order. Reload
    // and package staging use the short write gate; they never wait on network I/O.
    internal async Task<T> WithLifecycleAsync<T>(string uniqueName,
        Func<ClusterDefinition, Task<T>> action, CancellationToken token)
    {
        var gate = _lifecycleGates.GetOrAdd(uniqueName, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            return await action(GetCluster(uniqueName)
                ?? throw new KeyNotFoundException($"Unknown cluster '{uniqueName}'."));
        }
        finally { gate.Release(); }
    }

    // For the background control loops: backup, restore and activation hold a cluster's gate for
    // up to hours, and waiting for it would stall every other cluster. A busy cluster is skipped
    // (false) and picked up again on a later pass.
    internal async Task<bool> TryWithLifecycleAsync(string uniqueName,
        Func<ClusterDefinition, Task> action, CancellationToken token)
    {
        var gate = _lifecycleGates.GetOrAdd(uniqueName, _ => new(1, 1));
        if (!await gate.WaitAsync(0, token)) return false;
        try
        {
            // Removed since the caller listed it.
            if (GetCluster(uniqueName) is { } cluster) await action(cluster);
            return true;
        }
        finally { gate.Release(); }
    }

    // Caller holds the lifecycle gate. Preserve candidate package changes made
    // while the Gateway request was in flight, and reject externally edited identity.
    internal async Task RecordShutdownProofAsync(ClusterDefinition expected,
        ClusterShutdownProof? proof, CancellationToken token)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            var current = GetCluster(expected.UniqueName)
                ?? throw new KeyNotFoundException($"Unknown cluster '{expected.UniqueName}'.");
            if (current.GetLifecycleId() != expected.GetLifecycleId())
                throw new ClusterOperationConflictException(409, "lifecycle_changed",
                    "Cluster lifecycle configuration changed during reconciliation.");
            current.ShutdownProof = proof;
            await SaveAsync(current, token);
        }
        finally { _writeGate.Release(); }
    }

    internal async Task<ClusterPackageSelection> SelectPackageAsync(string uniqueName,
        long expectedRevision, ClusterPackageInstallation installation, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
            throw new ArgumentException("Expected package selection revision is invalid.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128)
            throw new ArgumentException("A package selection idempotency key is required (maximum 128 characters).");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ClusterDefinition cluster = GetCluster(uniqueName)
                ?? throw new KeyNotFoundException($"Unknown cluster '{uniqueName}'.");
            var selected = new ClusterPackageSelection(expectedRevision + 1, installation.Release.Version,
                installation.Release.Sha256, installation.Commit, idempotencyKey.Trim());
            // Recover a crash after catalog persistence but before the operation result was saved.
            if (cluster.PackageSelection == selected) return selected;
            if ((cluster.PackageSelection?.Revision ?? 0) != expectedRevision)
                throw new ClusterOperationConflictException(409, "package_selection_conflict",
                    "Package selection changed; read its current revision before selecting again.");
            cluster.PackageSelection = selected;
            cluster.DependencyManifestSha256 = null;
            cluster.PreparedDeployment = null;
            cluster.PreparedForSelectedRelease = false;
            Normalize(cluster);
            // UpdatedAtUtc participates in lifecycle request identity. Package staging must not change it.
            await SaveAsync(cluster, cancellationToken);
            return selected;
        }
        finally { _writeGate.Release(); }
    }

    internal async Task SelectDependenciesAsync(string uniqueName, ClusterDependencyRequest request,
        CancellationToken cancellationToken)
    {
        ClusterDependencyService.ValidateRequest(request);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var cluster = GetCluster(uniqueName) ?? throw new KeyNotFoundException($"Unknown cluster '{uniqueName}'.");
            if (cluster.PackageSelection?.Revision != request.ExpectedPackageRevision)
                throw new ClusterOperationConflictException(409, "package_selection_conflict", "Package selection changed.");
            if (cluster.DependencyManifestSha256 == request.ManifestSha256) return;
            if (cluster.DependencyManifestSha256 != request.ExpectedDependencySha256)
                throw new ClusterOperationConflictException(409, "dependency_selection_conflict", "Dependency selection changed.");
            cluster.DependencyManifestSha256 = request.ManifestSha256;
            cluster.PreparedDeployment = null;
            cluster.PreparedForSelectedRelease = false;
            await SaveAsync(cluster, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private Task<ClusterDefinition> UpdateAsync(string uniqueName, Action<ClusterDefinition> update,
        CancellationToken cancellationToken) => WithLifecycleAsync(uniqueName,
        _ => UpdateCoreAsync(uniqueName, update, cancellationToken), cancellationToken);

    private async Task<ClusterDefinition> UpdateCoreAsync(string uniqueName, Action<ClusterDefinition> update,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ClusterDefinition cluster = GetCluster(uniqueName)
                ?? throw new KeyNotFoundException($"Unknown cluster '{uniqueName}'.");
            update(cluster);
            cluster.UpdatedAtUtc = DateTimeOffset.UtcNow;
            cluster.ShutdownProof = null;
            Normalize(cluster);
            await SaveAsync(cluster, cancellationToken);
            return cluster.Clone();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SaveAsync(ClusterDefinition cluster, CancellationToken cancellationToken)
    {
        await AtomicFileWriter.WriteTextAsync(ResolvePath(cluster.UniqueName),
            JsonSerializer.Serialize(cluster, JsonOptions), cancellationToken);
        lock (_sync)
        {
            int index = _clusters.FindIndex(existing => string.Equals(existing.UniqueName,
                cluster.UniqueName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _clusters[index] = cluster.Clone();
            else _clusters.Add(cluster.Clone());
        }
        Changed?.Invoke();
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

    private string RemovedPath(string uniqueName) => Path.Combine(_directory, ".removed", uniqueName.ToLowerInvariant());

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
        if (cluster.PackageSelection is { } selection)
        {
            ClusterPackageService.ValidateVersion(selection.Version);
            if (selection.Revision <= 0 || !ClusterPackageService.IsHash(selection.Sha256, 64)
                || !ClusterPackageService.IsHash(selection.Commit, 40)
                || string.IsNullOrWhiteSpace(selection.IdempotencyKey) || selection.IdempotencyKey.Length > 128)
                throw new InvalidDataException("Cluster package selection is invalid.");
        }
        if (cluster.DependencyManifestSha256 is { } dependencies
            && (cluster.PackageSelection is null || !ClusterPackageService.IsHash(dependencies, 64)
                || dependencies != dependencies.ToLowerInvariant()))
            throw new InvalidDataException("Cluster dependency selection is invalid.");
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
            StopFence = null,
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
        // Do not let a watcher snapshot taken before a write replace the new CAS revision.
        _writeGate.Wait();
        try
        {
            List<ClusterDefinition> loaded = Load();
            lock (_sync)
                _clusters = loaded;
        }
        finally { _writeGate.Release(); }
        Changed?.Invoke();
    }
}

public sealed record ClusterCreateRequest(string UniqueName, string DisplayName, string GatewayUrl, string GatewayAdminTokenEnvironmentVariable);
