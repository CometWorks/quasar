using System.Globalization;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Models;
using Quasar.Services.Updates;

namespace Quasar.Services;

public sealed record ClusterPluginPin(string Id, string Name, string Repository, string Commit);
public sealed record ClusterContentItem(string Kind, string Id, string Name, string? PinnedVersion,
    string? BaselineVersion, string? LatestVersion, DateTimeOffset? ObservedAt, string? Error, string? DetailsUrl)
{
    public bool HasUpdate => (PinnedVersion ?? BaselineVersion) is { } baseline
        && LatestVersion is { } latest && !string.Equals(baseline, latest, StringComparison.OrdinalIgnoreCase);
}
public sealed record ClusterContentSnapshot(string ClusterId, string DeploymentRevision, DateTimeOffset? CheckedAt,
    ClusterPluginPin[]? PluginPins, ClusterContentItem[] Items, string? InventoryError, string? CheckError)
{
    public static ClusterContentSnapshot Empty(ClusterDefinition cluster) => new(cluster.UniqueName,
        cluster.ActiveDeployment?.Revision ?? "", null, null, [], null, null);
}

/// <summary>Observes content changes. Never downloads artifacts, changes pins or activates a deployment.</summary>
public sealed class ClusterContentMonitor : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<IReadOnlyList<ClusterDefinition>> _clusters;
    private readonly Func<string, IReadOnlyList<QuasarModSelection>?> _mods;
    private readonly Func<ClusterDefinition, CancellationToken, Task<ClusterPluginPin[]>> _pins;
    private readonly Func<IEnumerable<long>, CancellationToken, Task<IReadOnlyDictionary<long, WorkshopUpdateObservation>>> _workshop;
    private readonly Func<CancellationToken, Task<IReadOnlyList<QuasarPluginCatalogEntry>>> _hub;
    private readonly QuasarUpdateOptions _options;
    private readonly ILogger<ClusterContentMonitor> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ClusterContentSnapshot> _snapshots = new(StringComparer.Ordinal);
    private DateTimeOffset _nextCheck;
    private readonly string? _loadError;
    public event Action? Changed;

    public ClusterContentMonitor(ClusterCatalog clusters, QuasarConfigProfileCatalog profiles,
        ClusterDependencyService dependencies, QuasarWorkshopModResolver workshop, QuasarPluginCatalogService hub,
        QuasarUpdateOptions options, ILogger<ClusterContentMonitor> logger)
        : this(clusters.GetClusters, id => profiles.GetProfile(id)?.Mods, dependencies.ReadActivePluginPinsAsync,
            workshop.CheckUpdatesAsync, async token => { await hub.RefreshAsync(token); return hub.GetHubEntries(); },
            Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ClusterContentUpdates.json"), options, logger) { }

    internal ClusterContentMonitor(Func<IReadOnlyList<ClusterDefinition>> clusters,
        Func<string, IReadOnlyList<QuasarModSelection>?> mods,
        Func<ClusterDefinition, CancellationToken, Task<ClusterPluginPin[]>> pins,
        Func<IEnumerable<long>, CancellationToken, Task<IReadOnlyDictionary<long, WorkshopUpdateObservation>>> workshop,
        Func<CancellationToken, Task<IReadOnlyList<QuasarPluginCatalogEntry>>> hub,
        string path, QuasarUpdateOptions options, ILogger<ClusterContentMonitor> logger)
    {
        (_clusters, _mods, _pins, _workshop, _hub, _path, _options, _logger) =
            (clusters, mods, pins, workshop, hub, path, options, logger);
        if (File.Exists(path))
        {
            try
            {
                // Do not silently reset baselines after damaged persisted state.
                _snapshots = JsonSerializer.Deserialize<Dictionary<string, ClusterContentSnapshot>>(File.ReadAllBytes(path), Json)
                    ?? throw new InvalidDataException("Cluster content observations are empty.");
                if (_snapshots.Any(p => p.Value is null || p.Value.ClusterId != p.Key
                        || p.Value.Items is null || p.Value.Items.Any(i => i is null)))
                    throw new InvalidDataException("Cluster content observations are incomplete.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogError(error, "Cannot load cluster content observations from {Path}.", path);
                _loadError = "Saved content observations are unavailable. Restore ClusterContentUpdates.json and restart Quasar; checks are paused to preserve baselines.";
                _snapshots = new(StringComparer.Ordinal);
            }
        }
    }

    public ClusterContentSnapshot GetSnapshot(ClusterDefinition cluster)
    {
        var result = Volatile.Read(ref _snapshots).TryGetValue(cluster.UniqueName, out var snapshot)
            && snapshot.DeploymentRevision == cluster.ActiveDeployment?.Revision ? snapshot : ClusterContentSnapshot.Empty(cluster);
        return _loadError is null ? result : result with { CheckError = _loadError };
    }

    public bool ScheduledChecksEnabled => _options.Enabled;

    public async Task CheckNowAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            // Coalesce UI/API requests and cap upstream polling, including failed sweeps.
            if (DateTimeOffset.UtcNow < _nextCheck) return;
            try { await CheckCoreAsync(token); }
            finally { _nextCheck = DateTimeOffset.UtcNow.AddMinutes(1); }
        }
        finally { _gate.Release(); }
    }

    internal async Task CheckCoreAsync(CancellationToken token)
    {
        if (_loadError is not null) throw new InvalidOperationException(_loadError);
        var clusters = _clusters().Where(c => c.ActiveDeployment is not null).ToArray();
        if (clusters.Length == 0) return;
        var previous = Volatile.Read(ref _snapshots);
        var next = new Dictionary<string, ClusterContentSnapshot>(StringComparer.Ordinal);
        var mods = clusters.ToDictionary(c => c.UniqueName, c => _mods(c.ConfigProfileId));
        IReadOnlyDictionary<long, WorkshopUpdateObservation> observed = new Dictionary<long, WorkshopUpdateObservation>();
        string? workshopError = null, hubError = null;
        var ids = mods.Values.SelectMany(m => m ?? []).Where(m => m.WorkshopId > 0).Select(m => m.WorkshopId).Distinct().ToArray();
        try { if (ids.Length != 0) observed = await _workshop(ids, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { _logger.LogWarning(error, "Cluster Workshop check failed."); workshopError = "Workshop check failed; previous observations retained."; }

        IReadOnlyList<QuasarPluginCatalogEntry> hub = [];
        try { hub = await _hub(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { _logger.LogWarning(error, "Cluster plugin check failed."); hubError = "Plugin hub check failed; previous observations retained."; }
        var now = DateTimeOffset.UtcNow;
        foreach (var cluster in clusters)
        {
            previous.TryGetValue(cluster.UniqueName, out var old);
            ClusterPluginPin[]? pins = old?.DeploymentRevision == cluster.ActiveDeployment!.Revision ? old.PluginPins : null;
            string? inventoryError = null;
            if (pins is null)
            {
                try { pins = await _pins(cluster, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "Cannot verify active plugin inventory for {Cluster}.", cluster.UniqueName);
                    inventoryError = "Active plugin provenance is unavailable. Selected or prepared plugins are not treated as active.";
                }
            }
            var items = new List<ClusterContentItem>();
            if (mods[cluster.UniqueName] is null)
                items.AddRange((old?.Items ?? []).Where(i => i.Kind == "mod")
                    .Select(i => i with { Error = "Configuration profile unavailable; previous observation retained." }));
            foreach (var mod in (mods[cluster.UniqueName] ?? []).Where(m => m.WorkshopId > 0).DistinctBy(m => m.WorkshopId))
            {
                string id = mod.WorkshopId.ToString(CultureInfo.InvariantCulture);
                var prior = old?.Items.FirstOrDefault(i => i.Kind == "mod" && i.Id == id);
                observed.TryGetValue(mod.WorkshopId, out var observation);
                string? error = workshopError ?? observation?.Error ?? (observation is null ? "No Workshop observation returned." : null);
                string? latest = error is null ? observation?.UpdatedTimestamp?.ToString(CultureInfo.InvariantCulture) : null;
                if (latest is null && error is null) error = "No Workshop update timestamp returned.";
                items.Add(new("mod", id, string.IsNullOrWhiteSpace(mod.DisplayName) ? observation?.Name ?? id : mod.DisplayName,
                    null, prior?.BaselineVersion ?? latest, latest ?? prior?.LatestVersion,
                    latest is null ? prior?.ObservedAt : now, error, $"https://steamcommunity.com/sharedfiles/filedetails/changelog/{id}"));
            }
            foreach (var pin in pins ?? [])
            {
                var prior = old?.Items.FirstOrDefault(i => i.Kind == "plugin" && i.Id == pin.Id && i.PinnedVersion == pin.Commit);
                items.Add(ObservePlugin(pin, hub.FirstOrDefault(e => e.PluginId == pin.Id), prior, hubError, now));
            }
            // A candidate/activation racing network requests must not publish observations as its inventory.
            if (_clusters().FirstOrDefault(c => c.UniqueName == cluster.UniqueName)?.ActiveDeployment?.Revision != cluster.ActiveDeployment.Revision)
            {
                if (old is not null) next[cluster.UniqueName] = old;
                continue;
            }
            next[cluster.UniqueName] = new(cluster.UniqueName, cluster.ActiveDeployment.Revision, now, pins,
                items.ToArray(), inventoryError, mods[cluster.UniqueName] is null
                    ? "Configuration profile is unavailable; configured mods could not be checked." : null);
        }
        await AtomicFileWriter.WriteTextAsync(_path, JsonSerializer.Serialize(next, Json), token);
        Volatile.Write(ref _snapshots, next);
        Changed?.Invoke();
    }

    internal static ClusterContentItem ObservePlugin(ClusterPluginPin pin, QuasarPluginCatalogEntry? entry,
        ClusterContentItem? prior, string? error, DateTimeOffset now)
    {
        if (error is null && (entry is null || !ClusterPackageService.IsHash(entry.SourceCommit, 40)))
            error = "Plugin manifest is unavailable or has no exact commit.";
        if (error is null && (string.IsNullOrWhiteSpace(pin.Repository)
                || !string.Equals(pin.Repository, entry!.SourceRepo, StringComparison.OrdinalIgnoreCase)))
            error = "Hub source repository differs from the active pin; review its provenance.";
        string? latest = error is null ? entry!.SourceCommit.ToLowerInvariant() : prior?.LatestVersion;
        string? link = null;
        string[] repo = pin.Repository.Split('/');
        if (repo.Length == 2 && repo.All(s => s.Length > 0 && s is not "." and not ".."
                && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            link = latest is not null && latest != pin.Commit
                ? $"https://github.com/{pin.Repository}/compare/{pin.Commit}...{latest}"
                : $"https://github.com/{pin.Repository}/commit/{pin.Commit}";
        return new("plugin", pin.Id, pin.Name, pin.Commit, null, latest,
            error is null ? now : prior?.ObservedAt, error, link);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CheckNowAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { _logger.LogError(error, "Cluster content observations could not be saved."); }
                await Task.Delay(_options.CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
