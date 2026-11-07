using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading.Channels;
using System.Net.Sockets;
using CometWorks.Diagnostics;
using Microsoft.AspNetCore.Connections;
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

    private const string BindFailure = "Failed to bind to address http://[::]:8080: address already in use.";

    [Theory]
    [InlineData("socket", true)]
    [InlineData("kestrel", true)]
    [InlineData("startup", true)]
    [InlineData("wrapper", true)]
    [InlineData("aggregate", true)]
    [InlineData("io-aggregate", true)]
    [InlineData("io-message", true)]
    [InlineData("access-denied", false)]
    [InlineData("address-not-available", false)]
    [InlineData("io-other", false)]
    [InlineData("message-mention", false)]
    [InlineData("mixed-aggregate", false)]
    [InlineData("mixed-aggregate-reversed", false)]
    [InlineData("misleading-wrapper", false)]
    public async Task BindFailureClassificationPreservesLocalEvidenceAndOrdinaryBatches(string form, bool excluded)
    {
        Action throwSocket = () => throw new SocketException((int)SocketError.AddressAlreadyInUse);
        var socket = Assert.Throws<SocketException>(throwSocket);
        var startup = new IOException(BindFailure, new AddressInUseException("Address already in use", socket));
        Exception exception = form switch
        {
            "socket" => socket,
            "kestrel" => new AddressInUseException("Address already in use"),
            "startup" => startup,
            "wrapper" => new InvalidOperationException("Hosting failed", startup),
            "aggregate" => new IOException("Failed to bind to address http://localhost:8080.",
                new AggregateException(startup, new AddressInUseException("IPv4 port occupied", socket))),
            "io-aggregate" => new AggregateException(new IOException(BindFailure), new IOException(BindFailure)),
            "io-message" => new IOException(BindFailure),
            "access-denied" => new IOException("Failed to bind: permission denied", new SocketException((int)SocketError.AccessDenied)),
            "address-not-available" => new SocketException((int)SocketError.AddressNotAvailable),
            "io-other" => new IOException("Failed to bind to address http://[::]:8080: transport failure."),
            "message-mention" => new InvalidOperationException("Previous attempt: " + BindFailure),
            "mixed-aggregate" => new AggregateException(startup, new InvalidOperationException("Unrelated failure")),
            "mixed-aggregate-reversed" => new AggregateException(new InvalidOperationException("Unrelated failure"), startup),
            "misleading-wrapper" => new IOException(BindFailure, new SocketException((int)SocketError.AccessDenied)),
            _ => throw new ArgumentOutOfRangeException(nameof(form)),
        };
        var collector = new DiagnosticCollector(_root, "test-host");
        using var rsa = RSA.Create(3072);
        await collector.ApplyPolicyAsync(new HostDiagnosticPolicy(Guid.NewGuid().ToString("N"), true, false,
            1, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(2), DiagnosticsProtocol.KeyId(rsa),
            rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1)), default);
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false);
        provider.CreateLogger("Microsoft.Extensions.Hosting.Internal.Host").LogError(exception, "Hosting failed to start");
        await provider.DrainAsync();
        provider.Dispose(); // Program catches the startup failure after host disposal.
        provider.CaptureFatal(exception);
        Assert.Equal(2, Reports(provider).Length);
        Assert.NotNull(DiagnosticCollector.ReportedException(provider.DiagnosticDirectory));
        Assert.True(File.Exists(Path.Combine(provider.DiagnosticDirectory!, "exit.json")));
        using (var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(provider.DiagnosticDirectory!, "run.json"))))
        {
            Assert.False(run.RootElement.GetProperty("expectedExit").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, run.RootElement.GetProperty("endedAtUtc").ValueKind);
        }
        // Bootstrap's stdout batches may contain the same failure text and must keep working.
        DiagnosticCollector.RecordOutput(provider.DiagnosticDirectory, BindFailure);
        LogBatchCapture.Flush(provider.DiagnosticDirectory!, collector.PolicyFilePath, force: true);
        await collector.CollectAsync(default);
        var shared = new List<IncidentReport>();
        foreach (var envelope in collector.GetReports())
            shared.Add(await IncidentArchive.ValidateAsync(Path.Combine(collector.IncomingDirectory, envelope.ReportId + ".zip"), envelope, rsa));
        var batch = Assert.Single(shared, report => report.Kind == "log_batch");
        Assert.Contains(BindFailure, batch.Message);
        Assert.Equal(excluded ? 1 : 4, shared.Count);
        Assert.Equal(2, Reports(provider).Length); // Exclusion never deletes local exception evidence.
        if (!excluded)
        {
            Assert.Single(shared, report => report.Kind == "log");
            Assert.Single(shared, report => report.Kind == "unhandled_exception");
            Assert.Single(shared, report => report.Kind == "unexpected_exit");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoggedBindFailureDoesNotHideAnUnrelatedProcessExit(bool unrelatedFatal)
    {
        var collector = new DiagnosticCollector(_root, "test-host");
        using var rsa = RSA.Create(3072);
        await collector.ApplyPolicyAsync(new HostDiagnosticPolicy(Guid.NewGuid().ToString("N"), true, false,
            1, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(2), DiagnosticsProtocol.KeyId(rsa),
            rsa.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow.AddDays(1)), default);
        using var provider = new QuasarDiagnosticLoggerProvider(collector, TimeProvider.System, subscribe: false);
        provider.CreateLogger("Test").LogError(new AddressInUseException("Port occupied"), "Bind failed");
        await provider.DrainAsync();
        if (unrelatedFatal)
        {
            provider.CaptureFatal(new AddressInUseException("Port occupied"));
            provider.CaptureFatal(new InvalidOperationException("Unrelated crash"));
        }
        else DiagnosticCollector.Exited(provider.DiagnosticDirectory!, 1, expected: false);
        await collector.CollectAsync(default);
        Assert.Equal(unrelatedFatal ? 2 : 1, collector.GetReports().Length);
        Assert.True(File.Exists(Path.Combine(provider.DiagnosticDirectory!, "exit.json")));
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
