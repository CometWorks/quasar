using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal sealed class NodeActualizer
{
    private const int SchemaVersion = 1;
    private const string ReadyFileName = ".quasar-node-ready.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly string _stateDirectory;
    private readonly string _hostId;

    public NodeActualizer(string stateDirectory, string hostId)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _hostId = hostId;
    }

    internal void EnsureStopped(string clusterId)
    {
        string directory = Path.Combine(_stateDirectory, "launch-records");
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Launch record is empty.");
            if (record.ClusterId != clusterId) continue;
            var match = Inspect(record);
            match.Process?.Dispose();
            if (match.State != ProcessMatchState.Missing || record.Status == LaunchStatus.Launching)
                throw new InvalidOperationException("All node processes must be verifiably stopped before activation.");
        }
    }

    public async Task<NodeExecutionObservation[]> ReconcileAsync(HostContract.HostAttachmentSpec attachment,
        Admin.NodePlan[] plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attachment.BundleManifestPath))
            return [];

        var verified = new Lazy<Bundle>(() => LoadAndVerifyBundle(attachment));
        var observations = new List<NodeExecutionObservation>(plan.Length);
        foreach (Admin.NodePlan slot in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations.Add(await ReconcileSlotAsync(attachment, slot, verified, cancellationToken));
        }
        return observations.ToArray();
    }

    private async Task<NodeExecutionObservation> ReconcileSlotAsync(HostContract.HostAttachmentSpec attachment,
        Admin.NodePlan plan, Lazy<Bundle> verified, CancellationToken cancellationToken)
    {
        LaunchRecord? record;
        try
        {
            record = ReadRecord(attachment.ClusterId, plan.SlotKey);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return Observation(plan.SlotKey, "state-record", Admin.NodeObservation.Failed, null,
                "unmanaged_conflict:" + exception.Message);
        }

        ProcessMatch match = Inspect(record);
        bool killRequested = plan.Goal == Admin.NodeGoal.Kill
            || plan.IncumbentAction == Admin.IncumbentAction.Kill;

        if (match.State == ProcessMatchState.Conflict)
            return Observation(plan.SlotKey, record?.AttemptKey ?? "identity-conflict",
                Admin.NodeObservation.Failed, record?.NodeId,
                "unmanaged_conflict:recorded process identity does not match");

        if (record?.Status == LaunchStatus.Launching && record.ProcessId is null)
            return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed, null,
                "unmanaged_conflict:launch identity was not committed");

        if (record is not null && match.State == ProcessMatchState.Alive)
        {
            BundleManifest? manifest = TryReadManifest(attachment);
            NodeSpawnSpec? spec = manifest?.Nodes.SingleOrDefault(item => item.SlotKey == plan.SlotKey);
            if (spec is null || record.BundleManifestSha256 != attachment.BundleManifestSha256
                || manifest!.Revision != record.BundleRevision || spec.Role != plan.Role)
            {
                match.Process!.Dispose();
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed, record.NodeId,
                    "running_deployment_mismatch", record.Epoch ?? 0);
            }
            ReadyReceipt? receipt = ReadReadyReceipt(attachment, plan.SlotKey);
            if (receipt is not null && ReceiptMatches(receipt, attachment.ClusterId, record, spec))
            {
                var updated = record with { NodeId = receipt.NodeId, Epoch = receipt.Epoch,
                    Endpoint = receipt.Endpoint, Failure = receipt.Failure, Status = receipt.Ready ? LaunchStatus.Ready : LaunchStatus.Running };
                if (updated != record) WriteRecord(updated);
                record = updated;
            }
            if (!killRequested && record.Failure is not null)
            {
                match.Process!.Dispose();
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, record.Failure, record.Epoch ?? 0);
            }
        }

        if (killRequested && record is not null && match.State == ProcessMatchState.Alive)
            return await KillAsync(plan, record, match.Process!, cancellationToken);

        if (record is not null && match.State == ProcessMatchState.Missing
            && record.Status is LaunchStatus.Running or LaunchStatus.Ready)
        {
            record = record with { Status = LaunchStatus.Failed, Failure = "process_exited" };
            WriteRecord(record);
            if (!killRequested && plan.Goal == Admin.NodeGoal.Wanted)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, "process_exited", record.Epoch ?? 0);
        }

        if (killRequested)
        {
            if (record is not null)
                WriteRecord(record with { Status = LaunchStatus.Gone, Failure = null });
            return Observation(plan.SlotKey, record?.AttemptKey ?? "gone",
                Admin.NodeObservation.Gone, record?.NodeId, null, record?.Epoch ?? 0);
        }

        if (record is not null && match.State == ProcessMatchState.Alive)
        {
            using Process process = match.Process!;
            if (record.Status == LaunchStatus.Ready)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Ready,
                    record.NodeId, null, record.Epoch ?? 0);

            BundleManifest? manifest = TryReadManifest(attachment);
            NodeSpawnSpec? spec = manifest?.Nodes.SingleOrDefault(item =>
                item.SlotKey.Equals(plan.SlotKey, StringComparison.Ordinal));
            ReadyReceipt? ready = spec is null ? null : ReadReadyReceipt(attachment, plan.SlotKey);
            if (ready is { Ready: true } && spec is not null
                && ReceiptMatches(ready, attachment.ClusterId, record, spec))
            {
                record = record with
                {
                    Status = LaunchStatus.Ready,
                    NodeId = ready.NodeId,
                    Epoch = ready.Epoch,
                    Endpoint = ready.Endpoint,
                    Failure = null,
                };
                WriteRecord(record);
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Ready,
                    record.NodeId, null, record.Epoch ?? 0);
            }

            int timeout = spec?.ReadyTimeoutSeconds is > 0 ? spec.ReadyTimeoutSeconds : 120;
            if (DateTimeOffset.UtcNow - record.LaunchedAt >= TimeSpan.FromSeconds(timeout))
            {
                await KillProcessAsync(process, cancellationToken);
                record = record with { Status = LaunchStatus.Failed, Failure = "ready_timeout" };
                WriteRecord(record);
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, record.Failure, record.Epoch ?? 0);
            }
            return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Spawning,
                record.NodeId, null, record.Epoch ?? 0);
        }

        if (record?.Status == LaunchStatus.Launching && record.ProcessId is null)
            return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed, null,
                "unmanaged_conflict:launch identity was not committed");

        if (!plan.SpawnAllowed || plan.Goal != Admin.NodeGoal.Wanted)
        {
            if (record?.Status == LaunchStatus.Failed)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, record.Failure, record.Epoch ?? 0);
            return Observation(plan.SlotKey, record?.AttemptKey ?? "missing",
                Admin.NodeObservation.Missing, null, null);
        }

        return await SpawnAsync(attachment, plan, verified, cancellationToken);
    }

    private Task<NodeExecutionObservation> SpawnAsync(HostContract.HostAttachmentSpec attachment,
        Admin.NodePlan plan, Lazy<Bundle> verified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string attemptKey = Guid.NewGuid().ToString("N");
        bool processStarted = false;
        Process? process = null;
        try
        {
            Bundle bundle = verified.Value;
            NodeSpawnSpec spec = bundle.Manifest.Nodes.SingleOrDefault(item =>
                    item.SlotKey.Equals(plan.SlotKey, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"Bundle has no spawn spec for slot '{plan.SlotKey}'");
            if (spec.Role != plan.Role)
                throw new InvalidDataException(
                    $"Bundle role {spec.Role} does not match planned role {plan.Role}");
            ValidateSpec(spec, bundle);
            int? busyPort = FindBusyPort(spec.ReservedPorts);
            if (busyPort.HasValue)
                throw new UnmanagedConflictException($"reserved port {busyPort.Value} is already in use");

            string runDirectory = EnsureRunDirectory(attachment, plan.SlotKey);
            ExecutionBundle.PrepareNodeConfiguration(attachment.BundleManifestPath!, bundle.Manifest, spec, runDirectory, attachment.ClusterId);
            string readyPath = Path.Combine(runDirectory, ReadyFileName);
            if (File.Exists(readyPath))
                File.Delete(readyPath);

            string executablePath = ResolveBundlePath(bundle.Root, spec.Executable);
            string executableHash = bundle.Files[NormalizeRelativePath(spec.Executable)];
            var record = new LaunchRecord(SchemaVersion, attachment.ClusterId, plan.SlotKey,
                attemptKey, spec.NodeId, null, null, bundle.Manifest.Revision,
                attachment.BundleManifestSha256!, executablePath, executableHash, runDirectory,
                null, null, DateTimeOffset.UtcNow, LaunchStatus.Launching, null);
            // Verifying the bundle can outlast the executor lease. Give up before the Launching
            // record exists; nothing may cancel between that record and the committed identity.
            cancellationToken.ThrowIfCancellationRequested();
            WriteRecord(record);

            process = new Process
            {
                StartInfo = CreateStartInfo(attachment, plan, spec, bundle.Root, runDirectory,
                    readyPath, attemptKey, executablePath, bundle.Manifest.Revision),
            };
            if (!process.Start())
                throw new InvalidOperationException("Process start returned false");
            processStarted = true;
            DateTimeOffset startedAt = process.StartTime.ToUniversalTime();
            record = record with
            {
                ProcessId = process.Id,
                ProcessStartedAt = startedAt,
                ProcessIdentity = global::Quasar.Host.ProcessIdentity.Capture(process.Id),
                Status = LaunchStatus.Running,
            };
            WriteRecord(record);
            return Task.FromResult(Observation(plan.SlotKey, attemptKey,
                Admin.NodeObservation.Spawning, spec.NodeId, null));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or CryptographicException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // The identity of a started process could not be committed. It is still ours through
            // this handle: stop it so the slot fails cleanly instead of becoming a permanent conflict.
            if (processStarted && !TryStop(process!))
                return Task.FromResult(Observation(plan.SlotKey, attemptKey,
                    Admin.NodeObservation.Failed, null,
                    "unmanaged_conflict:started process identity could not be committed"));
            string failure = exception is UnmanagedConflictException
                ? "unmanaged_conflict:" + exception.Message
                : processStarted ? "spawn_commit_failed:" + exception.Message
                : "spawn_preflight_failed:" + exception.Message;
            WriteRecord(new LaunchRecord(SchemaVersion, attachment.ClusterId, plan.SlotKey,
                attemptKey, null, null, null, string.Empty, attachment.BundleManifestSha256!,
                string.Empty, string.Empty, attachment.RunRoot!, null, null, DateTimeOffset.UtcNow,
                LaunchStatus.Failed, failure));
            return Task.FromResult(Observation(plan.SlotKey, attemptKey,
                Admin.NodeObservation.Failed, null, failure));
        }
        finally { process?.Dispose(); }
    }

    internal static bool TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<NodeExecutionObservation> KillAsync(Admin.NodePlan plan, LaunchRecord record,
        Process process, CancellationToken cancellationToken)
    {
        using (process)
        {
            if (!string.Equals(plan.IncumbentNode, record.NodeId, StringComparison.Ordinal)
                    || plan.IncumbentEpoch <= 0 || plan.IncumbentEpoch != record.Epoch)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, "kill_authority_mismatch", record.Epoch ?? 0);
            try
            {
                await KillProcessAsync(process, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, "kill_failed:" + exception.Message, record.Epoch ?? 0);
            }
        }
        WriteRecord(record with { Status = LaunchStatus.Gone, Failure = null });
        return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Gone,
            record.NodeId, null, record.Epoch ?? 0);
    }

    private static async Task KillProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("process did not exit within 10 seconds");
        }
    }

    private static ProcessStartInfo CreateStartInfo(HostContract.HostAttachmentSpec attachment, Admin.NodePlan plan,
        NodeSpawnSpec spec, string bundleRoot, string runDirectory, string readyPath,
        string attemptKey, string executablePath, string deploymentRevision)
    {
        string role = plan.Role == Admin.NodeRole.WorldAuthority ? "WA" : "Regular";
        string Expand(string value) => value
            .Replace("{clusterId}", attachment.ClusterId, StringComparison.Ordinal)
            .Replace("{slotKey}", plan.SlotKey, StringComparison.Ordinal)
            .Replace("{nodeId}", spec.NodeId, StringComparison.Ordinal)
            .Replace("{role}", role, StringComparison.Ordinal)
            .Replace("{attemptKey}", attemptKey, StringComparison.Ordinal)
            .Replace("{runDirectory}", runDirectory, StringComparison.Ordinal)
            .Replace("{gatewayUrl}", attachment.GatewayUrl, StringComparison.Ordinal);

        string working = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
            ? runDirectory
            : Expand(spec.WorkingDirectory);
        if (!Path.IsPathFullyQualified(working))
            working = ResolveBundlePath(bundleRoot, working);
        else if (!Path.GetFullPath(working).Equals(runDirectory,
                     OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Absolute working directory must be the slot run directory");

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = working,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("CLUSTER_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        foreach (string argument in spec.Arguments ?? [])
            start.ArgumentList.Add(Expand(argument));
        foreach ((string name, string value) in spec.Environment ?? [])
            start.Environment[name] = Expand(value);
        start.Environment["CLUSTER_DEPLOYMENT_REVISION"] = deploymentRevision;
        start.Environment["CLUSTER_SLOT_ID"] = plan.SlotKey;
        start.Environment["CLUSTER_LAUNCH_ATTEMPT"] = attemptKey;
        start.Environment["CLUSTER_PROCESS_IDENTITY_PATH"] = readyPath;
        start.Environment["CLUSTER_GATEWAY_REGISTRY"] = attachment.GatewayUrl;
        start.Environment["CLUSTER_ID"] = attachment.ClusterId;
        start.Environment["CLUSTER_NODE_ID"] = spec.NodeId;
        start.Environment["CLUSTER_NODE_ROLE"] = role;
        ExecutionBundle.ApplySecrets(start, spec.SecretEnvironment);
        return start;
    }

    private Bundle LoadAndVerifyBundle(HostContract.HostAttachmentSpec attachment)
    {
        var verified = ExecutionBundle.Load(attachment.BundleManifestPath!, attachment.BundleManifestSha256!);
        verified.InitializeData(attachment.ClusterId);
        return new(verified.Root, verified.Manifest, verified.Files);
    }

    private static BundleManifest? TryReadManifest(HostContract.HostAttachmentSpec attachment)
    {
        try
        {
            return ReadManifest(attachment);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException
            or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BundleManifest ReadManifest(HostContract.HostAttachmentSpec attachment)
    {
        byte[] bytes = File.ReadAllBytes(Path.GetFullPath(attachment.BundleManifestPath!));
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actual.Equals(NormalizeSha256(attachment.BundleManifestSha256!), StringComparison.Ordinal))
            throw new CryptographicException("Bundle manifest failed SHA-256 verification");
        BundleManifest manifest = JsonSerializer.Deserialize<BundleManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Bundle manifest is empty");
        if (manifest.SchemaVersion != SchemaVersion || string.IsNullOrWhiteSpace(manifest.Revision))
            throw new InvalidDataException("Bundle manifest schema or revision is invalid");
        if (manifest.Files is null || manifest.Nodes is null)
            throw new InvalidDataException("Bundle manifest files and nodes are required");
        return manifest;
    }

    private static void ValidateSpec(NodeSpawnSpec spec, Bundle bundle)
    {
        if (string.IsNullOrWhiteSpace(spec.NodeId))
            throw new InvalidDataException("Node ID is required");
        string executable = NormalizeRelativePath(spec.Executable);
        if (!bundle.Files.ContainsKey(executable))
            throw new InvalidDataException("Node executable is not covered by the bundle manifest");
        if (spec.ReservedPorts is null || spec.ReservedPorts.Length == 0
            || spec.ReservedPorts.Any(port => port is < 1 or > 65535)
            || spec.ReservedPorts.Distinct().Count() != spec.ReservedPorts.Length)
            throw new InvalidDataException("Node spawn spec requires unique reserved ports");
        if (spec.ReadyTimeoutSeconds is < 1 or > 1800)
            throw new InvalidDataException("Ready timeout must be between 1 and 1800 seconds");
    }

    private string EnsureRunDirectory(HostContract.HostAttachmentSpec attachment, string slotKey)
    {
        string root = Path.GetFullPath(attachment.RunRoot!);
        Directory.CreateDirectory(root);
        SetPrivateDirectoryMode(root);
        string provenancePath = Path.Combine(root, ".quasar-host-root.json");
        var expected = new RunRootProvenance(SchemaVersion, attachment.ClusterId, _hostId);
        if (File.Exists(provenancePath))
        {
            RunRootProvenance existing = JsonSerializer.Deserialize<RunRootProvenance>(
                File.ReadAllText(provenancePath), JsonOptions)
                ?? throw new InvalidDataException("Run-root provenance is empty");
            if (existing != expected)
                throw new UnmanagedConflictException("run-root provenance does not match this cluster and host");
        }
        else
            WriteAtomic(provenancePath, expected);

        string slotDirectory = Path.Combine(root, SafeName(slotKey));
        Directory.CreateDirectory(slotDirectory);
        SetPrivateDirectoryMode(slotDirectory);
        return slotDirectory;
    }

    private static int? FindBusyPort(int[] ports)
    {
        IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
        HashSet<int> active = properties.GetActiveTcpListeners().Select(endpoint => endpoint.Port)
            .Concat(properties.GetActiveUdpListeners().Select(endpoint => endpoint.Port)).ToHashSet();
        int port = ports.FirstOrDefault(active.Contains);
        return port == 0 ? null : port;
    }

    private ReadyReceipt? ReadReadyReceipt(HostContract.HostAttachmentSpec attachment, string slotKey)
    {
        string path = Path.Combine(Path.GetFullPath(attachment.RunRoot!), SafeName(slotKey), ReadyFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ReadyReceipt>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static bool ReceiptMatches(ReadyReceipt receipt, string clusterId,
        LaunchRecord record, NodeSpawnSpec spec) =>
        receipt.SchemaVersion == SchemaVersion
        && receipt.ClusterId.Equals(clusterId, StringComparison.Ordinal)
        && receipt.SlotKey.Equals(record.SlotKey, StringComparison.Ordinal)
        && receipt.AttemptKey.Equals(record.AttemptKey, StringComparison.Ordinal)
        && receipt.NodeId.Equals(spec.NodeId, StringComparison.Ordinal)
        && receipt.ProcessId == record.ProcessId
        && receipt.DeploymentRevision == record.BundleRevision
        && receipt.Epoch > 0
        && !string.IsNullOrWhiteSpace(receipt.Endpoint);

    private ProcessMatch Inspect(LaunchRecord? record)
    {
        if (record?.ProcessId is not int processId)
            return new ProcessMatch(ProcessMatchState.Missing, null);
        // Final states are written only after the process was verified gone; its PID may since
        // belong to an unrelated process and must not be read as a conflict.
        if (record.Status is LaunchStatus.Failed or LaunchStatus.Gone)
            return new ProcessMatch(ProcessMatchState.Missing, null);
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return new ProcessMatch(ProcessMatchState.Missing, null);
            }
        }
        catch (ArgumentException)
        {
            return new ProcessMatch(ProcessMatchState.Missing, null);
        }
        try
        {
            bool? sameProcess = ProcessIdentity.Matches(record.ProcessIdentity, processId);
            if (sameProcess is null && record.ProcessStartedAt is { } recordedStart)
            {
                bool sameStart = Math.Abs((process.StartTime.ToUniversalTime() - recordedStart).TotalSeconds) <= 1;
                // The Windows creation time never moves, so a mismatch proves PID reuse. The Linux
                // start time is derived from the wall clock; without a recorded identity a mismatch stays a conflict.
                sameProcess = sameStart ? true : OperatingSystem.IsWindows() ? false : null;
            }
            if (sameProcess == false)
            {
                process.Dispose();
                return new ProcessMatch(ProcessMatchState.Missing, null);
            }
            string? executable = GetExecutablePath(process);
            if (sameProcess is null
                || executable is null
                || !Path.GetFullPath(executable).Equals(Path.GetFullPath(record.ExecutablePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !ComputeSha256(executable).Equals(record.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return new ProcessMatch(ProcessMatchState.Conflict, null);
            }
            return new ProcessMatch(ProcessMatchState.Alive, process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return new ProcessMatch(ProcessMatchState.Conflict, null);
        }
    }

    private static string? GetExecutablePath(Process process)
    {
        if (OperatingSystem.IsLinux())
            return File.ResolveLinkTarget($"/proc/{process.Id}/exe", returnFinalTarget: true)?.FullName;
        return process.MainModule?.FileName;
    }

    private LaunchRecord? ReadRecord(string clusterId, string slotKey)
    {
        string path = RecordPath(clusterId, slotKey);
        if (!File.Exists(path))
            return null;
        LaunchRecord record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Launch record is empty");
        if (record.SchemaVersion != SchemaVersion
            || !record.ClusterId.Equals(clusterId, StringComparison.Ordinal)
            || !record.SlotKey.Equals(slotKey, StringComparison.Ordinal))
            throw new InvalidDataException("Launch record provenance does not match its slot");
        return record;
    }

    private void WriteRecord(LaunchRecord record) => WriteAtomic(
        RecordPath(record.ClusterId, record.SlotKey), record);

    private string RecordPath(string clusterId, string slotKey)
    {
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(clusterId + "\0" + slotKey))).ToLowerInvariant();
        return Path.Combine(_stateDirectory, "launch-records", key + ".json");
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SetPrivateDirectoryMode(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        SetPrivateFileMode(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private static string ResolveBundlePath(string root, string relative)
    {
        string normalized = NormalizeRelativePath(relative);
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Bundle path escapes its root");
        string current = fullRoot;
        foreach (string segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Bundle paths must not contain symbolic links");
        }
        return path;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Bundle paths must be relative");
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Bundle path contains an invalid segment");
        return normalized;
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("SHA-256 values must contain 64 hexadecimal characters");
        return normalized;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static string SafeName(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static NodeExecutionObservation Observation(string slotKey, string attemptKey,
        Admin.NodeObservation state, string? node, string? failure, long epoch = 0) =>
        new(slotKey, attemptKey, state, node, failure, epoch);

    private sealed record Bundle(string Root, BundleManifest Manifest,
        IReadOnlyDictionary<string, string> Files);
    private sealed record ProcessMatch(ProcessMatchState State, Process? Process);
    private enum ProcessMatchState { Missing, Alive, Conflict }
}

internal sealed record BundleManifest(
    int SchemaVersion,
    string Revision,
    BundleFile[] Files,
    NodeSpawnSpec[] Nodes,
    GatewaySpawnSpec? Gateway = null,
    string? ArtifactRoot = null,
    BundleFile[]? ConfigFiles = null,
    string? RuntimeRoot = null, Dictionary<string, string>? InitialDirectories = null,
    string? ClusterId = null, string? HostId = null, string[]? RequiredHosts = null,
    Dictionary<string, string>? StorageFormats = null);

internal sealed record BundleFile(string Path, string Sha256);

internal sealed record NodeSpawnSpec(
    string SlotKey,
    Admin.NodeRole Role,
    string NodeId,
    string Executable,
    string WorkingDirectory,
    string[] Arguments,
    Dictionary<string, string> Environment,
    int[] ReservedPorts,
    int ReadyTimeoutSeconds = 120,
    Dictionary<string, string>? SecretEnvironment = null, string? ConfigurationSeed = null);

internal sealed record ReadyReceipt(
    int SchemaVersion,
    string ClusterId,
    string SlotKey,
    string AttemptKey,
    string NodeId,
    long Epoch,
    string Endpoint,
    int ProcessId,
    string DeploymentRevision,
    bool Ready = true,
    string? Failure = null);

internal sealed record LaunchRecord(
    int SchemaVersion,
    string ClusterId,
    string SlotKey,
    string AttemptKey,
    string? NodeId,
    long? Epoch,
    string? Endpoint,
    string BundleRevision,
    string BundleManifestSha256,
    string ExecutablePath,
    string ExecutableSha256,
    string RunDirectory,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAt,
    DateTimeOffset LaunchedAt,
    LaunchStatus Status,
    string? Failure,
    string? ProcessIdentity = null);

internal sealed record RunRootProvenance(int SchemaVersion, string ClusterId, string HostId);

internal enum LaunchStatus { Launching, Running, Ready, Failed, Gone }

internal sealed class UnmanagedConflictException(string message) : InvalidOperationException(message);

// Local execution result only; this is not a Gateway wire contract.
internal sealed record NodeExecutionObservation(string SlotKey, string AttemptKey,
    Admin.NodeObservation State, string? Node, string? Failure, long Epoch = 0);
