using System.Diagnostics;
using Quasar.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal sealed class GatewayActualizer : IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };
    private readonly string _stateDirectory;
    private readonly string _hostId;
    private readonly DiagnosticCollector _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GatewayActualizer(string stateDirectory, string hostId, DiagnosticCollector? diagnostics = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _hostId = hostId;
        _diagnostics = diagnostics ?? DiagnosticStorage.Create(Path.Combine(_stateDirectory, "diagnostics"), hostId,
            Environment.GetEnvironmentVariable("QUASAR_HOST_DIAGNOSTICS_DIRECTORY"));
    }

    /// <summary>Stops following the Gateways' output, as the Host's exit does. The Gateways keep running.</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            foreach (string clusterId in _outputs.Keys.ToArray())
                DropOutput(clusterId);
        }
        finally { _gate.Release(); }
    }

    internal void EnsureStopped(string clusterId)
    {
        var record = ReadRecord(clusterId);
        var match = Inspect(record);
        match.Process?.Dispose();
        if (match.State != ProcessMatchState.Missing || record?.Status == GatewayLaunchStatus.Launching)
            throw new InvalidOperationException("Gateway must be verifiably stopped before activation.");
        lock (_preparations)
            if (_preparations.TryGetValue(clusterId, out Preparation? preparation) && !preparation.Task.IsCompleted)
                throw new InvalidOperationException("The Gateway's start is still preparing its runtime data; try again when it has started.");
    }

    public async Task<HostContract.GatewayStatus> ReconcileAsync(
        HostContract.GatewaySpec spec, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            HostContract.GatewayStatus status = await ReconcileCoreAsync(spec, cancellationToken);
            if (status.Observed == HostContract.GatewayObservedState.Failed && spec.Goal == HostContract.GatewayGoal.On)
                lock (_respawns)
                    if (_respawns.TryGetValue(spec.ClusterId, out Respawn? respawn) && respawn.SpecKey == SpecKey(spec))
                        status = status with { LaunchAttempts = respawn.Attempts };
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>An Off goal stops only the process its fence names. Persisted over any other live process, it would
    /// leave that Gateway unsupervised. Callers hold the Host execution gate, so no respawn can come between this
    /// check and persisting the goal. A record that cannot be read is left to reconcile, which reports the conflict.</summary>
    internal void RefuseOffOverLiveProcess(HostContract.GatewaySpec spec)
    {
        if (spec.Goal != HostContract.GatewayGoal.Off) return;
        GatewayLaunchRecord? record;
        try
        {
            record = ReadRecord(spec.ClusterId);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return;
        }
        ProcessMatch match = Inspect(record);
        match.Process?.Dispose();
        if (match.State == ProcessMatchState.Alive
            && (spec.StopFence?.ProcessId != record!.ProcessId || spec.StopFence.LaunchedAt != record.LaunchedAt))
            throw new InvalidOperationException("The Gateway process is alive and this Off goal does not name it.");
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
            if (!_outputs.ContainsKey(spec.ClusterId))
                AdoptOutput(spec, record, process.Id);
            if (await ControlUnhealthyAsync(spec, record, cancellationToken) is { } unhealthy)
                return await RestartUnhealthyAsync(spec, record, process, unhealthy, cancellationToken);
            if (DateTimeOffset.UtcNow - record.LaunchedAt > StableAfter)
                lock (_respawns) _respawns.Remove(spec.ClusterId);
            return Status(spec, HostContract.GatewayObservedState.Running,
                record.ProcessId, record.LaunchedAt, null) with { Metrics = SampleMetrics(spec.ClusterId, process) };
        }

        if (record is not null && match.State == ProcessMatchState.Missing
            && record.Status == GatewayLaunchStatus.Running)
        {
            string failure = "process_exited";
            string[]? output = null;
            if (_outputs.Remove(spec.ClusterId, out ProcessOutputTail? tail))
                using (tail)
                {
                    (int? exitCode, output) = await tail.DrainAsync(cancellationToken);
                    if (exitCode is not null)
                        failure += ":exit_code=" + exitCode;
                }
            if (record.DiagnosticDirectory is { } diagnosticDirectory) DiagnosticCollector.Exited(diagnosticDirectory, null, false);
            WriteRecord(record with { Status = GatewayLaunchStatus.Failed, Failure = failure, Output = output });
            return Status(spec, HostContract.GatewayObservedState.Failed,
                record.ProcessId, record.LaunchedAt, failure, output);
        }

        // A Gateway that keeps failing is respawned with a growing delay: every attempt verifies
        // the whole bundle. A changed spec (a new start generation from goal On included) retries at once.
        string specKey = SpecKey(spec);
        lock (_respawns)
        {
            long now = Environment.TickCount64;
            _respawns.TryGetValue(spec.ClusterId, out Respawn? respawn);
            if (respawn?.SpecKey == specKey && now < respawn.NotBefore && record?.Status == GatewayLaunchStatus.Failed)
                return Status(spec, HostContract.GatewayObservedState.Failed, record.ProcessId, record.LaunchedAt,
                    (record.Failure ?? "spawn_failed") + $";respawn_in_seconds={(respawn.NotBefore - now + 999) / 1000}",
                    record.Output);
        }
        Task<VerifiedBundle> preparation = Prepare(spec, specKey);
        await Task.WhenAny(preparation, Task.Delay(PreparationWait, cancellationToken));
        if (!preparation.IsCompleted)
            return Status(spec, HostContract.GatewayObservedState.Missing, null, null, null);
        lock (_preparations) _preparations.Remove(spec.ClusterId);
        lock (_respawns)
        {
            _respawns.TryGetValue(spec.ClusterId, out Respawn? respawn);
            int attempts = (respawn?.SpecKey == specKey ? respawn.Attempts : 0) + 1;
            _respawns[spec.ClusterId] = new(specKey, attempts, Environment.TickCount64 + RespawnDelay(attempts));
        }
        return Spawn(spec, preparation);
    }

    // The bundle check and the data seeding of a first start (every node's data and the world: 35 s for
    // 8 nodes on ai) outlast Quasar's 30 s Host command deadline, and held the execution gate, so every
    // executor heartbeat on this Host, as long (#1433). They run in the background instead. Until they are
    // done the Gateway stays Missing without a failure, which Quasar shows as Converging; the next
    // reconcile, the Host loop's or Quasar's, launches it.
    internal TimeSpan PreparationWait { get; init; } = TimeSpan.FromSeconds(5);
    internal Func<Task>? BeforePreparation { get; init; }
    private readonly Dictionary<string, Preparation> _preparations = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Preparation(string SpecKey, Task<VerifiedBundle> Task);

    private Task<VerifiedBundle> Prepare(HostContract.GatewaySpec spec, string specKey)
    {
        lock (_preparations)
        {
            _preparations.TryGetValue(spec.ClusterId, out Preparation? current);
            if (current?.SpecKey == specKey) return current.Task;
            // Seeding writes the runtime root: one preparation per cluster at a time.
            Task previous = current?.Task ?? Task.CompletedTask;
            Task<VerifiedBundle> task = Task.Run(async () =>
            {
                await Task.WhenAny(previous);
                // Hard-linked mods come from the content store; a collection must not prune it meanwhile.
                using var storage = await Quasar.ClusterDeployment.ClusterStorageActivity.BeginAsync(CancellationToken.None);
                if (BeforePreparation is { } before) await before();
                return LoadAndVerifyBundle(spec);
            });
            _preparations[spec.ClusterId] = new(specKey, task);
            return task;
        }
    }

    // SE-1171: after a persistence failure the Gateway stays alive but answers every control request with 503
    // registry_persistence_failed until it is restarted (cluster Docs/Gateway/StateDurability.md). PID liveness
    // missed that, so players lost the cluster until someone noticed. Each reconcile asks the control endpoint;
    // once it has answered that way for UnhealthyRestartAfter, the Host stops the Gateway and the respawn below
    // starts it again with the same spec and start generation, as after an exit. Any other answer, or none (a
    // Gateway still starting), resets the count. QUASAR_HOST_GATEWAY_UNHEALTHY_RESTART_SECONDS, 0 = never.
    internal TimeSpan UnhealthyRestartAfter { get; init; } = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("QUASAR_HOST_GATEWAY_UNHEALTHY_RESTART_SECONDS"), out int seconds) && seconds >= 0
            ? seconds : 60);
    private static readonly HttpClient ControlProbe = new() { Timeout = TimeSpan.FromSeconds(2) };
    // Guarded by _gate: since when the running Gateway's control has answered persistence failure, and its control URL per spec.
    private readonly Dictionary<string, (int? ProcessId, long Since)> _unhealthy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string SpecKey, string? Url)> _controlUrls = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string?> ControlUnhealthyAsync(HostContract.GatewaySpec spec, GatewayLaunchRecord record,
        CancellationToken cancellationToken)
    {
        if (UnhealthyRestartAfter <= TimeSpan.Zero || ControlUrl(spec) is not { } url) return null;
        bool failed;
        try
        {
            using HttpResponseMessage response = await ControlProbe.GetAsync(url + "/registry/status", cancellationToken);
            failed = response.StatusCode == HttpStatusCode.ServiceUnavailable
                && (await response.Content.ReadAsStringAsync(cancellationToken)).Contains("registry_persistence_failed", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            failed = false;
        }
        if (!failed)
        {
            _unhealthy.Remove(spec.ClusterId);
            return null;
        }
        long now = Environment.TickCount64;
        if (!_unhealthy.TryGetValue(spec.ClusterId, out var since) || since.ProcessId != record.ProcessId)
        {
            _unhealthy[spec.ClusterId] = since = (record.ProcessId, now);
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} cluster={spec.ClusterId} Gateway control answers 503 registry_persistence_failed;"
                + $" it is restarted if that lasts {UnhealthyRestartAfter.TotalSeconds:0} s");
        }
        long seconds = (now - since.Since) / 1000;
        return now - since.Since >= UnhealthyRestartAfter.TotalMilliseconds
            ? $"control_unhealthy:registry_persistence_failed_for_seconds={seconds}" : null;
    }

    private async Task<HostContract.GatewayStatus> RestartUnhealthyAsync(HostContract.GatewaySpec spec,
        GatewayLaunchRecord record, Process process, string failure, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} cluster={spec.ClusterId} restarting the Gateway (pid {record.ProcessId},"
            + $" start generation {spec.StartGeneration}): {failure}");
        if (record.DiagnosticDirectory is { } diagnosticDirectory)
        {
            DiagnosticCollector.ReportFailure(diagnosticDirectory, "gateway_control_unhealthy",
                "The Gateway answered 503 registry_persistence_failed and was restarted: " + failure);
            DiagnosticCollector.ExpectExit(diagnosticDirectory);
        }
        try
        {
            await KillProcessAsync(process, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return Status(spec, HostContract.GatewayObservedState.Failed,
                record.ProcessId, record.LaunchedAt, failure + ";stop_failed:" + exception.Message);
        }
        _unhealthy.Remove(spec.ClusterId);
        string[]? output = null;
        if (_outputs.Remove(spec.ClusterId, out ProcessOutputTail? tail))
            using (tail)
                (_, output) = await tail.DrainAsync(cancellationToken);
        WriteRecord(record with { Status = GatewayLaunchStatus.Failed, Failure = failure, Output = output });
        return Status(spec, HostContract.GatewayObservedState.Failed, record.ProcessId, record.LaunchedAt, failure, output);
    }

    /// <summary>The Gateway's control URL from its spawn spec: the last <c>--control</c> argument, else
    /// <c>CLUSTER_CONTROL</c> (the Gateway reads the same two). A wildcard bind is asked on loopback.</summary>
    private string? ControlUrl(HostContract.GatewaySpec spec)
    {
        string specKey = SpecKey(spec);
        if (_controlUrls.TryGetValue(spec.ClusterId, out var cached) && cached.SpecKey == specKey) return cached.Url;
        string? url;
        try
        {
            url = ControlUrl(ExecutionBundle.ReadManifest(spec.BundleManifestPath, spec.BundleManifestSha256).Gateway);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException
            or UnauthorizedAccessException or CryptographicException)
        {
            url = null;
        }
        _controlUrls[spec.ClusterId] = (specKey, url);
        return url;
    }

    internal static string? ControlUrl(GatewaySpawnSpec? spawn)
    {
        if (spawn is null) return null;
        string? control = null;
        string[] arguments = spawn.Arguments ?? [];
        for (int i = 0; i + 1 < arguments.Length; i++)
            if (arguments[i] == "--control") control = arguments[i + 1];
        control ??= spawn.Environment?.GetValueOrDefault("CLUSTER_CONTROL");
        if (string.IsNullOrWhiteSpace(control)) return null;
        if (!IPEndPoint.TryParse(control, out IPEndPoint? endpoint)) return "http://" + control.Trim();
        if (endpoint.Address.Equals(IPAddress.Any)) endpoint.Address = IPAddress.Loopback;
        else if (endpoint.Address.Equals(IPAddress.IPv6Any)) endpoint.Address = IPAddress.IPv6Loopback;
        if (endpoint.Port == 0) endpoint.Port = 28016;   // the Gateway's default control port
        return "http://" + endpoint;
    }

    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, Respawn> _respawns = new(StringComparer.OrdinalIgnoreCase);
    // Output of the Gateways this Host process started or adopted; guarded by _gate.
    private readonly Dictionary<string, ProcessOutputTail> _outputs = new(StringComparer.OrdinalIgnoreCase);
    // Running Gateways this Host process adopted, so it says only once that it can't follow one.
    private readonly HashSet<(string ClusterId, int ProcessId)> _adoptions = [];
    private readonly Dictionary<string, (int Pid, DateTime Started, long Timestamp, TimeSpan Cpu)> _cpuSamples = new(StringComparer.OrdinalIgnoreCase);

    private HostContract.GatewayProcessMetrics? SampleMetrics(string clusterId, Process process)
    {
        try
        {
            process.Refresh();
            long timestamp = Stopwatch.GetTimestamp();
            var started = process.StartTime.ToUniversalTime();
            var cpu = process.TotalProcessorTime;
            double? percent = null;
            if (_cpuSamples.TryGetValue(clusterId, out var previous) && previous.Pid == process.Id && previous.Started == started)
            {
                double elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
                if (elapsed > 0 && cpu >= previous.Cpu)
                    percent = (cpu - previous.Cpu).TotalSeconds / elapsed * 100;
            }
            _cpuSamples[clusterId] = (process.Id, started, timestamp, cpu);
            return new(DateTimeOffset.UtcNow, percent, process.WorkingSet64);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _cpuSamples.Remove(clusterId);
            return null;
        }
    }
    private sealed record Respawn(string SpecKey, int Attempts, long NotBefore);

    private static string SpecKey(HostContract.GatewaySpec spec) =>
        spec.BundleManifestSha256 + "/" + spec.ConfigRevision + "/" + spec.StartGeneration;

    private void DropOutput(string clusterId)
    {
        if (_outputs.Remove(clusterId, out ProcessOutputTail? tail))
            tail.Dispose();
    }

    private static string OutputPrefix(HostContract.GatewaySpec spec) => $"[gateway {spec.ClusterId}] ";

    /// <summary>The file the Gateway appends its stdout and stderr to; null where it writes into pipes (not Linux).</summary>
    private static string? OutputLogPath(HostContract.GatewaySpec spec) =>
        OperatingSystem.IsLinux() ? Path.Combine(spec.RunRoot, "gateway.log") : null;

    // The Gateway outlives a Host restart; a pipe would lose its output with the Host that reads it.
    // The shell opens the log for appending and execs the Gateway in place, so the PID stays the Gateway's.
    private static void AppendOutputTo(ProcessStartInfo start, string log)
    {
        string[] arguments = [.. start.ArgumentList];
        start.ArgumentList.Clear();
        foreach (string argument in (string[])["-c", "log=$1; shift; exec \"$@\" >>\"$log\" 2>&1", "sh", log, start.FileName, .. arguments])
            start.ArgumentList.Add(argument);
        start.FileName = "/bin/sh";
        start.RedirectStandardOutput = start.RedirectStandardError = false;
    }

    // Until the shell execs, /proc/PID/exe is the shell, which Inspect would take for another process.
    private static void WaitForExec(Process process, string executablePath)
    {
        string expected = File.ResolveLinkTarget(executablePath, returnFinalTarget: true)?.FullName
            ?? Path.GetFullPath(executablePath);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && !process.HasExited)
        {
            try
            {
                if (GetExecutablePath(process) == expected)
                    return;
            }
            catch (IOException) { }
            Thread.Sleep(5);
        }
    }

    // A running Gateway this Host process did not start: an earlier Host started it. Follow its log from
    // the end, or say once that its output is lost: one started by a Host before the log file wrote into
    // that Host's pipe, which nothing reads any more.
    private void AdoptOutput(HostContract.GatewaySpec spec, GatewayLaunchRecord record, int processId)
    {
        if (OutputLogPath(spec) is not { } log || !_adoptions.Add((spec.ClusterId, processId)))
            return;
        string? target = null;
        try
        {
            // The kernel names the file by its resolved path, which differs from the spec's if the run root has a symlink.
            target = new FileInfo($"/proc/{processId}/fd/1").LinkTarget;
            if (target is not null && Path.GetFileName(target) == Path.GetFileName(log) && File.Exists(target))
            {
                _outputs[spec.ClusterId] = ProcessOutputTail.Follow(null, OutputPrefix(spec), record.DiagnosticDirectory,
                    target, new FileInfo(target).Length);
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        Console.Error.WriteLine($"{OutputPrefix(spec)}The Gateway (PID {processId}) was started by an earlier Host and "
            + $"writes its output to {target ?? "an unknown target"}, not {log}. This Host can't show it; "
            + "a restart of the Gateway fixes that.");
    }

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
            DropOutput(spec.ClusterId);
            lock (_preparations)
                if (_preparations.TryGetValue(spec.ClusterId, out Preparation? preparation) && preparation.Task.IsCompleted)
                    _preparations.Remove(spec.ClusterId);
            if (record is not null && record.Status != GatewayLaunchStatus.Stopped)
                WriteRecord(record with { Status = GatewayLaunchStatus.Stopped, Failure = null, Output = null });
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
            if (record.DiagnosticDirectory is { } diagnosticDirectory) DiagnosticCollector.ExpectExit(diagnosticDirectory);
            await KillProcessAsync(process, cancellationToken);
            DropOutput(spec.ClusterId);
            WriteRecord(record with { Status = GatewayLaunchStatus.Stopped, Failure = null, Output = null });
            return Status(spec, HostContract.GatewayObservedState.Missing,
                null, null, null);
        }
        catch (InvalidOperationException exception)
        {
            return Status(spec, HostContract.GatewayObservedState.Failed,
                record.ProcessId, record.LaunchedAt, "stop_failed:" + exception.Message);
        }
    }

    private HostContract.GatewayStatus Spawn(HostContract.GatewaySpec spec, Task<VerifiedBundle> preparation)
    {
        bool processStarted = false;
        Process? process = null;
        try
        {
            VerifiedBundle bundle = preparation.GetAwaiter().GetResult();
            GatewaySpawnSpec spawn = bundle.Manifest.Gateway
                ?? throw new InvalidDataException("Bundle manifest has no Gateway spawn specification");
            ValidateSpawn(spawn, bundle);
            if (spec.PlayerAccess?.Enabled == true)
            {
                const string capability = "Package/cli/deployment-capabilities.json";
                if (!bundle.Files.ContainsKey(capability))
                    throw new InvalidDataException("Gateway bundle does not declare player authentication support.");
                using var capabilities = JsonDocument.Parse(File.ReadAllBytes(ResolveBundlePath(bundle.Root, capability)));
                if (!capabilities.RootElement.TryGetProperty("playerAuthentication", out var version) || version.GetInt32() < 1)
                    throw new InvalidDataException("Update the Gateway package before starting with player authentication.");
            }
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

            string? outputLog = OutputLogPath(spec);
            long outputOffset = 0;
            if (outputLog is not null)
                // In the private run root. Each launch appends; the Host follows from where the file ended.
                using (var log = new FileStream(outputLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    outputOffset = log.Length;
            process = new Process
            {
                StartInfo = CreateStartInfo(spec, spawn, bundle.Root, executablePath),
            };
            string? diagnosticDirectory = _diagnostics.Configure(process.StartInfo, new()
            {
                ["clusterId"] = spec.ClusterId, ["role"] = "gateway-registry",
                ["deploymentRevision"] = spec.ConfigRevision, ["launchAttempt"] = Guid.NewGuid().ToString("N"),
            }, native: false);
            record = record with { DiagnosticDirectory = diagnosticDirectory };
            WriteRecord(record);
            if (outputLog is not null)
                AppendOutputTo(process.StartInfo, outputLog);
            if (!process.Start())
                throw new InvalidOperationException("Process start returned false");
            processStarted = true;
            if (outputLog is not null)
                WaitForExec(process, executablePath);
            _diagnostics.Started(diagnosticDirectory, process, ProcessIdentity.Capture(process.Id));
            var output = outputLog is null
                ? new ProcessOutputTail(process, OutputPrefix(spec), diagnosticDirectory)
                : ProcessOutputTail.Follow(process, OutputPrefix(spec), diagnosticDirectory, outputLog, outputOffset);
            record = record with
            {
                ProcessId = process.Id,
                ProcessStartedAt = process.StartTime.ToUniversalTime(),
                ProcessIdentity = global::Quasar.Host.ProcessIdentity.Capture(process.Id),
                Status = GatewayLaunchStatus.Running,
            };
            WriteRecord(record);
            DropOutput(spec.ClusterId);
            _outputs[spec.ClusterId] = output;
            process = null; // Owned by the output tail now.
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

    internal static ProcessStartInfo CreateStartInfo(HostContract.GatewaySpec spec,
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
            // Read by ProcessOutputTail, so a failure report can include the Gateway's last output lines.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The Gateway reads no Quasar value; its diagnostics settings are added by DiagnosticCollector.Configure.
        ExecutionBundle.RestrictEnvironment(start);
        foreach (string argument in spawn.Arguments ?? [])
            start.ArgumentList.Add(Expand(argument));
        foreach ((string name, string value) in spawn.Environment ?? [])
            start.Environment[name] = Expand(value);
        start.Environment["CLUSTER_ID"] = spec.ClusterId;
        start.Environment["CLUSTER_DEPLOYMENT_REVISION"] = spec.ConfigRevision;
        if (spec.StartGeneration is { } generation)
            start.Environment["CLUSTER_START_GENERATION"] = generation.ToString("D");
        start.Environment["CLUSTER_START_RECOVERY"] = !spec.Recover ? "false" : spec.RecoveryUpdate ? "update" : "true";
        ExecutionBundle.ApplySecrets(start, spawn.SecretEnvironment);
        // Only the authenticated Host spec owns these values; bundle environment cannot override them.
        start.Environment.Remove("QUASAR_PLAYER_WHITELIST");
        start.Environment.Remove("QUASAR_PLAYER_ACCESS_REVISION");
        if (spec.PlayerAccess is { } intent)
        {
            start.Environment["QUASAR_PLAYER_WHITELIST"] = intent.Enabled && intent.Whitelist ? "true" : "false";
            start.Environment["QUASAR_PLAYER_ACCESS_REVISION"] = intent.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
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
        HostContract.GatewayObservedState observed, int? processId, DateTimeOffset? launchedAt, string? failure,
        string[]? output = null) =>
        new(spec.ClusterId, spec.Goal, observed, spec.BundleManifestSha256, spec.ConfigRevision,
            spec.Ports, spec.RunRoot, processId, launchedAt, failure,
            observed == HostContract.GatewayObservedState.Missing ? spec.StopFence : null, spec.StartGeneration, output);

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
    string? ProcessIdentity = null,
    string[]? Output = null,
    string? DiagnosticDirectory = null);

internal sealed record GatewayRunRootProvenance(int SchemaVersion, string ClusterId, string HostId);

internal enum GatewayLaunchStatus { Launching, Running, Failed, Stopped }
