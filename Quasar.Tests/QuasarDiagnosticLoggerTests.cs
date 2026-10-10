using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading.Channels;
using CometWorks.Diagnostics;
using Quasar.Host.Contract.V1;
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
    public async Task LoggingCapturesWithoutConsentAndIgnoresScopesAndRecursiveFormatting()
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
        await provider.DrainAsync();
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
    public async Task OrdinaryLogsAreNotDuplicatedEvenWithDiagnosticConsent()
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false);
        using var rsa = RSA.Create(3072);
        await collector.ApplyPolicyAsync(new HostDiagnosticPolicy(Guid.NewGuid().ToString("N"), true, false,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), DiagnosticsProtocol.KeyId(rsa),
            rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1)), default);
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Components.RenderTree.Renderer");
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning })
        {
            Assert.False(logger.IsEnabled(level));
            logger.Log(level, "Existing Quasar log sink owns this message");
        }
        await provider.DrainAsync();
        Assert.Empty(Reports(provider));
        Assert.False(File.Exists(Path.Combine(provider.DiagnosticDirectory!, "log-batch.pending")));
        Assert.Empty(Directory.GetFiles(provider.DiagnosticDirectory!, "log-*.json"));
    }

    [Fact]
    public async Task LoggingReturnsWhenWriterIsBlockedAndQueueIsFull()
    {
        using var provider = new QuasarDiagnosticLoggerProvider(new DiagnosticCollector(_root, "test-host"),
            TimeProvider.System, subscribe: false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Stall the storage worker deterministically, without relying on disk speed or platform file locks.
        var queue = (Channel<Action>)typeof(QuasarDiagnosticLoggerProvider)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(provider)!;
        Assert.True(queue.Writer.TryWrite(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            var logger = provider.CreateLogger("Test");
            await Task.Run(() =>
            {
                for (int count = 0; count < 1000; count++) logger.LogError("Failure {Count}", count);
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(256, queue.Reader.Count);
            Assert.Empty(Reports(provider));
        }
        finally { release.Set(); }
        await provider.DrainAsync();
        Assert.Equal(32, Reports(provider).Length);
    }

    [Fact]
    public void DisposeDrainsAcceptedErrorsAndStopsFurtherLogging()
    {
        using var provider = new QuasarDiagnosticLoggerProvider(new DiagnosticCollector(_root, "test-host"),
            TimeProvider.System, subscribe: false);
        var logger = provider.CreateLogger("Test");
        logger.LogError("Accepted before shutdown");
        provider.Dispose();
        Assert.Equal("Accepted before shutdown", Assert.Single(Reports(provider)).Message);
        Assert.False(logger.IsEnabled(LogLevel.Error));
        logger.LogError("After shutdown");
        Assert.Single(Reports(provider));
    }

    [Fact]
    public async Task ErrorStormIsBoundedAndFatalEvidencePreservesRunMetadata()
    {
        var clock = new ManualClock();
        using var provider = new QuasarDiagnosticLoggerProvider(new DiagnosticCollector(_root, "test-host"), clock, subscribe: false);
        var logger = provider.CreateLogger("Quasar.Test");
        for (int minute = 0; minute < 9; minute++)
        {
            for (int count = 0; count < 40; count++) logger.LogError("failure {Count}", count);
            await provider.DrainAsync();
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
    public async Task ProviderAdoptsMatchingBootstrapRunWithoutCreatingAnotherRun()
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var process = Process.GetCurrentProcess();
        string inherited = collector.Configure(new ProcessStartInfo(Environment.ProcessPath!), new() { ["role"] = "quasar" })!;
        collector.Started(inherited, process);
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false, inheritedDirectory: inherited);
        Assert.Equal(inherited, provider.DiagnosticDirectory);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "runs")));
        provider.CreateLogger("Test").LogError("Adopted run");
        await provider.DrainAsync();
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
    public async Task ProviderResolvesInsideLoggerFactoryWithoutLifetimeDependencyCycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DiagnosticCollector(_root, "test-host"));
        services.AddSingleton<QuasarDiagnosticLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<QuasarDiagnosticLoggerProvider>());
        using var container = services.BuildServiceProvider();
        container.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogError("Factory integration");
        var provider = container.GetRequiredService<QuasarDiagnosticLoggerProvider>();
        await provider.DrainAsync();
        Assert.Single(Reports(provider));
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
