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

        if (spec.Goal == HostContract.GatewayGoal.Off)
            return await ReconcileOffAsync(spec, record, match, cancellationToken);

        if (record is not null && match.State == ProcessMatchState.Alive)
        {
            using Process process = match.Process!;
            if (!RecordMatchesSpec(record, spec))
                return Status(spec, HostContract.GatewayObservedState.Failed,
                    record.ProcessId, record.LaunchedAt, "running_spec_mismatch");
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

        if (record?.Status == GatewayLaunchStatus.Launching && record.ProcessId is null)
            return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                null, record.LaunchedAt, "launch_identity_not_committed");

        return Spawn(spec);
    }

    private async Task<HostContract.GatewayStatus> ReconcileOffAsync(
        HostContract.GatewaySpec spec, GatewayLaunchRecord? record, ProcessMatch match,
        CancellationToken cancellationToken)
    {
        if (record is null || match.State == ProcessMatchState.Missing)
        {
            if (record is not null && record.Status != GatewayLaunchStatus.Stopped)
                WriteRecord(record with { Status = GatewayLaunchStatus.Stopped, Failure = null });
            return Status(spec, HostContract.GatewayObservedState.Missing,
                null, null, null);
        }

        using Process process = match.Process!;
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
                GatewayLaunchStatus.Launching, null);
            WriteRecord(record);

            using var process = new Process
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
            if (processStarted)
                return Status(spec, HostContract.GatewayObservedState.UnmanagedConflict,
                    null, null, "started_process_identity_not_committed");
            string failure = exception is UnmanagedConflictException
                ? "unmanaged_conflict:" + exception.Message
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
        foreach (string argument in spawn.Arguments ?? [])
            start.ArgumentList.Add(Expand(argument));
        foreach ((string name, string value) in spawn.Environment ?? [])
            start.Environment[name] = Expand(value);
        start.Environment["QUASAR_CLUSTER_ID"] = spec.ClusterId;
        start.Environment["QUASAR_CLUSTER_CONFIG_REVISION"] = spec.ConfigRevision;
        start.Environment["QUASAR_CLUSTER_GATEWAY_RUN_ROOT"] = spec.RunRoot;
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
        byte[] bytes = File.ReadAllBytes(spec.BundleManifestPath);
        string manifestHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!manifestHash.Equals(NormalizeSha256(spec.BundleManifestSha256), StringComparison.Ordinal))
            throw new CryptographicException("Bundle manifest failed SHA-256 verification");
        BundleManifest manifest = JsonSerializer.Deserialize<BundleManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Bundle manifest is empty");
        if (manifest.SchemaVersion != SchemaVersion || string.IsNullOrWhiteSpace(manifest.Revision)
            || manifest.Files is null)
            throw new InvalidDataException("Bundle manifest schema, revision, or files are invalid");
        string root = Path.GetDirectoryName(spec.BundleManifestPath)!;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BundleFile file in manifest.Files)
        {
            string relative = NormalizeRelativePath(file.Path);
            string expected = NormalizeSha256(file.Sha256);
            string path = ResolveBundlePath(root, relative);
            if (!File.Exists(path))
                throw new InvalidDataException($"Bundle file '{relative}' is missing");
            if (!ComputeSha256(path).Equals(expected, StringComparison.Ordinal))
                throw new CryptographicException($"Bundle file '{relative}' failed SHA-256 verification");
            if (!files.TryAdd(relative, expected))
                throw new InvalidDataException($"Bundle file '{relative}' is duplicated");
        }
        return new VerifiedBundle(root, manifest, files);
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
        && record.ConfigRevision.Equals(spec.ConfigRevision, StringComparison.Ordinal)
        && Path.GetFullPath(record.RunRoot).Equals(Path.GetFullPath(spec.RunRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && record.Ports.SequenceEqual(spec.Ports);

    private static HostContract.GatewayStatus Status(HostContract.GatewaySpec spec,
        HostContract.GatewayObservedState observed, int? processId, DateTimeOffset? launchedAt, string? failure) =>
        new(spec.ClusterId, spec.Goal, observed, spec.BundleManifestSha256, spec.ConfigRevision,
            spec.Ports, spec.RunRoot, processId, launchedAt, failure);

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
    Dictionary<string, string> Environment);

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
    string? Failure);

internal sealed record GatewayRunRootProvenance(int SchemaVersion, string ClusterId, string HostId);

internal enum GatewayLaunchStatus { Launching, Running, Failed, Stopped }
