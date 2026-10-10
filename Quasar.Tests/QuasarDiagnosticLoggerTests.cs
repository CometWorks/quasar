using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using Quasar.Host.Contract.V1;
using CometWorks.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Diagnostics;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarDiagnosticLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-self-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoggingCapturesWithoutConsentAndIgnoresScopesAndRecursiveFormatting()
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false);
        var logger = provider.CreateLogger("Quasar.Test");
        logger.LogWarning("Not an incident");
        using (logger.BeginScope(new Dictionary<string, string> { ["privateSetting"] = "must-not-copy" }))
            logger.Log(LogLevel.Error, new EventId(7, "Failure"), "state", new InvalidOperationException("broken"), (state, error) =>
            {
                logger.LogError("Recursive formatter log");
                return new string('x', 10000);
            });
        var report = Assert.Single(Reports(provider));
        DiagnosticsProtocol.ValidateReport(report);
        Assert.Equal(8192, report.Message.Length);
        Assert.Equal("Quasar.Test", report.Source);
        Assert.Equal("7", report.Context["eventId"]);
        Assert.DoesNotContain("privateSetting", report.Context.Keys);
        Assert.Contains("broken", report.Exception);
        Assert.Null(collector.CurrentPolicy);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(provider.DiagnosticDirectory!, "run.json")));
        Assert.Equal(Environment.ProcessId, run.RootElement.GetProperty("processId").GetInt32());
    }

    [Fact]
    public async Task OrdinaryLogsBatchOnlyWithinCurrentConsentGeneration()
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false);
        var logger = provider.CreateLogger("Quasar.Ordinary");
        logger.LogInformation("before opt in");
        Assert.False(File.Exists(Path.Combine(provider.DiagnosticDirectory!, "log-batch.pending")));
        using var rsa = RSA.Create(3072);
        var policy = new HostDiagnosticPolicy(Guid.NewGuid().ToString("N"), true, false, 1, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1), DiagnosticsProtocol.KeyId(rsa), rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1));
        await collector.ApplyPolicyAsync(policy, default);
        logger.LogInformation("ordinary one");
        logger.LogWarning("ordinary two");
        Assert.Empty(Reports(provider));
        LogBatchCapture.Flush(provider.DiagnosticDirectory!, Path.Combine(_root, "policy.json"));
        Assert.Empty(Directory.GetFiles(provider.DiagnosticDirectory!, "log-*.json"));
        LogBatchCapture.Flush(provider.DiagnosticDirectory!, Path.Combine(_root, "policy.json"), force: true);
        string batch = Assert.Single(Directory.GetFiles(provider.DiagnosticDirectory!, "log-*.json"));
        var report = JsonSerializer.Deserialize<IncidentReport>(File.ReadAllText(batch), DiagnosticsProtocol.Json)!;
        DiagnosticsProtocol.ValidateReport(report);
        Assert.Equal("log_batch", report.Kind);
        Assert.Equal("2", report.Context["lineCount"]);
        Assert.DoesNotContain("before opt in", report.Message);
        logger.LogInformation("withdraw this pending log");
        collector.Revoke(2);
        provider.Dispose();
        Assert.False(File.Exists(Path.Combine(provider.DiagnosticDirectory!, "log-batch.pending")));
        Assert.Single(Directory.GetFiles(provider.DiagnosticDirectory!, "log-*.json"));
    }

    [Fact]
    public void ErrorStormIsBoundedAndFatalEvidencePreservesRunMetadata()
    {
        var clock = new ManualClock();
        using var provider = new QuasarDiagnosticLoggerProvider(new DiagnosticCollector(_root, "test-host"), clock, subscribe: false);
        var logger = provider.CreateLogger("Quasar.Test");
        for (int minute = 0; minute < 9; minute++)
        {
            for (int count = 0; count < 40; count++) logger.LogError("failure {Count}", count);
            Assert.Equal(Math.Min((minute + 1) * 32, 256), Reports(provider).Length);
            clock.Advance();
        }
        provider.MarkExpectedStop();
        provider.Dispose(); // The top-level catch can execute after Host disposal.
        provider.CaptureFatal(new Exception("terminal"));
        var reports = Reports(provider);
        Assert.Equal(256, reports.Length);
        Assert.Single(reports, report => report.Kind == "unhandled_exception");
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(provider.DiagnosticDirectory!, "run.json")));
        Assert.True(run.RootElement.GetProperty("expectedExit").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, run.RootElement.GetProperty("endedAtUtc").ValueKind);
    }

    [Fact]
    public void ExpectedStopIsPersistedAndCaptureStorageFailureDoesNotEscapeLogger()
    {
        using var provider = new QuasarDiagnosticLoggerProvider(new DiagnosticCollector(_root, "test-host"), TimeProvider.System, subscribe: false);
        provider.MarkExpectedStop();
        using (var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(provider.DiagnosticDirectory!, "run.json"))))
            Assert.True(run.RootElement.GetProperty("expectedExit").GetBoolean());
        Directory.Delete(provider.DiagnosticDirectory!, true);
        File.WriteAllText(provider.DiagnosticDirectory!, "not a directory");
        Assert.Null(Record.Exception(() => provider.CreateLogger("Test").LogError("Storage is unavailable")));
    }

    [Fact]
    public void ProviderAdoptsMatchingBootstrapRunWithoutCreatingAnotherRun()
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var process = Process.GetCurrentProcess();
        string inherited = collector.Configure(new ProcessStartInfo(Environment.ProcessPath!), new() { ["role"] = "quasar" })!;
        collector.Started(inherited, process);
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false, inheritedDirectory: inherited);
        Assert.Equal(inherited, provider.DiagnosticDirectory);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "runs")));
        provider.CreateLogger("Test").LogError("Adopted run");
        Assert.Single(Reports(provider));
    }

    [Fact]
    public void ProviderRejectsInheritedRunForAnotherProcess()
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var process = Process.GetCurrentProcess();
        string inherited = collector.Configure(new ProcessStartInfo(Environment.ProcessPath!), new() { ["role"] = "quasar" })!;
        collector.Started(inherited, process);
        string metadata = Path.Combine(inherited, "run.json");
        var run = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadata))!;
        run["processId"] = process.Id + 1;
        File.WriteAllText(metadata, run.ToJsonString());
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false, inheritedDirectory: inherited);
        Assert.NotEqual(inherited, provider.DiagnosticDirectory);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "runs")).Length);
    }

    [Fact]
    public void ProviderResolvesInsideLoggerFactoryWithoutLifetimeDependencyCycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DiagnosticCollector(_root, "test-host"));
        services.AddSingleton<QuasarDiagnosticLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<QuasarDiagnosticLoggerProvider>());
        using var container = services.BuildServiceProvider();
        container.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogError("Factory integration");
        Assert.Single(Reports(container.GetRequiredService<QuasarDiagnosticLoggerProvider>()));
    }

    private static IncidentReport[] Reports(QuasarDiagnosticLoggerProvider provider) =>
        Directory.EnumerateFiles(provider.DiagnosticDirectory!, "*.json")
            .Where(path => DiagnosticsProtocol.IsReportId(Path.GetFileNameWithoutExtension(path)))
            .Select(path => JsonSerializer.Deserialize<IncidentReport>(File.ReadAllBytes(path), DiagnosticsProtocol.Json)!).ToArray();
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddMinutes(1);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
