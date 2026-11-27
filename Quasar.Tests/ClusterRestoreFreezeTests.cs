extern alias Host;
using System.Text.Json;
using System.Text.Json.Nodes;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;
using HostApp = Host::Quasar.Host;

namespace Quasar.Tests;

// SE-1988: a restore of backup 6fd37907 brought back the game version saved with it (DS 1.211.018) over the
// 1.211.019 freeze, and left the cluster shutdown_unverified, so the deploy after it could not run until a manual
// start. A restore now keeps the current freeze unless asked otherwise, and one of a cleanly stopped backup leaves
// the cluster as cleanly down as the backup was.
public sealed class ClusterRestoreFreezeTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "cluster-restore-freeze-" + Guid.NewGuid()
    );
    private static readonly DateTimeOffset BackupStopped = new(
        2026,
        11,
        26,
        22,
        40,
        0,
        TimeSpan.Zero
    );
    private static readonly ClusterPackageSelection Old = new(
        3,
        "0.3.36",
        new string('a', 64),
        new string('1', 40),
        "old"
    );
    private static readonly ClusterPackageSelection Current = new(
        4,
        "0.3.37",
        new string('b', 64),
        new string('2', 40),
        "new"
    );

    // Dependency freezes: DS 1.211.018 saved with the backup, DS 1.211.019 frozen after it.
    private static readonly string Deps018 = new('8', 64),
        Deps019 = new('9', 64);
    private static readonly ClusterPreparationRequest BackupPreparation = new(
        "inputs-1.211.018",
        """{"clusterId":"demo"}""",
        "http://gateway.test",
        []
    );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreKeepsTheCurrentFreezeUnlessTheBackupsIsAskedFor(
        bool backupDependencies
    )
    {
        // The restore's preparation: the backup's own inputs, or the current freeze's.
        var preparation = BackupPreparation with
        {
            InputsSha256 = backupDependencies ? BackupPreparation.InputsSha256 : "inputs-1.211.019",
            SpecificationJson = """{"clusterId":"demo","joinTokenEnvironmentVariable":"restore"}""",
        };
        using var catalog = Catalog(Restored(preparation));
        var backup = Backup(ExportConsistency.Quiescent);

        await catalog.RecordRestoredProvenanceAsync(
            "demo",
            ClusterDependencyService.DeploymentRevision(preparation),
            backup.Deployment,
            "profile",
            ClusterBackupService.RestoredStop(backup),
            default
        );

        var cluster = catalog.GetCluster("demo")!;
        var expected = backupDependencies ? Old : Current;
        Assert.Equal(expected, cluster.PackageSelection);
        Assert.Equal(backupDependencies ? Deps018 : Deps019, cluster.DependencyManifestSha256);
        Assert.Equal(expected.Version, cluster.ActiveDeployment!.PackageVersion);
        Assert.Equal(expected, cluster.ActiveDeployment.Provenance!.PackageSelection);
        Assert.Equal(
            preparation.InputsSha256,
            cluster.ActiveDeployment.Provenance.Preparation.InputsSha256
        );
        // Down and deployable: the backup's clean stop is the proof, and it has run, so an update backs it up.
        Assert.True(cluster.IsProvenStopped(requireRun: true));
        Assert.Equal(BackupStopped, cluster.ActivationProof!.CleanShutdownAt);
    }

    [Fact]
    public async Task RestoreOfACrashConsistentBackupStillNeedsRecovery()
    {
        var preparation = BackupPreparation with { InputsSha256 = "inputs-1.211.019" };
        using var catalog = Catalog(Restored(preparation));
        var backup = Backup(ExportConsistency.CrashConsistent);
        Assert.Null(ClusterBackupService.RestoredStop(backup));

        await catalog.RecordRestoredProvenanceAsync(
            "demo",
            ClusterDependencyService.DeploymentRevision(preparation),
            backup.Deployment,
            "profile",
            ClusterBackupService.RestoredStop(backup),
            default
        );

        Assert.False(catalog.GetCluster("demo")!.IsProvenStopped());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("current", false)]
    [InlineData("backup", true)]
    public void DependencyChoiceDefaultsToTheCurrentFreeze(string? requested, bool backup) =>
        Assert.Equal(backup, ClusterBackupService.BackupDependencies(requested));

    [Fact]
    public void UnknownDependencyChoiceIsRefused() =>
        Assert.Throws<InvalidDataException>(() =>
            ClusterBackupService.BackupDependencies("latest")
        );

    // An update right after a restore activates another revision before the Gateway ever ran. The Gateway accepts
    // the restore authorization only for the revision it starts with, so the activation carries it over.
    [Fact]
    public void ActivationCarriesAnUntakenRestoreAuthorizationToItsRevision()
    {
        string registry = Path.Combine(root, "runtime", "gateway", "registry");
        Directory.CreateDirectory(registry);
        string marker = Path.Combine(registry, "managed-restore.json");
        var restoreId = Guid.NewGuid();
        File.WriteAllText(
            marker,
            JsonSerializer.Serialize(new { restoreId, revision = "restored" }, Json)
        );

        Bundle("updated").CarryRestoreAuthorization();

        var carried = JsonNode.Parse(File.ReadAllText(marker))!;
        Assert.Equal(restoreId, carried["restoreId"]!.GetValue<Guid>());
        Assert.Equal("updated", carried["revision"]!.GetValue<string>());

        // Nothing to carry without a restore.
        File.Delete(marker);
        Bundle("next").CarryRestoreAuthorization();
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task StartedOperationOutlivesTheRequestThatStartedIt()
    {
        var operations = new ClusterOperationStore(Path.Combine(root, "operations"));
        var release = new TaskCompletionSource();
        var started = await operations.StartAsync(
            "demo",
            "cluster.backup.restore-run",
            "run-1",
            "test",
            new { id = 1 },
            async token =>
            {
                await release.Task.WaitAsync(token);
                return new AdminEnvelope<string>(
                    AdminProtocol.Version,
                    DateTimeOffset.UtcNow,
                    "restored"
                );
            },
            CancellationToken.None
        );
        Assert.Equal(ClusterOperationState.Running, started.State);

        release.SetResult();
        ClusterOperation done;
        while ((done = operations.Get(started.OperationId)!).State == ClusterOperationState.Running)
            await Task.Delay(10);
        Assert.Equal(ClusterOperationState.Succeeded, done.State);
        Assert.Equal("restored", done.Result!.Value.GetString());

        // The same key replays the finished record instead of running again.
        var replay = await operations.StartAsync<object, string>(
            "demo",
            "cluster.backup.restore-run",
            "run-1",
            "test",
            new { id = 1 },
            _ => throw new InvalidOperationException("must not run"),
            CancellationToken.None
        );
        Assert.Equal(started.OperationId, replay.OperationId);
        Assert.Equal(ClusterOperationState.Succeeded, replay.State);
    }

    private HostApp.ExecutionBundle Bundle(string revision) =>
        new(
            root,
            new HostApp.BundleManifest(
                1,
                revision,
                [],
                [],
                new HostApp.GatewaySpawnSpec("gateway", root, [], []),
                RuntimeRoot: Path.Combine(root, "runtime")
            ),
            new Dictionary<string, string>()
        );

    // The cluster right after the restore's activation: its preparation, and the current freeze as the selection.
    private static ClusterDefinition Restored(ClusterPreparationRequest preparation) =>
        new()
        {
            UniqueName = "demo",
            GatewayUrl = "http://gateway.test",
            PackageSelection = Current,
            DependencyManifestSha256 = Deps019,
            Preparation = preparation,
            ActiveDeployment = new ClusterActiveRevision(
                ClusterDependencyService.DeploymentRevision(preparation),
                [],
                DateTimeOffset.UtcNow
            )
            {
                Provenance = new(Current, Deps019, preparation),
            },
        };

    // A backup taken after a clean Down, of the deployment on the old package and DS 1.211.018.
    private static ClusterBackup Backup(ExportConsistency consistency)
    {
        var saved = new ClusterDefinition
        {
            UniqueName = "demo",
            GatewayUrl = "http://gateway.test",
            PackageSelection = Old,
            DependencyManifestSha256 = Deps018,
            ActiveDeployment = new ClusterActiveRevision(
                ClusterDependencyService.DeploymentRevision(BackupPreparation),
                [],
                DateTimeOffset.UnixEpoch
            )
            {
                Provenance = new(Old, Deps018, BackupPreparation),
            },
        };
        saved.ShutdownProof = new(
            saved.GetLifecycleId(),
            BackupStopped,
            new GatewayStopFence(1, DateTimeOffset.UnixEpoch)
        );
        return new(Guid.NewGuid(), "demo", BackupStopped, consistency, saved, [], false);
    }

    private ClusterCatalog Catalog(ClusterDefinition cluster)
    {
        string path = Path.Combine(root, "clusters", "demo", "cluster.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cluster, Json));
        return new ClusterCatalog(
            NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters"),
                    }
                )
                .Build()
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
