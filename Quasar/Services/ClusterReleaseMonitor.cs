using Quasar.Models;
using Quasar.Services.Updates;

namespace Quasar.Services;

/// <summary>Observes published cluster packages; never stages or activates them.</summary>
public sealed class ClusterReleaseMonitor(ClusterCatalog catalog, ClusterPackageService packages,
    GitHubUpdateCredentialsCatalog credentials, QuasarUpdateOptions options,
    ILogger<ClusterReleaseMonitor> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClusterReleaseSnapshot _snapshot = new(null, null, null);

    public event Action? Changed;

    public ClusterReleaseSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    public static bool IsUpdateAvailable(ClusterDefinition cluster, ClusterPackageRelease? release) =>
        cluster.PackageSelection is { } selection && release is not null
        && Version.TryParse(selection.Version, out var selected)
        && Version.TryParse(release.Version, out var latest) && latest > selected;

    public static bool IsOnLatestRelease(ClusterDefinition cluster, ClusterPackageRelease? release)
    {
        if (release is null || cluster.ActiveDeployment is null)
            return false;

        // Existing first deployments predate the recorded active package version.
        var activeVersion = cluster.ActiveDeployment.PackageVersion
            ?? (cluster.PackageSelection is { Revision: 1 } selection
                && cluster.PreviousDeployment is null && cluster.PreparedDeployment is null
                && cluster.Update is null ? selection.Version : null);
        return string.Equals(activeVersion, release.Version, StringComparison.Ordinal);
    }

    public async Task CheckNowAsync(CancellationToken token = default)
    {
        if (!options.Enabled || !OperatingSystem.IsLinux()
            || !catalog.GetClusters().Any(cluster => cluster.PackageSelection is not null)) return;

        await _gate.WaitAsync(token);
        try
        {
            if (!credentials.HasToken)
            {
                SetSnapshot(new(null, DateTimeOffset.UtcNow,
                    "A GitHub token with access to CometWorks/cluster is required."));
                return;
            }

            try
            {
                var release = await packages.GetReleaseAsync(null, token);
                SetSnapshot(new(release, DateTimeOffset.UtcNow, null));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Cluster release check failed.");
                SetSnapshot(new(null, DateTimeOffset.UtcNow, error.Message));
            }
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !OperatingSystem.IsLinux()) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckNowAsync(stoppingToken);
                await Task.Delay(options.CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void SetSnapshot(ClusterReleaseSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke();
    }
}

public sealed record ClusterReleaseSnapshot(ClusterPackageRelease? Release,
    DateTimeOffset? LastCheckedUtc, string? Error);
