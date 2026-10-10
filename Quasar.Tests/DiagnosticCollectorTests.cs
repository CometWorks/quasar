using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CometWorks.Diagnostics;
using Quasar.Diagnostics;
using Quasar.Host.Contract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class DiagnosticCollectorTests : IDisposable
{
    [Fact]
    public void WindowsMinidumpInteropMatchesPackedX64WindowsSdkAbi()
    {
        if (IntPtr.Size != 8) return;
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<WindowsCrashMonitor.ExceptionInformation>());
        Assert.Equal(4, System.Runtime.InteropServices.Marshal.OffsetOf<WindowsCrashMonitor.ExceptionInformation>("Pointers").ToInt32());
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.OffsetOf<WindowsCrashMonitor.ExceptionInformation>("ClientPointers").ToInt32());
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<WindowsCrashMonitor.CallbackInputHeader>());
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.OffsetOf<WindowsCrashMonitor.CallbackInputHeader>("CallbackType").ToInt32());
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "collector-test-" + Guid.NewGuid().ToString("N"));
    private readonly RSA rsa = RSA.Create(3072);
    private HostDiagnosticPolicy Policy(bool dumps = false) => new(Guid.NewGuid().ToString("N"), true, dumps, 1,
        DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(2), DiagnosticsProtocol.KeyId(rsa),
        rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
    private static string Configure(DiagnosticCollector collector) => collector.Configure(new ProcessStartInfo(Environment.ProcessPath!),
        new() { ["role"] = "test", ["nodeId"] = "trusted-node" })!;
    private static IncidentReport Report() => new(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "test-plugin", "log", "Error",
        "Failed handover", null, new() { ["nodeId"] = "child-forgery", ["partitionId"] = "partition-a", ["phase"] = "restore" });
    private static void Mutate(string directory, Action<JsonObject> mutate)
    {
        string path = Path.Combine(directory, "run.json");
        var run = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); mutate(run); File.WriteAllText(path, run.ToJsonString());
    }

    [Fact]
    public async Task SeparateEvidenceStoragePreservesPolicyAndRevocationAcrossMoves()
    {
        string state = Path.Combine(root, "state"), storage = Path.Combine(root, "large disk", "logs");
        var collector = DiagnosticStorage.Create(state, "host-a", storage);
        var policy = Policy();
        await collector.ApplyPolicyAsync(policy, default);
        Assert.Equal(Path.Combine(state, "policy.json"), collector.PolicyFilePath);
        Assert.False(File.Exists(Path.Combine(storage, "policy.json")));
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        string run = collector.Configure(start, new() { ["role"] = "test" })!;
        Assert.Equal(collector.PolicyFilePath, start.Environment["ERROR_REPORTING_POLICY_FILE"]);
        Assert.Equal(Path.Combine(storage, "runs"), Path.GetDirectoryName(run));
        for (int i = 0; i < 64; i++) DiagnosticCollector.RecordOutput(run, "ordinary output " + i);
        LogBatchCapture.Flush(run, collector.PolicyFilePath, force: true);
        await collector.CollectAsync(default);
        var envelope = Assert.Single(collector.GetReports());
        Assert.True(File.Exists(Path.Combine(storage, "incoming", envelope.ReportId + ".zip")));
        var report = await IncidentArchive.ValidateAsync(Path.Combine(collector.IncomingDirectory, envelope.ReportId + ".zip"), envelope, rsa);
        Assert.Equal("log_batch", report.Kind);
        collector.Revoke(2);
        var moved = DiagnosticStorage.Create(state, "host-a", Path.Combine(root, "another disk"));
        Assert.Null(moved.CurrentPolicy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => moved.ApplyPolicyAsync(policy, default));
    }

    [Fact]
    public void DiagnosticStorageDefaultsRemainCompatibleAndRejectRelativeOverrides()
    {
        Assert.Equal(Path.GetFullPath(root), DiagnosticStorage.Resolve(null, root));
        Assert.Equal(Path.GetFullPath(root), DiagnosticStorage.Resolve(" ", root));
        Assert.Throws<ArgumentException>(() => DiagnosticStorage.Resolve("relative/logs", root));
        Assert.Throws<ArgumentException>(() => DiagnosticStorage.Resolve(Path.GetPathRoot(root), root));
    }

    [Fact]
    public void FreeSpaceUsesTheEvidenceMountInsteadOfTheFilesystemRoot()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) return;
        var mounted = DriveInfo.GetDrives().Single(d => d.Name.TrimEnd('/') == "/dev/shm");
        long before = mounted.AvailableFreeSpace;
        long actual = DiagnosticStorage.AvailableFreeSpace("/dev/shm/diagnostics-evidence");
        long after = mounted.AvailableFreeSpace;
        Assert.InRange(actual, Math.Min(before, after), Math.Max(before, after));
    }

    [Fact]
    public async Task SealsCorrelatedReportAndRestartDoesNotResendAcknowledgedRun()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        await collector.ApplyPolicyAsync(Policy(), default);
        string directory = Configure(collector);
        var report = Report();
        File.WriteAllText(Path.Combine(directory, "bad.json"), "{invalid");
        File.WriteAllText(Path.Combine(directory, report.ReportId + ".json"), JsonSerializer.Serialize(report));
        File.WriteAllText(Path.Combine(collector.IncomingDirectory, "broken.json"), "{invalid");
        DiagnosticCollector.RecordOutput(directory, "recent output");
        await collector.CollectAsync(default);
        // Invalid inventory entries must not hide the valid archive or be opened by a guessed ID.
        string mismatchedId = Guid.NewGuid().ToString("N");
        string missingId = Guid.NewGuid().ToString("N");
        string truncatedId = Guid.NewGuid().ToString("N");
        var valid = Assert.Single(collector.GetReports());
        File.WriteAllText(Path.Combine(collector.IncomingDirectory, mismatchedId + ".json"), JsonSerializer.Serialize(valid));
        File.Copy(Path.Combine(collector.IncomingDirectory, valid.ReportId + ".zip"), Path.Combine(collector.IncomingDirectory, mismatchedId + ".zip"));
        File.WriteAllText(Path.Combine(collector.IncomingDirectory, missingId + ".json"), JsonSerializer.Serialize(valid with { ReportId = missingId }));
        File.WriteAllText(Path.Combine(collector.IncomingDirectory, truncatedId + ".json"), JsonSerializer.Serialize(valid with { ReportId = truncatedId }));
        File.WriteAllBytes(Path.Combine(collector.IncomingDirectory, truncatedId + ".zip"), [1]);
        Assert.Throws<InvalidOperationException>(() => collector.OpenReport(mismatchedId));
        var envelope = Assert.Single(collector.GetReports());
        var received = await IncidentArchive.ValidateAsync(Path.Combine(collector.IncomingDirectory, envelope.ReportId + ".zip"), envelope, rsa);
        Assert.Equal("trusted-node", received.Context["nodeId"]);
        Assert.Equal("partition-a", received.Context["partitionId"]);
        Assert.Equal("restore", received.Context["phase"]);
        Assert.Equal("host-a", received.Context["hostId"]);
        Assert.False(envelope.ContainsDump);
        collector.DeleteReport(envelope.ReportId);
        var reloaded = new DiagnosticCollector(root, "host-a");
        await reloaded.CollectAsync(default);
        Assert.Empty(reloaded.GetReports());
    }

    [Fact]
    public async Task RevocationPersistsCancelsOpenSharingAndRejectsStaleGeneration()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        var policy = Policy(); await collector.ApplyPolicyAsync(policy, default);
        var sharing = collector.SharingToken;
        collector.Revoke(2);
        Assert.True(sharing.IsCancellationRequested);
        Assert.Null(new DiagnosticCollector(root, "host-a").CurrentPolicy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.ApplyPolicyAsync(policy, default));
        using var stale = new CancellationTokenSource(); stale.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.ApplyPolicyAsync(policy with { Generation = 2 }, stale.Token));
        Assert.Null(collector.CurrentPolicy);
    }

    [Fact]
    public async Task RenewedLeaseAndExpiredLeaseCancelInFlightSharing()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        var policy = Policy() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(1) };
        await collector.ApplyPolicyAsync(policy, default);
        var sharing = collector.SharingToken;
        await collector.ApplyPolicyAsync(policy with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(150) }, default);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = sharing.Register(() => canceled.TrySetResult());
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(collector.CurrentPolicy);
    }

    [Fact]
    public async Task NoConsentStillTrimsFinishedRunsAndNeverUploadsHistoricalRecovery()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        string old = Configure(collector);
        DiagnosticCollector.Exited(old, 3, false);
        Mutate(old, run => run["endedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-8));
        await collector.CollectAsync(default);
        Assert.False(Directory.Exists(old));
        string historical = Configure(collector);
        Mutate(historical, run =>
        {
            run["createdAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1);
            run["processId"] = int.MaxValue;
            run["processStartedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1);
        });
        await collector.ApplyPolicyAsync(Policy(), default);
        await collector.CollectAsync(default);
        Assert.False(File.Exists(Path.Combine(historical, "exit.json")));
        Assert.Empty(collector.GetReports());
    }

    [Fact]
    public async Task CorruptRunMetadataUsesAgeCleanupWithoutBlockingOtherRuns()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        string fresh = Configure(collector);
        File.WriteAllText(Path.Combine(fresh, "run.json"), "{invalid");
        foreach (string metadata in new[] { "{invalid", "{\"executable\":\"fixture\",\"context\":null}", new string('x', DiagnosticsProtocol.MaxJsonBytes + 1) })
        {
            string stale = Configure(collector);
            File.WriteAllText(Path.Combine(stale, "run.json"), metadata);
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
        }
        string finished = Configure(collector);
        DiagnosticCollector.Exited(finished, 3, false);
        Mutate(finished, run => run["endedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-8));
        await collector.CollectAsync(default);
        Assert.Equal(fresh, Assert.Single(Directory.GetDirectories(Path.Combine(root, "runs"))));
    }

    [Fact]
    public async Task ContinuousStdoutOnlySharesLinesCapturedUnderTheCurrentGrant()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        string directory = Configure(collector);
        DiagnosticCollector.RecordOutput(directory, "before-consent");
        var policy = Policy(); await collector.ApplyPolicyAsync(policy, default);
        DiagnosticCollector.RecordOutput(directory, "after-consent");
        LogBatchCapture.Flush(directory, Path.Combine(root, "policy.json"), force: true);
        await collector.CollectAsync(default);
        var envelope = Assert.Single(collector.GetReports());
        var report = await IncidentArchive.ValidateAsync(Path.Combine(collector.IncomingDirectory, envelope.ReportId + ".zip"), envelope, rsa);
        Assert.Equal("log_batch", report.Kind);
        Assert.Contains("after-consent", report.Message);
        Assert.DoesNotContain("before-consent", report.Message);
        Assert.Equal(policy.Generation.ToString(), report.Context["captureConsentGeneration"]);
        Assert.Equal("host-a", report.Context["hostId"]);
        Assert.False(envelope.ContainsDump);
        collector.Revoke(2);
        DiagnosticCollector.RecordOutput(directory, "after-withdrawal");
        LogBatchCapture.Flush(directory, Path.Combine(root, "policy.json"), force: true);
        Assert.Empty(collector.GetReports());
        Assert.False(File.Exists(Path.Combine(directory, "log-batch.pending")));
    }

    [Fact]
    public async Task NativeExceptionAttributionIsPreservedOnTheDumpBearingExitReport()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        await collector.ApplyPolicyAsync(Policy(true), default);
        string directory = Configure(collector);
        DiagnosticCollector.ReportNativeException(directory, 0xc0000005, 42, 0xdeadbeef);
        File.WriteAllBytes(Path.Combine(directory, "native.dmp"), [1, 2, 3]);
        DiagnosticCollector.Exited(directory, 3, false);
        await collector.CollectAsync(default);
        var envelope = collector.GetReports().Single(e => e.ContainsDump);
        var report = await IncidentArchive.ValidateAsync(Path.Combine(collector.IncomingDirectory, envelope.ReportId + ".zip"), envelope, rsa);
        Assert.Equal("unexpected_exit", report.Kind);
        Assert.Equal("0xc0000005", report.Context["nativeExceptionCode"]);
        Assert.Equal("42", report.Context["nativeThreadId"]);
        Assert.Equal("0x00000000deadbeef", report.Context["nativeExceptionAddress"]);
    }

    [Fact]
    public async Task KernelIdentitySurvivesWallClockChangeAndAdministrativeExitIsExpected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var collector = new DiagnosticCollector(root, "host-a");
        await collector.ApplyPolicyAsync(Policy(), default);
        string directory = Configure(collector);
        using var process = Process.GetCurrentProcess(); collector.Started(directory, process);
        Mutate(directory, run => run["processStartedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1));
        await collector.CollectAsync(default);
        Assert.False(File.Exists(Path.Combine(directory, "exit.json")));
        DiagnosticCollector.ReportFailure(directory, "ready_timeout", "Fixture readiness failed.");
        Assert.Single(Directory.GetFiles(directory, "*.json").Where(p => Path.GetFileName(p) != "run.json"));
        DiagnosticCollector.ExpectExit(directory);
        DiagnosticCollector.Exited(directory, null, false);
        Assert.False(File.Exists(Path.Combine(directory, "exit.json")));
    }

    [Fact]
    public async Task DumpFromOldConsentGenerationIsNeverIncluded()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        var policy = Policy(true); await collector.ApplyPolicyAsync(policy, default);
        string directory = Configure(collector);
        File.WriteAllBytes(Path.Combine(directory, "fixture.dmp"), [1, 2, 3]);
        await collector.ApplyPolicyAsync(policy with { Generation = 2 }, default);
        DiagnosticCollector.Exited(directory, 3, false);
        await collector.CollectAsync(default);
        Assert.False(Assert.Single(collector.GetReports()).ContainsDump);
    }

    [Fact]
    public void CaptureIoFailureDoesNotBlockLaunchOrExit()
    {
        var collector = new DiagnosticCollector(root, "host-a");
        File.WriteAllText(Path.Combine(root, "runs"), "cannot create run directories");
        Assert.Null(collector.Configure(new ProcessStartInfo(Environment.ProcessPath!), new()));
        collector.Started(Path.Combine(root, "missing"), Process.GetCurrentProcess());
        DiagnosticCollector.ExpectExit(Path.Combine(root, "missing"));
        DiagnosticCollector.Exited(Path.Combine(root, "missing"), 3, false);
    }
    public void Dispose() { rsa.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
