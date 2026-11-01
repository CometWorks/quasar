using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CometWorks.Diagnostics;
using Quasar.Host.Contract.V1;

namespace Quasar.Diagnostics;

/// <summary>Local capture is independent of sharing. Only a current Quasar lease permits sealing or transfer.</summary>
public sealed class DiagnosticCollector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const long MaxLocalBytes = 8L << 30;
    public const string HungNodeKind = "node_hung", ReadyTimeoutKind = "ready_timeout", HangEvidenceFile = "hang-evidence.txt";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _policySync = new();
    private readonly string _hostId;
    private HostDiagnosticPolicy? _policy;
    private long _minimumGeneration;
    private long? _lastLogCollection;
    private long _logGeneration;
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    private CancellationTokenSource _sharing = new();
    public string RootDirectory { get; }
    public string PolicyFilePath { get; }
    public string IncomingDirectory => Path.Combine(RootDirectory, "incoming");
    public DiagnosticCollector(string rootDirectory, string hostId, string? stateDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        string state = Path.GetFullPath(stateDirectory ?? rootDirectory);
        PolicyFilePath = Path.Combine(state, "policy.json");
        _hostId = hostId;
        try { PrivateDirectory(state); PrivateDirectory(RootDirectory); PrivateDirectory(IncomingDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"Diagnostic storage unavailable ({e.GetType().Name})."); }
        try { _policy = Read<HostDiagnosticPolicy>(PolicyFilePath); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        if (_policy is { } policy)
        {
            _minimumGeneration = policy.Generation;
            if (CurrentPolicy is not null) _sharing.CancelAfter(new[] { policy.ExpiresAtUtc, policy.KeyExpiresAtUtc }.Min() - DateTimeOffset.UtcNow);
            else _sharing.Cancel();
        }
    }

    public HostDiagnosticPolicy? CurrentPolicy
    {
        get { lock (_policySync) return _policy is { DiagnosticsGranted: true } p
            && !_sharing.IsCancellationRequested && p.Generation >= _minimumGeneration && p.ExpiresAtUtc > DateTimeOffset.UtcNow
            && p.KeyExpiresAtUtc > DateTimeOffset.UtcNow ? p : null; }
    }

    public void Revoke(long generation)
    {
        lock (_policySync)
        {
            _minimumGeneration = Math.Max(_minimumGeneration, generation);
            _sharing.Cancel();
            if (_policy is { } p)
            {
                _policy = p with { DiagnosticsGranted = false, DumpsGranted = false, Generation = _minimumGeneration };
                Write(PolicyFilePath, _policy);
            }
            PurgeIncoming();
        }
    }

    public Task ApplyPolicyAsync(HostDiagnosticPolicy policy, CancellationToken token)
    {
        ValidatePolicy(policy);
        // Policy synchronization never waits behind sealing or streaming a dump.
        lock (_policySync)
        {
            token.ThrowIfCancellationRequested();
            if (policy.Generation < _minimumGeneration
                || _policy is { } old && (policy.InstallationId != old.InstallationId || policy.Generation < old.Generation))
                throw new InvalidOperationException("Diagnostic policy identity or generation is stale.");
            if (_policy is not { } previous || policy.Generation != previous.Generation
                || !policy.DiagnosticsGranted || policy.DumpsGranted != previous.DumpsGranted) _sharing.Cancel();
            Write(PolicyFilePath, policy);
            _policy = policy;
            _minimumGeneration = Math.Max(_minimumGeneration, policy.Generation);
            if (_sharing.IsCancellationRequested) _sharing = new();
            if (!policy.DiagnosticsGranted) _sharing.Cancel();
            else _sharing.CancelAfter(new[] { policy.ExpiresAtUtc, policy.KeyExpiresAtUtc }.Min() - DateTimeOffset.UtcNow);
            foreach (string path in Directory.EnumerateFiles(IncomingDirectory, "*.json"))
            {
                ReportEnvelope? envelope = TryReadEnvelope(path);
                if (envelope is null || !Allowed(envelope)) DeleteReportFiles(path);
            }
        }
        return Task.CompletedTask;
    }

    public string? Configure(ProcessStartInfo start, Dictionary<string, string> context, bool native = false)
    {
        try { return ConfigureCore(start, context, native); }
        catch (Exception e) when (CaptureFailure(e))
        {
            start.Environment.Remove("ERROR_REPORTING_DIRECTORY");
            start.Environment.Remove("ERROR_REPORTING_CONTEXT");
            Console.Error.WriteLine($"Diagnostic capture unavailable ({e.GetType().Name}).");
            return null;
        }
    }
    private string ConfigureCore(ProcessStartInfo start, Dictionary<string, string> context, bool native)
    {
        // Command handlers may create an actualizer on demand; its launch sees the latest persisted lease.
        string runId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(RootDirectory, "runs", runId);
        PrivateDirectory(directory);
        context = new(context, StringComparer.Ordinal) { ["runId"] = runId, ["hostId"] = _hostId,
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() };
        if (_policy is { } p) context["installationId"] = p.InstallationId;
        string executable = start.FileName;
        if (!Path.IsPathFullyQualified(executable) && !File.Exists(executable))
        {
            executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .SelectMany(p => OperatingSystem.IsWindows() && Path.GetExtension(start.FileName).Length == 0
                    ? new[] { Path.Combine(p, start.FileName), Path.Combine(p, start.FileName + ".exe") }
                    : new[] { Path.Combine(p, start.FileName) }).FirstOrDefault(File.Exists) ?? start.FileName;
        }
        executable = Path.GetFullPath(executable);
        using (var file = File.OpenRead(executable)) context["executableSha256"] = Convert.ToHexString(SHA256.HashData(file));
        Write(Path.Combine(directory, "run.json"), new CaptureRun(runId, DateTimeOffset.UtcNow, null, null,
            executable, null, native, context, null, false, PolicyFilePath));
        start.Environment["ERROR_REPORTING_DIRECTORY"] = directory;
        start.Environment["ERROR_REPORTING_CONTEXT"] = JsonSerializer.Serialize(context, Json);
        start.Environment["ERROR_REPORTING_POLICY_FILE"] = PolicyFilePath;
        // Remove inherited per-process locations; another run must never write into this one's evidence.
        foreach (string key in new[] { "DOTNET_DbgEnableMiniDump", "DOTNET_DbgMiniDumpName", "DOTNET_DbgMiniDumpType",
            "DOTNET_EnableCrashReport", "COMPlus_DbgEnableMiniDump", "COMPlus_DbgMiniDumpName" }) start.Environment.Remove(key);
        if (CurrentPolicy is { DumpsGranted: true } dumpPolicy)
        {
            context["dumpsGrantedAtStart"] = "true";
            context["dumpConsentGeneration"] = dumpPolicy.Generation.ToString();
            Write(Path.Combine(directory, "run.json"), new CaptureRun(runId, DateTimeOffset.UtcNow, null, null,
                executable, null, native, context, null, false, PolicyFilePath));
            start.Environment["DOTNET_DbgEnableMiniDump"] = "1";
            start.Environment["DOTNET_DbgMiniDumpType"] = "4"; // Single-file runtimes require Full.
            start.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(directory, "dump-%p-%t.dmp");
            start.Environment["DOTNET_EnableCrashReport"] = "1";
            start.Environment["RUST_BACKTRACE"] = "1";
            NativeDumpCapture.EnableInheritedCoreLimit();
        }
        return directory;
    }

    public void Started(string? directory, Process process, string? kernelIdentity = null)
    {
        if (directory is null) return;
        try
        {
            StartedCore(directory, process, kernelIdentity ?? CaptureIdentity(process.Id));
            lock (_policySync)
                if (CurrentPolicy is { DumpsGranted: true } policy
                    && Read<CaptureRun>(Path.Combine(directory, "run.json")) is { } run && DumpAllowed(run, policy))
                    WindowsCrashMonitor.Start(directory, process.Id, _sharing.Token);
        }
        catch (Exception e) when (CaptureFailure(e)) { Console.Error.WriteLine($"Diagnostic identity unavailable ({e.GetType().Name})."); }
    }
    private static void StartedCore(string directory, Process process, string? kernelIdentity)
    {
        CaptureRun run = Read<CaptureRun>(Path.Combine(directory, "run.json")) ?? throw new InvalidDataException("Missing diagnostic run.");
        string executable = OperatingSystem.IsLinux()
            ? File.ResolveLinkTarget($"/proc/{process.Id}/exe", true)?.FullName ?? run.Executable : run.Executable;
        Write(Path.Combine(directory, "run.json"), run with { ProcessId = process.Id,
            ProcessStartedAtUtc = process.StartTime.ToUniversalTime(), KernelIdentity = kernelIdentity,
            Executable = executable });
    }

    public static void Exited(string directory, int? exitCode, bool expected)
    {
        try { ExitedCore(directory, exitCode, expected); }
        catch (Exception e) when (CaptureFailure(e)) { Console.Error.WriteLine($"Diagnostic exit capture deferred ({e.GetType().Name})."); }
    }
    private static void ExitedCore(string directory, int? exitCode, bool expected)
    {
        string path = Path.Combine(directory, "run.json");
        CaptureRun? run = Read<CaptureRun>(path);
        if (run is null) return;
        expected |= run.ExpectedExit;
        if (run.EndedAtUtc is null) Write(path, run with { EndedAtUtc = DateTimeOffset.UtcNow, ExpectedExit = expected });
        if (!expected && !File.Exists(Path.Combine(directory, "exit-recorded")))
        {
            Write(Path.Combine(directory, "exit.json"), new IncidentReport(1, Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow, "host", "unexpected_exit", "Error",
                exitCode.HasValue ? $"Process exited unexpectedly with code {exitCode}." : "Process disappeared; exit status is unavailable.",
                null, new(run.Context) { ["exitCode"] = exitCode?.ToString() ?? "unknown" }));
            File.WriteAllText(Path.Combine(directory, "exit-recorded"), "");
        }
    }

    public static void ExpectExit(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "run.json");
            if (Read<CaptureRun>(path) is { } run) Write(path, run with { ExpectedExit = true });
        }
        catch (Exception e) when (CaptureFailure(e)) { Console.Error.WriteLine($"Diagnostic stop capture deferred ({e.GetType().Name})."); }
    }
    public static void ReportFailure(string? directory, string kind, string message, Exception? exception = null, string? details = null)
    {
        if (directory is null) return;
        try
        {
            CaptureRun? run = Read<CaptureRun>(Path.Combine(directory, "run.json"));
            if (run is null || Directory.EnumerateFiles(directory, "*.json").Where(p => DiagnosticsProtocol.IsReportId(Path.GetFileNameWithoutExtension(p))).Take(256).Count() >= 256) return;
            var report = new IncidentReport(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "host", kind, "Error",
                message[..Math.Min(message.Length, 8192)], (details ?? exception?.ToString()) is { } error ? error[..Math.Min(error.Length, 32768)] : null, new(run.Context));
            Write(Path.Combine(directory, report.ReportId + ".json"), report);
        }
        catch (Exception e) when (CaptureFailure(e)) { }
    }
    private static bool CaptureFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or InvalidOperationException or ArgumentException or CryptographicException or System.ComponentModel.Win32Exception;

    internal static void ReportNativeException(string directory, uint code, uint threadId, ulong address)
    {
        try
        {
            string path = Path.Combine(directory, "run.json");
            if (Read<CaptureRun>(path) is not { } run) return;
            run.Context["nativeExceptionCode"] = $"0x{code:x8}";
            run.Context["nativeThreadId"] = threadId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            run.Context["nativeExceptionAddress"] = $"0x{address:x16}";
            Write(path, run);
            ReportFailure(directory, "native_exception", $"Unhandled Windows exception 0x{code:x8} on thread {threadId}.");
        }
        catch (Exception e) when (CaptureFailure(e)) { }
    }

    /// <summary>What the process itself reported as its fatal exception in this run, for a person to read: the innermost
    /// exception of the chain and the frame that threw it. Null when it reported none.</summary>
    public static string? ReportedException(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return null;
        var reports = new List<IncidentReport>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Where(p => DiagnosticsProtocol.IsReportId(Path.GetFileNameWithoutExtension(p))).Take(256))
        {
            try { if (Read<IncidentReport>(path) is { Source: not "host", Exception.Length: > 0 } report) reports.Add(report); }
            catch (Exception e) when (CaptureFailure(e)) { }
        }
        // A run may also have reported errors it survived. The last words are the unhandled exception, if there is one.
        var fatal = reports.OrderByDescending(r => r.Kind == "unhandled_exception").ThenByDescending(r => r.CapturedAtUtc).FirstOrDefault();
        return fatal is null ? null : Summarize(fatal.Exception!);
    }

    internal static string Summarize(string exception)
    {
        string[] lines = exception.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // ToString() lists the chain outermost first, each inner one as " ---> Type: message", then the innermost's stack.
        int cause = Array.FindLastIndex(lines, line => line.StartsWith("---> ", StringComparison.Ordinal));
        string summary = cause < 0 ? lines[0] : lines[cause][5..];
        if (Math.Max(cause, 0) + 1 < lines.Length && lines[Math.Max(cause, 0) + 1].StartsWith("at ", StringComparison.Ordinal))
            summary += " " + lines[Math.Max(cause, 0) + 1];
        // The outermost can name what the cause can't, such as which plugin's Preloader type threw.
        if (cause > 0)
            summary = lines[0] + " caused by " + summary;
        return summary.Length > 500 ? summary[..500] + "…" : summary;
    }

    public static void RecordOutput(string? directory, string line)
    {
        if (directory is null) return;
        try
        {
            LogBatchCapture.Append(directory, PolicyPath(directory), "stdout", "Information", line);
            string path = Path.Combine(directory, "process.log");
            lock (Json)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 8 << 20) File.Move(path, path + ".1", true);
                File.AppendAllText(path, line[..Math.Min(line.Length, 16_384)] + Environment.NewLine);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception e) when (CaptureFailure(e)) { }
    }

    private static string PolicyPath(string directory) => Read<CaptureRun>(Path.Combine(directory, "run.json"))?.PolicyFilePath
        ?? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, "policy.json");

    public async Task CollectAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (string temp in Directory.EnumerateFiles(IncomingDirectory, "*.tmp")) File.Delete(temp);
            string runs = Path.Combine(RootDirectory, "runs");
            HostDiagnosticPolicy? policy = CurrentPolicy;
            if (Directory.Exists(runs))
            {
                foreach (string directory in Directory.EnumerateDirectories(runs))
                    try { if (Read<CaptureRun>(Path.Combine(directory, "run.json")) is { } run) RecoverExit(directory, run, policy?.GrantedSinceUtc ?? DateTimeOffset.MaxValue); }
                    catch (Exception e) when (CaptureFailure(e)) { }
                TrimRuns(runs);
            }
            if (policy is null) { PurgeIncoming(); return; }
            foreach (string path in Directory.EnumerateFiles(IncomingDirectory))
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)
                    || path.EndsWith(".zip", StringComparison.Ordinal) && !File.Exists(Path.ChangeExtension(path, ".json"))) File.Delete(path);
            using var work = CancellationTokenSource.CreateLinkedTokenSource(token, SharingToken);
            if (!Directory.Exists(runs)) return;
            // Deliver the first consented batch promptly, then group ordinary logs every ten minutes.
            // Incidents and consent lease renewal keep their existing cadence.
            bool collectLogs = _logGeneration != policy.Generation || _lastLogCollection is not { } last
                || Clock.GetElapsedTime(last) >= TimeSpan.FromMinutes(10);
            bool sealedLogs = false;
            // ponytail: fixed local quota and sequential packaging; avoids concurrent multi-GB dump copies.
            foreach (string directory in Directory.EnumerateDirectories(runs))
            {
                work.Token.ThrowIfCancellationRequested();
                try
                {
                    CaptureRun? run = Read<CaptureRun>(Path.Combine(directory, "run.json"));
                    if (run is null) continue;
                    LogBatchCapture.Flush(directory, PolicyFilePath);
                    RecoverExit(directory, run, policy.GrantedSinceUtc);
                    run = Read<CaptureRun>(Path.Combine(directory, "run.json"))!;
                    if (run.EndedAtUtc is not null && run.Native && DumpAllowed(run, policy))
                        await NativeDumpCapture.ImportAsync(directory, run.ProcessId, run.Executable,
                            run.ProcessStartedAtUtc ?? run.CreatedAtUtc, run.EndedAtUtc.Value, run.KernelIdentity, work.Token);
                    foreach (string path in Directory.EnumerateFiles(directory, "*.json")
                        .OrderBy(path => Path.GetFileName(path).StartsWith("log-", StringComparison.Ordinal)))
                    {
                        if (Path.GetFileName(path) is "run.json" || path.EndsWith(".crashreport.json", StringComparison.Ordinal)) continue;
                        if (new FileInfo(path).Length > 1 << 20) continue;
                        IncidentReport? report;
                        try { report = Read<IncidentReport>(path); if (report is not null) DiagnosticsProtocol.ValidateReport(report); }
                        catch (Exception e) when (e is JsonException or InvalidDataException) { continue; }
                        if (report is null || report.CapturedAtUtc < policy.GrantedSinceUtc) continue;
                        if (report.Kind == "log_batch" && !collectLogs) continue;
                        if (report.Kind == "log_batch" && report.Context.GetValueOrDefault("captureConsentGeneration") != policy.Generation.ToString()) continue;
                        string marker = Path.Combine(directory, report.ReportId + ".sealed");
                        if (File.Exists(marker)) continue;
                        // Never let child-supplied identities replace the Host's trusted launch context.
                        var fields = new[] { "transferId", "partitionId", "phase", "sourceNode", "targetNode", "reasonCode", "eventCode", "exitCode",
                            "captureConsentGeneration", "batchStartedAtUtc", "batchEndedAtUtc", "lineCount" };
                        var context = report.Context.Where(p => fields.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
                        foreach (var pair in run.Context) context[pair.Key] = pair.Value;
                        context["installationId"] = policy.InstallationId;
                        report = report with { Context = context };
                        var attachments = new Dictionary<string, string>();
                        string log = Path.Combine(directory, "process.log");
                        // A log spanning a previous consent window cannot be shared retrospectively.
                        if (report.Kind != "log_batch" && File.Exists(log) && run.CreatedAtUtc >= policy.GrantedSinceUtc) attachments["log-001.txt"] = Snapshot(log, directory);
                        // A hang's dump is written before its report, while the process still runs.
                        bool hang = report.Kind is HungNodeKind or ReadyTimeoutKind;
                        if (hang && File.Exists(Path.Combine(directory, HangEvidenceFile))) attachments["log-002.txt"] = Path.Combine(directory, HangEvidenceFile);
                        if (DumpAllowed(run, policy) && (hang || run.EndedAtUtc is not null && Path.GetFileName(path) == "exit.json"))
                        {
                            if (run.Native && NativeDumpCapture.Capability == "systemd-coredump"
                                && !File.Exists(Path.Combine(directory, "native.dmp"))
                                && !File.Exists(Path.Combine(directory, "core-unavailable"))) continue;
                            int n = 0;
                            foreach (string dump in Directory.EnumerateFiles(directory, "*.dmp").Take(2))
                                if (new FileInfo(dump).Length is > 0 and < (2L << 30)) attachments[$"dump-{++n:000}.dmp"] = dump;
                            if (n == 0 && !run.Native) report.Context["dumpStatus"] = "Runtime dump unavailable or larger than the supported limit.";
                        }
                        report.Context["dumpCapture"] = run.Native || OperatingSystem.IsWindows() ? NativeDumpCapture.Capability : "coreclr";
                        if (File.Exists(Path.Combine(directory, "core-unavailable"))) report.Context["dumpStatus"] = File.ReadAllText(Path.Combine(directory, "core-unavailable"));
                        long estimate = attachments.Values.Sum(p => new FileInfo(p).Length) + (1 << 20);
                        if (estimate > DiagnosticsProtocol.MaxArchiveBytes)
                        {
                            foreach (string dump in attachments.Keys.Where(p => p.EndsWith(".dmp", StringComparison.Ordinal)).ToArray()) attachments.Remove(dump);
                            report.Context["dumpStatus"] = "Dump exceeds the archive size limit.";
                            estimate = attachments.Values.Sum(p => new FileInfo(p).Length) + (1 << 20);
                        }
                        int queued = Directory.EnumerateFiles(IncomingDirectory, "*.json").Take(256).Count();
                        if (report.Kind == "log_batch" && queued >= 192) continue; // Reserve queue space for incidents.
                        if (queued >= 256
                            || Directory.EnumerateFiles(IncomingDirectory).Sum(p => new FileInfo(p).Length) + estimate > MaxLocalBytes
                            || DiagnosticStorage.AvailableFreeSpace(RootDirectory) < estimate + (1L << 30)) return;
                        if (File.Exists(Path.Combine(IncomingDirectory, report.ReportId + ".zip"))) DeleteReport(report.ReportId);
                        await IncidentArchive.SealAsync(IncomingDirectory, report, attachments,
                            new BackofficeKey(policy.KeyId, policy.PublicKeyPem, policy.KeyExpiresAtUtc), policy.Generation, work.Token);
                        if (CurrentPolicy?.Generation != policy.Generation) { DeleteReport(report.ReportId); continue; }
                        File.WriteAllText(marker, "");
                        if (report.Kind == "log_batch") sealedLogs = true;
                    }
                }
                catch (Exception e) when (CaptureFailure(e))
                { Console.Error.WriteLine($"Diagnostic collection deferred ({e.GetType().Name})."); }
            }
            if (sealedLogs && CurrentPolicy?.Generation == policy.Generation)
            {
                _lastLogCollection = Clock.GetTimestamp();
                _logGeneration = policy.Generation;
            }
        }
        finally { _gate.Release(); }
    }

    public ReportEnvelope[] GetReports() => !Directory.Exists(IncomingDirectory) ? [] : Directory.EnumerateFiles(IncomingDirectory, "*.json")
        .Select(TryReadEnvelope).OfType<ReportEnvelope>().Where(Allowed).Take(100).ToArray();
    public bool Allowed(ReportEnvelope envelope) => CurrentPolicy is { } p && envelope.ConsentGeneration == p.Generation
        && (!envelope.ContainsDump || p.DumpsGranted);
    public FileStream OpenReport(string id)
    {
        RequireId(id);
        ReportEnvelope? envelope = TryReadEnvelope(Path.Combine(IncomingDirectory, id + ".json"));
        if (envelope is null || !Allowed(envelope)) throw new InvalidOperationException("Report sharing is not authorized.");
        return new FileStream(Path.Combine(IncomingDirectory, id + ".zip"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }
    public CancellationToken SharingToken { get { lock (_policySync) return _sharing.Token; } }
    public void DeleteReport(string id)
    {
        RequireId(id);
        File.Delete(Path.Combine(IncomingDirectory, id + ".json"));
        File.Delete(Path.Combine(IncomingDirectory, id + ".zip"));
    }

    private static string Snapshot(string log, string directory)
    {
        string target = Path.Combine(directory, "log-snapshot.txt");
        using var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return target;
    }
    private static void RecoverExit(string directory, CaptureRun run, DateTimeOffset consentSince)
    {
        if (run.ProcessId is not int pid || run.EndedAtUtc is not null) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited && (run.KernelIdentity is { } identity && CaptureIdentity(pid) is { } actual
                ? identity == actual : run.ProcessStartedAtUtc is { } started && Math.Abs((p.StartTime.ToUniversalTime() - started).TotalSeconds) < 1)) return;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        bool rebooted = run.KernelIdentity is { } recorded && OperatingSystem.IsLinux()
            && !recorded.StartsWith(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() + "/", StringComparison.Ordinal);
        // Unknown-time deaths recovered after a new opt-in may predate consent.
        Exited(directory, null, run.ExpectedExit || rebooted || run.CreatedAtUtc < consentSince);
    }
    public static string? CaptureIdentity(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() + "/" + fields[19];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IndexOutOfRangeException) { return null; }
    }
    private static ReportEnvelope? TryReadEnvelope(string path)
    {
        try
        {
            var envelope = Read<ReportEnvelope>(path);
            if (envelope is null) return null;
            DiagnosticsProtocol.ValidateEnvelope(envelope);
            var archive = new FileInfo(Path.ChangeExtension(path, ".zip"));
            return envelope.ReportId == Path.GetFileNameWithoutExtension(path) && archive.Exists && archive.Length == envelope.ArchiveBytes
                ? envelope : null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
    private void PurgeIncoming() { foreach (string path in Directory.EnumerateFiles(IncomingDirectory)) File.Delete(path); }
    private void DeleteReportFiles(string envelopePath)
    {
        File.Delete(envelopePath);
        File.Delete(Path.ChangeExtension(envelopePath, ".zip"));
    }
    public bool DumpAllowed(string directory)
    {
        try { return CurrentPolicy is { } policy && Read<CaptureRun>(Path.Combine(directory, "run.json")) is { } run && DumpAllowed(run, policy); }
        catch (Exception e) when (CaptureFailure(e)) { return false; }
    }
    private static bool DumpAllowed(CaptureRun run, HostDiagnosticPolicy policy) => policy.DumpsGranted
        && run.Context.GetValueOrDefault("dumpsGrantedAtStart") == "true"
        && run.Context.GetValueOrDefault("dumpConsentGeneration") == policy.Generation.ToString();
    private static void TrimRuns(string runs)
    {
        var directories = Directory.GetDirectories(runs).Select(p => new DirectoryInfo(p)).OrderBy(p => p.CreationTimeUtc).ToArray();
        long bytes = directories.Sum(p => p.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
        foreach (var directory in directories)
        {
            CaptureRun? run;
            try { run = Read<CaptureRun>(Path.Combine(directory.FullName, "run.json")); }
            catch (Exception e) when (e is JsonException or IOException or InvalidDataException) { run = null; }
            if (run is null || run.ProcessId is null && run.EndedAtUtc is null)
            {
                if (DateTime.UtcNow - directory.LastWriteTimeUtc > TimeSpan.FromDays(7))
                {
                    bytes -= directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    directory.Delete(true);
                }
                continue;
            }
            if (run.EndedAtUtc is null) continue;
            if (bytes <= MaxLocalBytes && DateTimeOffset.UtcNow - run.EndedAtUtc < TimeSpan.FromDays(7)) continue;
            bytes -= directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            directory.Delete(true);
        }
    }
    public static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void ValidatePolicy(HostDiagnosticPolicy p)
    {
        if (!DiagnosticsProtocol.IsReportId(p.InstallationId) || p.Generation < 0 || p.DiagnosticsGranted && p.Generation == 0 || p.DumpsGranted && !p.DiagnosticsGranted
            || p.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || p.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) || p.GrantedSinceUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("Invalid diagnostic policy.");
        if (p.DiagnosticsGranted)
        {
            using var rsa = RSA.Create(); rsa.ImportFromPem(p.PublicKeyPem);
            if (rsa.KeySize is < 3072 or > 8192 || p.KeyExpiresAtUtc <= DateTimeOffset.UtcNow || p.KeyId != DiagnosticsProtocol.KeyId(rsa))
                throw new InvalidDataException("Invalid diagnostic encryption key.");
        }
    }
    private static void RequireId(string id) { if (!DiagnosticsProtocol.IsReportId(id)) throw new InvalidDataException("Invalid report ID."); }
    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > DiagnosticsProtocol.MaxJsonBytes) throw new InvalidDataException("Diagnostic metadata exceeds limit.");
        var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
        if (value is CaptureRun run && (run.Context is null || run.Context.Count > 32 || run.Executable is null))
            throw new InvalidDataException("Invalid diagnostic run.");
        return value;
    }
    private static void Write<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { using (var file = new FileStream(temp, options)) { file.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json)); file.Flush(true); } File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
    private sealed record CaptureRun(string RunId, DateTimeOffset CreatedAtUtc, int? ProcessId,
        DateTimeOffset? ProcessStartedAtUtc, string Executable, string? KernelIdentity, bool Native,
        Dictionary<string, string> Context, DateTimeOffset? EndedAtUtc, bool ExpectedExit, string? PolicyFilePath = null);
}
