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

    public async Task<Admin.ExecutorObservation[]> ReconcileAsync(HostContract.HostAttachmentSpec attachment,
        Admin.NodePlan[] plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attachment.BundleManifestPath))
            return [];

        var observations = new List<Admin.ExecutorObservation>(plan.Length);
        foreach (Admin.NodePlan slot in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations.Add(await ReconcileSlotAsync(attachment, slot, cancellationToken));
        }
        return observations.ToArray();
    }

    private async Task<Admin.ExecutorObservation> ReconcileSlotAsync(HostContract.HostAttachmentSpec attachment,
        Admin.NodePlan plan, CancellationToken cancellationToken)
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

        if (killRequested && record is not null && match.State == ProcessMatchState.Alive)
            return await KillAsync(plan, record, match.Process!, cancellationToken);

        if (record is not null && match.State == ProcessMatchState.Missing
            && record.Status is LaunchStatus.Running or LaunchStatus.Ready)
        {
            record = record with { Status = LaunchStatus.Failed, Failure = "process_exited" };
            WriteRecord(record);
            if (!killRequested && plan.Goal == Admin.NodeGoal.Wanted)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, "process_exited");
        }

        if (killRequested)
        {
            if (record is not null)
                WriteRecord(record with { Status = LaunchStatus.Gone, Failure = null });
            return Observation(plan.SlotKey, record?.AttemptKey ?? "gone",
                Admin.NodeObservation.Gone, record?.NodeId, null);
        }

        if (record is not null && match.State == ProcessMatchState.Alive)
        {
            using Process process = match.Process!;
            if (record.Status == LaunchStatus.Ready)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Ready,
                    record.NodeId, null);

            BundleManifest? manifest = TryReadManifest(attachment);
            NodeSpawnSpec? spec = manifest?.Nodes.SingleOrDefault(item =>
                item.SlotKey.Equals(plan.SlotKey, StringComparison.Ordinal));
            ReadyReceipt? ready = spec is null ? null : ReadReadyReceipt(attachment, plan.SlotKey);
            if (ready is not null && spec is not null
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
                    record.NodeId, null);
            }

            int timeout = spec?.ReadyTimeoutSeconds is > 0 ? spec.ReadyTimeoutSeconds : 120;
            if (DateTimeOffset.UtcNow - record.LaunchedAt >= TimeSpan.FromSeconds(timeout))
            {
                await KillProcessAsync(process, cancellationToken);
                record = record with { Status = LaunchStatus.Failed, Failure = "ready_timeout" };
                WriteRecord(record);
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, record.Failure);
            }
            return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Spawning,
                record.NodeId, null);
        }

        if (record?.Status == LaunchStatus.Launching && record.ProcessId is null)
            return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed, null,
                "unmanaged_conflict:launch identity was not committed");

        if (!plan.SpawnAllowed || plan.Goal != Admin.NodeGoal.Wanted)
        {
            if (record?.Status == LaunchStatus.Failed)
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, record.Failure);
            return Observation(plan.SlotKey, record?.AttemptKey ?? "missing",
                Admin.NodeObservation.Missing, null, null);
        }

        return await SpawnAsync(attachment, plan, cancellationToken);
    }

    private Task<Admin.ExecutorObservation> SpawnAsync(HostContract.HostAttachmentSpec attachment,
        Admin.NodePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string attemptKey = Guid.NewGuid().ToString("N");
        bool processStarted = false;
        try
        {
            Bundle bundle = LoadAndVerifyBundle(attachment);
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
            string readyPath = Path.Combine(runDirectory, ReadyFileName);
            if (File.Exists(readyPath))
                File.Delete(readyPath);

            string executablePath = ResolveBundlePath(bundle.Root, spec.Executable);
            string executableHash = bundle.Files[NormalizeRelativePath(spec.Executable)];
            var record = new LaunchRecord(SchemaVersion, attachment.ClusterId, plan.SlotKey,
                attemptKey, spec.NodeId, null, null, bundle.Manifest.Revision,
                attachment.BundleManifestSha256!, executablePath, executableHash, runDirectory,
                null, null, DateTimeOffset.UtcNow, LaunchStatus.Launching, null);
            WriteRecord(record);

            using var process = new Process
            {
                StartInfo = CreateStartInfo(attachment, plan, spec, bundle.Root, runDirectory,
                    readyPath, attemptKey, executablePath),
            };
            if (!process.Start())
                throw new InvalidOperationException("Process start returned false");
            processStarted = true;
            DateTimeOffset startedAt = process.StartTime.ToUniversalTime();
            record = record with
            {
                ProcessId = process.Id,
                ProcessStartedAt = startedAt,
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
            if (processStarted)
                return Task.FromResult(Observation(plan.SlotKey, attemptKey,
                    Admin.NodeObservation.Failed, null,
                    "unmanaged_conflict:started process identity could not be committed"));
            string failure = exception is UnmanagedConflictException
                ? "unmanaged_conflict:" + exception.Message
                : "spawn_preflight_failed:" + exception.Message;
            WriteRecord(new LaunchRecord(SchemaVersion, attachment.ClusterId, plan.SlotKey,
                attemptKey, null, null, null, string.Empty, attachment.BundleManifestSha256!,
                string.Empty, string.Empty, attachment.RunRoot!, null, null, DateTimeOffset.UtcNow,
                LaunchStatus.Failed, failure));
            return Task.FromResult(Observation(plan.SlotKey, attemptKey,
                Admin.NodeObservation.Failed, null, failure));
        }
    }

    private async Task<Admin.ExecutorObservation> KillAsync(Admin.NodePlan plan, LaunchRecord record,
        Process process, CancellationToken cancellationToken)
    {
        using (process)
        {
            if (record.Status == LaunchStatus.Ready
                && (!string.Equals(plan.IncumbentNode, record.NodeId, StringComparison.Ordinal)
                    || plan.IncumbentEpoch <= 0 || plan.IncumbentEpoch != record.Epoch))
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, "kill_authority_mismatch");
            try
            {
                await KillProcessAsync(process, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Failed,
                    record.NodeId, "kill_failed:" + exception.Message);
            }
        }
        WriteRecord(record with { Status = LaunchStatus.Gone, Failure = null });
        return Observation(plan.SlotKey, record.AttemptKey, Admin.NodeObservation.Gone,
            record.NodeId, null);
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
        string attemptKey, string executablePath)
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
        foreach (string argument in spec.Arguments ?? [])
            start.ArgumentList.Add(Expand(argument));
        foreach ((string name, string value) in spec.Environment ?? [])
            start.Environment[name] = Expand(value);
        start.Environment["QUASAR_CLUSTER_ID"] = attachment.ClusterId;
        start.Environment["QUASAR_CLUSTER_SLOT"] = plan.SlotKey;
        start.Environment["QUASAR_CLUSTER_ATTEMPT"] = attemptKey;
        start.Environment["QUASAR_CLUSTER_READY_PATH"] = readyPath;
        start.Environment["SE_CLUSTER_NODE_ID"] = spec.NodeId;
        start.Environment["SE_CLUSTER_NODE_ROLE"] = role;
        return start;
    }

    private Bundle LoadAndVerifyBundle(HostContract.HostAttachmentSpec attachment)
    {
        BundleManifest manifest = ReadManifest(attachment);
        string root = Path.GetDirectoryName(Path.GetFullPath(attachment.BundleManifestPath!))!;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BundleFile file in manifest.Files)
        {
            string relative = NormalizeRelativePath(file.Path);
            string expected = NormalizeSha256(file.Sha256);
            string path = ResolveBundlePath(root, relative);
            if (!File.Exists(path))
                throw new InvalidDataException($"Bundle file '{relative}' is missing");
            string actual = ComputeSha256(path);
            if (!actual.Equals(expected, StringComparison.Ordinal))
                throw new CryptographicException($"Bundle file '{relative}' failed SHA-256 verification");
            if (!files.TryAdd(relative, expected))
                throw new InvalidDataException($"Bundle file '{relative}' is duplicated");
        }
        return new Bundle(root, manifest, files);
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
        && receipt.Epoch > 0
        && !string.IsNullOrWhiteSpace(receipt.Endpoint);

    private ProcessMatch Inspect(LaunchRecord? record)
    {
        if (record?.ProcessId is not int processId)
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
            DateTimeOffset started = process.StartTime.ToUniversalTime();
            string? executable = GetExecutablePath(process);
            if (record.ProcessStartedAt is null
                || Math.Abs((started - record.ProcessStartedAt.Value).TotalSeconds) > 1
                || executable is null
                || !Path.GetFullPath(executable).Equals(Path.GetFullPath(record.ExecutablePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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

    private static Admin.ExecutorObservation Observation(string slotKey, string attemptKey,
        Admin.NodeObservation state, string? node, string? failure) =>
        new(slotKey, attemptKey, state, node, failure);

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
    GatewaySpawnSpec? Gateway = null);

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
    int ReadyTimeoutSeconds = 120);

internal sealed record ReadyReceipt(
    int SchemaVersion,
    string ClusterId,
    string SlotKey,
    string AttemptKey,
    string NodeId,
    long Epoch,
    string Endpoint,
    int ProcessId);

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
    string? Failure);

internal sealed record RunRootProvenance(int SchemaVersion, string ClusterId, string HostId);

internal enum LaunchStatus { Launching, Running, Ready, Failed, Gone }

internal sealed class UnmanagedConflictException(string message) : InvalidOperationException(message);
