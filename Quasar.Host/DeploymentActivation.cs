using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

/// <summary>Recoverable activation of attachment and Gateway specs. Caller holds Host's execution gate.</summary>
internal sealed class DeploymentActivation(string stateDirectory, string hostId,
    AttachmentStore attachments, GatewaySpecStore gateways, NodeActualizer nodes, GatewayActualizer gateway)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private string DirectoryPath => Path.Combine(stateDirectory, "deployments");
    private sealed record Transaction(HostContract.HostDeploymentActivation Request, HostContract.HostActiveDeployment Deployment, bool Applied);

    internal HostContract.HostRecoveryReadiness CheckRecovery(string clusterId, string expectedHash)
    {
        var attachment = attachments.GetAll().Single(a => a.ClusterId == clusterId);
        if (attachment.BundleManifestSha256 != expectedHash) throw new InvalidOperationException("Recovery deployment changed.");
        nodes.EnsureStopped(clusterId);
        return new(hostId, expectedHash);
    }

    internal void Recover()
    {
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            string? clusterId = null;
            try
            {
                var transaction = Read(path)!;
                clusterId = transaction.Request.ClusterId;
                if (!transaction.Applied) Apply(transaction.Request);
            }
            // One cluster's unrecoverable activation must not take the Host and its other clusters down.
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                clusterId ??= attachments.GetAll().Select(a => a.ClusterId).FirstOrDefault(id =>
                    Path.GetFileName(path) == ExecutionBundle.Hash(Encoding.UTF8.GetBytes(id)) + ".json");
                if (clusterId is null) Console.Error.WriteLine($"Deployment transaction {path} is unreadable and was ignored: {exception.Message}");
                else PausedClusters.Pause(clusterId, $"interrupted activation {path} could not be recovered: {exception.Message}");
            }
        }
    }

    internal HostContract.HostActiveDeployment Apply(HostContract.HostDeploymentActivation request, bool preview = false, bool online = false)
    {
        string path = Path.Combine(DirectoryPath,
            ExecutionBundle.Hash(Encoding.UTF8.GetBytes(request.ClusterId)) + ".json");
        Transaction? previous = Read(path);
        if (!preview && previous?.Request == request && previous.Applied) return previous.Deployment;
        if (previous is { Applied: false } && previous.Request != request)
            throw new InvalidOperationException("An interrupted activation must be recovered before a different revision is activated.");
        string? currentHash = previous?.Deployment.BundleManifestSha256
            ?? attachments.GetAll().SingleOrDefault(item => item.ClusterId == request.ClusterId)?.BundleManifestSha256;
        if (previous?.Request != request && currentHash != request.ExpectedBundleManifestSha256)
            throw new InvalidOperationException("Active deployment changed; refresh before activation.");
        if (!preview || !online)
        {
            nodes.EnsureStopped(request.ClusterId);
            gateway.EnsureStopped(request.ClusterId);
        }
        var bundle = ExecutionBundle.Load(request.BundleManifestPath, request.BundleManifestSha256);
        ProfileContentMigration.Validate(bundle, request.BundleManifestPath);
        if (bundle.Manifest.ClusterId != request.ClusterId || bundle.Manifest.HostId != hostId
            || bundle.Manifest.RuntimeRoot is not { } runtime || !Path.IsPathFullyQualified(runtime))
            throw new InvalidDataException("Execution bundle belongs to a different cluster/host or lacks a runtime root.");
        ExecutionBundle.RequireCompatibleStorage(bundle.Manifest.StorageFormats, bundle.Manifest.StorageFormats);
        var installed = attachments.GetAll().SingleOrDefault(a => a.ClusterId == request.ClusterId);
        var installedManifest = installed?.BundleManifestPath is { } installedPath
            ? ExecutionBundle.ReadManifest(installedPath, installed.BundleManifestSha256!) : null;
        if (installedManifest is not null)
            ExecutionBundle.RequireCompatibleStorage(bundle.Manifest.StorageFormats, installedManifest.StorageFormats);
        var attachment = AttachmentStore.Validate(new(request.ClusterId, request.GatewayUrl,
            request.ExecutorTokenEnvironmentVariable, request.BundleManifestPath, request.BundleManifestSha256,
            Path.Combine(runtime, "nodes")));
        HostContract.GatewaySpec? gatewaySpec = bundle.Manifest.Gateway is { } spawn
            ? GatewaySpecStore.Validate(new(request.ClusterId, HostContract.GatewayGoal.Off,
                request.BundleManifestPath, request.BundleManifestSha256, bundle.Manifest.Revision,
                spawn.ReservedPorts ?? [], Path.Combine(runtime, "gateway"), StartGeneration: Guid.NewGuid())) : null;
        if (gatewaySpec is null && gateways.GetAll().Any(spec => spec.ClusterId == request.ClusterId))
            throw new InvalidOperationException("Gateway relocation requires explicit migration; activation cannot abandon an existing Gateway.");
        var active = previous?.Request == request ? previous.Deployment
            : new HostContract.HostActiveDeployment(request.ClusterId, bundle.Manifest.Revision,
                request.BundleManifestSha256, attachment, gatewaySpec, bundle.Manifest.RequiredHosts);
        if (preview) return active;
        var transaction = new Transaction(request, active, false);
        Write(path, transaction); // Intent precedes both writes; restart replays it before any process can start.
        long started = Stopwatch.GetTimestamp();
        ProfileContentMigration.Apply(bundle, request.BundleManifestPath);
        ExecutionBundle.LogStep(request.ClusterId, "activation: profile content", started);
        started = Stopwatch.GetTimestamp();
        WorldFilesMigration.Apply(bundle, request.BundleManifestPath, installedManifest);
        ExecutionBundle.LogStep(request.ClusterId, "activation: world files", started);
        attachments.Apply(active.Attachment);
        if (active.Gateway is not null) gateways.Apply(active.Gateway);
        bundle.ConfirmRestoreActivation();
        Write(path, transaction with { Applied = true });
        PausedClusters.Resume(request.ClusterId);
        return active;
    }

    /// <summary>Stops supervising a deleted cluster: its attachment, Gateway spec and deployment record move
    /// to state/detached/. Fenced like activation: every process must be verifiably stopped. Runtime data,
    /// launch records and snapshots stay where they are. Repeating it after a crash finishes the move.</summary>
    internal HostContract.HostClusterDetach Detach(string clusterId, string expectedHash)
    {
        // The ID names a directory below.
        if (clusterId.Length == 0 || clusterId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Cluster ID must contain only letters, digits, underscores, and hyphens");
        var attachment = attachments.GetAll().SingleOrDefault(a => a.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase));
        var spec = gateways.GetAll().SingleOrDefault(g => g.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase));
        clusterId = attachment?.ClusterId ?? spec?.ClusterId ?? clusterId; // the deployment record is keyed on the exact ID
        string path = Path.Combine(DirectoryPath, ExecutionBundle.Hash(Encoding.UTF8.GetBytes(clusterId)) + ".json");
        Transaction? transaction = Read(path);
        if (transaction is { Applied: false })
            throw new InvalidOperationException("An interrupted activation must be recovered before the cluster is detached.");
        string? current = attachment?.BundleManifestSha256 ?? spec?.BundleManifestSha256 ?? transaction?.Deployment.BundleManifestSha256;
        if (!string.Equals(current ?? "", expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Active deployment changed; refresh before detaching the cluster.");
        if (spec is not null && spec.Goal != HostContract.GatewayGoal.Off)
            throw new InvalidOperationException("The Gateway goal must be Off before the cluster is detached.");
        try
        {
            nodes.EnsureStopped(clusterId);
            gateway.EnsureStopped(clusterId);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Every node and the Gateway must be verifiably stopped before the cluster is detached.");
        }
        if (attachment is null && spec is null && transaction is null)
            return new(clusterId, [], null);
        if (attachment is not null && attachments.IsConfigured(clusterId)) // the Host config would bring it back
            throw new InvalidOperationException("This attachment comes from the Host configuration file; remove it there.");
        string archive = Path.Combine(stateDirectory, "detached", $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{clusterId}");
        Directory.CreateDirectory(archive);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(archive,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var detached = new List<string>();
        if (attachments.Detach(clusterId, archive)) detached.Add("attachment");
        if (gateways.Detach(clusterId, archive)) detached.Add("gateway");
        if (transaction is not null)
        {
            File.Move(path, Path.Combine(archive, "deployment.json"));
            detached.Add("deployment");
        }
        Console.Error.WriteLine($"cluster={clusterId} detached {string.Join(", ", detached)} to {archive}");
        return new(clusterId, detached.ToArray(), archive);
    }

    /// <summary>Removes what the Host keeps of a deleted cluster once its retention has ended: the named
    /// preparation workspaces with their runtime, and its snapshots and detached records. Refused while the
    /// Host still supervises the cluster or a workspace serves an attachment. Paths that are not preparation
    /// workspaces are an operator's directories and stay. Returns the workspaces removed.</summary>
    internal string[] ReleaseDeleted(string clusterId, string[] workspaces)
    {
        if (clusterId.Length == 0 || clusterId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Cluster ID must contain only letters, digits, underscores, and hyphens");
        if (attachments.GetAll().Any(a => a.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase))
            || gateways.GetAll().Any(g => g.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase)) || IsManaged(clusterId))
            throw new InvalidOperationException("Detach the deleted cluster before releasing its data.");
        nodes.EnsureStopped(clusterId);
        gateway.EnsureStopped(clusterId);
        string conversions = Path.GetFullPath(Path.Combine(stateDirectory, "conversions"));
        var used = attachments.GetAll().Where(a => a.BundleManifestPath is not null).SelectMany(a =>
        {
            var manifest = ExecutionBundle.ReadManifest(a.BundleManifestPath!, a.BundleManifestSha256!);
            return new[] { a.BundleManifestPath!, manifest.ArtifactRoot, manifest.RuntimeRoot };
        }).OfType<string>().ToArray();
        var removed = new List<string>();
        foreach (string workspace in workspaces.Distinct())
        {
            if (!Path.IsPathFullyQualified(workspace) || Path.GetFullPath(workspace) != workspace || Path.GetDirectoryName(workspace) != conversions
                || !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _) || !Directory.Exists(workspace)
                || (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0) continue;
            if (used.Any(p => p == workspace || p.StartsWith(workspace + "/", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Workspace {workspace} serves an attached cluster.");
            Quasar.ClusterDeployment.ClusterStagingSweep.DeleteTree(workspace);
            removed.Add(workspace);
        }
        string snapshots = Path.Combine(stateDirectory, "snapshots", ExecutionBundle.Hash(Encoding.UTF8.GetBytes(clusterId)));
        if (Directory.Exists(snapshots)) Quasar.ClusterDeployment.ClusterStagingSweep.DeleteTree(snapshots);
        string detached = Path.Combine(stateDirectory, "detached");
        if (Directory.Exists(detached))
            foreach (string record in Directory.EnumerateDirectories(detached, "*-" + clusterId))
                if (Path.GetFileName(record).Split('-', 2)[1] == clusterId) Directory.Delete(record, true);
        Console.Error.WriteLine($"cluster={clusterId} released {removed.Count} workspaces of the deleted cluster");
        return removed.ToArray();
    }

    // Starting a process means nothing of this Host is in flight. Called before the command server starts.
    internal static void SweepStaging(string stateDirectory)
    {
        static void Report(string message) => Console.Error.WriteLine("storage: " + message);
        DateTime now = DateTime.UtcNow;
        try
        {
            string conversions = Path.Combine(stateDirectory, "conversions");
            string[] staging = [".stage-*", ".transfer-*", ".world-*"];
            foreach (string inputs in new[] { "installations", "mods", "worlds" })
                Quasar.ClusterDeployment.ClusterStagingSweep.Sweep(Path.Combine(conversions, inputs), now, Report, staging);
            if (Directory.Exists(conversions))
                foreach (string workspace in Directory.EnumerateDirectories(conversions).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)))
                {
                    // Recover has already run: a resumed restore finished or paused, and staging starts over on a retry.
                    Quasar.ClusterDeployment.ClusterStagingSweep.Sweep(workspace, now, Report, ".restore-*");
                    if (!Directory.EnumerateFileSystemEntries(workspace).Any()) Directory.Delete(workspace);
                }
            string snapshots = Path.Combine(stateDirectory, "snapshots");
            if (Directory.Exists(snapshots))
                foreach (string cluster in Directory.EnumerateDirectories(snapshots))
                    Quasar.ClusterDeployment.ClusterStagingSweep.Sweep(cluster, now, Report, "*.upload-*", "*.tmp");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Report("sweep stopped: " + error.Message); }
    }

    internal bool IsManaged(string clusterId) => File.Exists(Path.Combine(DirectoryPath,
        ExecutionBundle.Hash(Encoding.UTF8.GetBytes(clusterId)) + ".json"));

    private static Transaction? Read(string path) => !File.Exists(path) ? null
        : JsonSerializer.Deserialize<Transaction>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Deployment transaction is empty.");

    private static void Write(string path, Transaction value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(path)!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json)); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
}
