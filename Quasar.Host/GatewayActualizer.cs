using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal sealed class GatewayActualizer
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };
    private readonly string _stateDirectory;
    private readonly string _hostId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GatewayActualizer(string stateDirectory, string hostId)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _hostId = hostId;
    }

    internal void EnsureStopped(string clusterId)
    {
        var record = ReadRecord(clusterId);
        var match = Inspect(record);
        match.Process?.Dispose();
        if (match.State != ProcessMatchState.Missing || record?.Status == GatewayLaunchStatus.Launching)
            throw new InvalidOperationException("Gateway must be verifiably stopped before activation.");
    }

    public async Task<HostContract.GatewayStatus> ReconcileAsync(
        HostContract.GatewaySpec spec, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReconcileCoreAsync(spec, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostContract.GatewayStatus> ReconcileCoreAsync(
        HostContract.GatewaySpec spec, CancellationToken cancellationToken)
    {
        GatewayLaunchRecord? record;
        try
        {
            record = ReadRecord(spec.ClusterId);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                null, null, "launch_record_invalid:" + exception.Message);
        }

        ProcessMatch match = Inspect(record);
        if (match.State == ProcessMatchState.Conflict)
            return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                record?.ProcessId, record?.LaunchedAt, "recorded_process_identity_mismatch");

        if (record?.Status == GatewayLaunchStatus.Launching && record.ProcessId is null)
            return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                null, record.LaunchedAt, "launch_identity_not_committed");

        if (record is not null && match.State == ProcessMatchState.Alive && !RecordMatchesSpec(record, spec))
        {
            match.Process!.Dispose();
            return Status(spec, HostContract.GatewayObservedState.Failed,
                record.ProcessId, record.LaunchedAt, "running_spec_mismatch");
        }

        if (spec.Goal == HostContract.GatewayGoal.Off)
            return await ReconcileOffAsync(spec, record, match, cancellationToken);

        if (record is not null && match.State == ProcessMatchState.Alive)
        {
            using Process process = match.Process!;
            if (DateTimeOffset.UtcNow - record.LaunchedAt > StableAfter)
                lock (_respawns) _respawns.Remove(spec.ClusterId);
            return Status(spec, HostContract.GatewayObservedState.Running,
                record.ProcessId, record.LaunchedAt, null);
        }

        if (record is not null && match.State == ProcessMatchState.Missing
            && record.Status == GatewayLaunchStatus.Running)
        {
            WriteRecord(record with { Status = GatewayLaunchStatus.Failed, Failure = "process_exited" });
            return Status(spec, HostContract.GatewayObservedState.Failed,
                record.ProcessId, record.LaunchedAt, "process_exited");
        }

        // A Gateway that keeps failing is respawned with a growing delay: every attempt verifies
        // the whole bundle while the Host execution gate is held. A changed spec (a new start
        // generation from goal On included) retries at once.
        string specKey = spec.BundleManifestSha256 + "/" + spec.ConfigRevision + "/" + spec.StartGeneration;
        lock (_respawns)
        {
            long now = Environment.TickCount64;
            _respawns.TryGetValue(spec.ClusterId, out Respawn? respawn);
            if (respawn?.SpecKey != specKey) respawn = null;
            if (respawn is not null && now < respawn.NotBefore && record?.Status == GatewayLaunchStatus.Failed)
                return Status(spec, HostContract.GatewayObservedState.Failed, record.ProcessId, record.LaunchedAt,
                    (record.Failure ?? "spawn_failed") + $";respawn_in_seconds={(respawn.NotBefore - now + 999) / 1000}");
            int attempts = (respawn?.Attempts ?? 0) + 1;
            _respawns[spec.ClusterId] = new(specKey, attempts, now + RespawnDelay(attempts));
        }
        return Spawn(spec);
    }

    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, Respawn> _respawns = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Respawn(string SpecKey, int Attempts, long NotBefore);

    // 5 s, 10 s, 20 s ... capped at 5 minutes.
    internal static long RespawnDelay(int attempts) =>
        (long)Math.Min(TimeSpan.FromMinutes(5).TotalMilliseconds, 5000 * Math.Pow(2, Math.Min(attempts - 1, 10)));

    private async Task<HostContract.GatewayStatus> ReconcileOffAsync(
        HostContract.GatewaySpec spec, GatewayLaunchRecord? record, ProcessMatch match,
        CancellationToken cancellationToken)
    {
        if (spec.StopFence is { } expected
            && (record?.ProcessId != expected.ProcessId || record.LaunchedAt != expected.LaunchedAt))
        {
            match.Process?.Dispose();
            return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                record?.ProcessId, record?.LaunchedAt, "gateway_stop_fence_mismatch");
        }
        if (record is null || match.State == ProcessMatchState.Missing)
        {
            if (record is not null && record.Status != GatewayLaunchStatus.Stopped)
                WriteRecord(record with { Status = GatewayLaunchStatus.Stopped, Failure = null });
            return Status(spec, HostContract.GatewayObservedState.Missing,
                null, null, null);
        }

        using Process process = match.Process!;
        if (spec.StopFence is not { } fence || fence.ProcessId != record.ProcessId
            || fence.LaunchedAt != record.LaunchedAt)
            return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                record.ProcessId, record.LaunchedAt, "gateway_stop_fence_mismatch");
        try
        {
            await KillProcessAsync(process, cancellationToken);
            WriteRecord(record with { Status = GatewayLaunchStatus.Stopped, Failure = null });
            return Status(spec, HostContract.GatewayObservedState.Missing,
                null, null, null);
        }
        catch (InvalidOperationException exception)
        {
            return Status(spec, HostContract.GatewayObservedState.Failed,
                record.ProcessId, record.LaunchedAt, "stop_failed:" + exception.Message);
        }
    }

    private HostContract.GatewayStatus Spawn(HostContract.GatewaySpec spec)
    {
        bool processStarted = false;
        Process? process = null;
        try
        {
            VerifiedBundle bundle = LoadAndVerifyBundle(spec);
            GatewaySpawnSpec spawn = bundle.Manifest.Gateway
                ?? throw new InvalidDataException("Bundle manifest has no Gateway spawn specification");
            ValidateSpawn(spawn, bundle);
            EnsureRunRoot(spec);
            int? busyPort = FindBusyPort(spec.Ports);
            if (busyPort is not null)
                throw new UnmanagedConflictException($"reserved port {busyPort} is already in use");

            string executablePath = ResolveBundlePath(bundle.Root, spawn.Executable);
            var record = new GatewayLaunchRecord(SchemaVersion, spec.ClusterId,
                bundle.Manifest.Revision, spec.BundleManifestSha256, spec.ConfigRevision,
                executablePath, bundle.Files[NormalizeRelativePath(spawn.Executable)],
                spec.RunRoot, spec.Ports, null, null, DateTimeOffset.UtcNow,
                GatewayLaunchStatus.Launching, null, spec.StartGeneration);
            WriteRecord(record);

            process = new Process
            {
                StartInfo = CreateStartInfo(spec, spawn, bundle.Root, executablePath),
            };
            if (!process.Start())
                throw new InvalidOperationException("Process start returned false");
            processStarted = true;
            record = record with
            {
                ProcessId = process.Id,
                ProcessStartedAt = process.StartTime.ToUniversalTime(),
                ProcessIdentity = global::Quasar.Host.ProcessIdentity.Capture(process.Id),
                Status = GatewayLaunchStatus.Running,
            };
            WriteRecord(record);
            return Status(spec, HostContract.GatewayObservedState.Running,
                record.ProcessId, record.LaunchedAt, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or CryptographicException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // Still ours through this handle: stop it rather than leave a permanent conflict.
            if (processStarted && !NodeActualizer.TryStop(process!))
                return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                    null, null, "started_process_identity_not_committed");
            string failure = exception is UnmanagedConflictException
                ? "unmanaged_conflict:" + exception.Message
                : processStarted ? "spawn_commit_failed:" + exception.Message
                : "spawn_preflight_failed:" + exception.Message;
            WriteRecord(new GatewayLaunchRecord(SchemaVersion, spec.ClusterId, string.Empty,
                spec.BundleManifestSha256, spec.ConfigRevision, string.Empty, string.Empty,
                spec.RunRoot, spec.Ports, null, null, DateTimeOffset.UtcNow,
                GatewayLaunchStatus.Failed, failure));
            return Status(spec, exception is UnmanagedConflictException
                    ? HostContract.GatewayObservedState.UnmanagedConflict
                    : HostContract.GatewayObservedState.Failed,
                null, null, failure);
        }
        finally { process?.Dispose(); }
    }

    private static ProcessStartInfo CreateStartInfo(HostContract.GatewaySpec spec,
        GatewaySpawnSpec spawn, string bundleRoot, string executablePath)
    {
        string Expand(string value) => value
            .Replace("{clusterId}", spec.ClusterId, StringComparison.Ordinal)
            .Replace("{configRevision}", spec.ConfigRevision, StringComparison.Ordinal)
            .Replace("{bundleRoot}", bundleRoot, StringComparison.Ordinal)
            .Replace("{runRoot}", spec.RunRoot, StringComparison.Ordinal);
        string working = string.IsNullOrWhiteSpace(spawn.WorkingDirectory)
            ? spec.RunRoot : Expand(spawn.WorkingDirectory);
        if (!Path.IsPathFullyQualified(working))
            working = ResolveBundlePath(bundleRoot, working);
        else if (!Path.GetFullPath(working).Equals(Path.GetFullPath(spec.RunRoot),
                     OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Absolute working directory must be the Gateway run root");

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = working,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("CLUSTER_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        foreach (string argument in spawn.Arguments ?? [])
            start.ArgumentList.Add(Expand(argument));
        foreach ((string name, string value) in spawn.Environment ?? [])
            start.Environment[name] = Expand(value);
        start.Environment["CLUSTER_ID"] = spec.ClusterId;
        start.Environment["CLUSTER_DEPLOYMENT_REVISION"] = spec.ConfigRevision;
        if (spec.StartGeneration is { } generation)
            start.Environment["CLUSTER_START_GENERATION"] = generation.ToString("D");
        start.Environment["CLUSTER_START_RECOVERY"] = spec.Recover ? "true" : "false";
        ExecutionBundle.ApplySecrets(start, spawn.SecretEnvironment);
        return start;
    }

    private static void ValidateSpawn(GatewaySpawnSpec spawn, VerifiedBundle bundle)
    {
        string executable = NormalizeRelativePath(spawn.Executable);
        if (!bundle.Files.ContainsKey(executable))
            throw new InvalidDataException("Gateway executable is not covered by the bundle manifest");
    }

    private VerifiedBundle LoadAndVerifyBundle(HostContract.GatewaySpec spec)
    {
        var verified = ExecutionBundle.Load(spec.BundleManifestPath, spec.BundleManifestSha256);
        verified.InitializeData(spec.ClusterId);
        return new(verified.Root, verified.Manifest, verified.Files);
    }

    private void EnsureRunRoot(HostContract.GatewaySpec spec)
    {
        Directory.CreateDirectory(spec.RunRoot);
        SetPrivateDirectoryMode(spec.RunRoot);
        string path = Path.Combine(spec.RunRoot, ".quasar-gateway-root.json");
        var expected = new GatewayRunRootProvenance(SchemaVersion, spec.ClusterId, _hostId);
        if (File.Exists(path))
        {
            GatewayRunRootProvenance existing = JsonSerializer.Deserialize<GatewayRunRootProvenance>(
                File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Gateway run-root provenance is empty");
            if (existing != expected)
                throw new UnmanagedConflictException("Gateway run-root provenance does not match this cluster and host");
        }
        else
            WriteAtomic(path, expected);
    }

    private ProcessMatch Inspect(GatewayLaunchRecord? record)
    {
        if (record?.ProcessId is not int processId)
            return new ProcessMatch(ProcessMatchState.Missing, null);
        // Final states are written only after the process was verified gone; its PID may since
        // belong to an unrelated process and must not be read as a conflict.
        if (record.Status is GatewayLaunchStatus.Failed or GatewayLaunchStatus.Stopped)
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

    private GatewayLaunchRecord? ReadRecord(string clusterId)
    {
        string path = RecordPath(clusterId);
        if (!File.Exists(path))
            return null;
        GatewayLaunchRecord record = JsonSerializer.Deserialize<GatewayLaunchRecord>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Gateway launch record is empty");
        if (record.SchemaVersion != SchemaVersion
            || !record.ClusterId.Equals(clusterId, StringComparison.Ordinal))
            throw new InvalidDataException("Gateway launch record provenance does not match its cluster");
        return record;
    }

    private void WriteRecord(GatewayLaunchRecord record) => WriteAtomic(RecordPath(record.ClusterId), record);

    private string RecordPath(string clusterId) => Path.Combine(_stateDirectory, "gateway-launch-records",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clusterId))).ToLowerInvariant() + ".json");

    private static bool RecordMatchesSpec(GatewayLaunchRecord record, HostContract.GatewaySpec spec) =>
        record.BundleManifestSha256.Equals(spec.BundleManifestSha256, StringComparison.Ordinal)
        && record.StartGeneration == spec.StartGeneration
        && record.ConfigRevision.Equals(spec.ConfigRevision, StringComparison.Ordinal)
        && Path.GetFullPath(record.RunRoot).Equals(Path.GetFullPath(spec.RunRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && record.Ports.SequenceEqual(spec.Ports);

    private static HostContract.GatewayStatus Status(HostContract.GatewaySpec spec,
        HostContract.GatewayObservedState observed, int? processId, DateTimeOffset? launchedAt, string? failure) =>
        new(spec.ClusterId, spec.Goal, observed, spec.BundleManifestSha256, spec.ConfigRevision,
            spec.Ports, spec.RunRoot, processId, launchedAt, failure,
            observed == HostContract.GatewayObservedState.Missing ? spec.StopFence : null, spec.StartGeneration);

    private static int? FindBusyPort(int[] ports)
    {
        IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
        HashSet<int> active = properties.GetActiveTcpListeners().Select(endpoint => endpoint.Port)
            .Concat(properties.GetActiveUdpListeners().Select(endpoint => endpoint.Port)).ToHashSet();
        int port = ports.FirstOrDefault(active.Contains);
        return port == 0 ? null : port;
    }

    private static string? GetExecutablePath(Process process)
    {
        if (OperatingSystem.IsLinux())
            return File.ResolveLinkTarget($"/proc/{process.Id}/exe", returnFinalTarget: true)?.FullName;
        return process.MainModule?.FileName;
    }

    private static string ResolveBundlePath(string root, string relative)
    {
        string normalized = NormalizeRelativePath(relative);
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed record VerifiedBundle(string Root, BundleManifest Manifest,
        IReadOnlyDictionary<string, string> Files);
    private sealed record ProcessMatch(ProcessMatchState State, Process? Process);
    private enum ProcessMatchState { Missing, Alive, Conflict }
}

internal sealed record GatewaySpawnSpec(
    string Executable,
    string WorkingDirectory,
    string[] Arguments,
    Dictionary<string, string> Environment,
    Dictionary<string, string>? SecretEnvironment = null,
    int[]? ReservedPorts = null);

internal sealed record GatewayLaunchRecord(
    int SchemaVersion,
    string ClusterId,
    string BundleRevision,
    string BundleManifestSha256,
    string ConfigRevision,
    string ExecutablePath,
    string ExecutableSha256,
    string RunRoot,
    int[] Ports,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAt,
    DateTimeOffset LaunchedAt,
    GatewayLaunchStatus Status,
    string? Failure,
    Guid? StartGeneration = null,
    string? ProcessIdentity = null);

internal sealed record GatewayRunRootProvenance(int SchemaVersion, string ClusterId, string HostId);

internal enum GatewayLaunchStatus { Launching, Running, Failed, Stopped }
