using System.Diagnostics;
using System.Text.Json;
using CometWorks.Diagnostics;
using Quasar.Diagnostics;

namespace Quasar.Services;

/// <summary>Bounded local evidence for Quasar itself; consent and delivery remain the collector's responsibility.</summary>
[ProviderAlias("Diagnostics")]
public sealed class QuasarDiagnosticLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly string _policyPath;
    private readonly DiagnosticCollector _collector;
    private readonly Dictionary<string, string> _context;
    private readonly bool _subscribed;
    private bool _capturing;
    private bool _disposed;
    private DateTimeOffset _window;
    private int _count;
    internal string? DiagnosticDirectory { get; }

    public QuasarDiagnosticLoggerProvider(DiagnosticCollector collector) : this(collector, TimeProvider.System, true) { }

    internal QuasarDiagnosticLoggerProvider(DiagnosticCollector collector, TimeProvider clock, bool subscribe, string? inheritedDirectory = null)
    {
        _clock = clock;
        _collector = collector;
        _policyPath = collector.PolicyFilePath;
        _context = new() { ["role"] = "quasar", ["version"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown" };
        try
        {
            using var process = Process.GetCurrentProcess();
            DiagnosticDirectory = AdoptRun(collector.RootDirectory, inheritedDirectory
                ?? Environment.GetEnvironmentVariable("ERROR_REPORTING_DIRECTORY"), process.Id);
            if (DiagnosticDirectory is null && Environment.ProcessPath is { } executable)
            {
                DiagnosticDirectory = collector.Configure(new ProcessStartInfo(executable), _context);
                collector.Started(DiagnosticDirectory, process);
            }
        }
        catch { /* Diagnostic storage must never prevent Quasar startup. */ }
        if (subscribe)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            _subscribed = true;
        }
    }

    private static string? AdoptRun(string root, string? inherited, int pid)
    {
        if (string.IsNullOrEmpty(inherited)) return null;
        try
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inherited));
            string expectedParent = Path.Combine(root, "runs");
            if (!string.Equals(Path.GetDirectoryName(directory), expectedParent,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            string metadata = Path.Combine(directory, "run.json");
            if ((File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0) return null;
            var publication = Stopwatch.StartNew();
            do
            {
                using var run = JsonDocument.Parse(File.ReadAllBytes(metadata));
                var identity = run.RootElement;
                if (identity.TryGetProperty("processId", out var processId) && processId.ValueKind == JsonValueKind.Number)
                {
                    if (processId.GetInt32() != pid) return null;
                    if (identity.TryGetProperty("kernelIdentity", out var kernel) && kernel.ValueKind == JsonValueKind.String
                        && DiagnosticCollector.CaptureIdentity(pid) is { } actual && kernel.GetString() != actual) return null;
                    return directory;
                }
                // Bootstrap publishes the PID just after Start; the worker can reach this point first.
                Thread.Sleep(10);
            } while (publication.Elapsed < TimeSpan.FromMilliseconds(200));
        }
        catch { /* Invalid inherited evidence cannot prevent a new local capture run. */ }
        return null;
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, Limit(categoryName, 128));

    public void MarkExpectedStop()
    {
        if (DiagnosticDirectory is { } directory) DiagnosticCollector.ExpectExit(directory);
    }

    public void CaptureFatal(Exception? exception)
    {
        // Program's catch may run after the host disposed its logging providers.
        Capture("Quasar", LogLevel.Critical, default, () => "Unhandled Quasar process exception", exception, fatal: true);
        if (DiagnosticDirectory is { } directory) DiagnosticCollector.Exited(directory, 1, expected: false);
    }

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args) => CaptureFatal(args.ExceptionObject as Exception);

    private void Capture(string category, LogLevel level, EventId eventId, Func<string> message, Exception? exception, bool fatal = false)
    {
        if (DiagnosticDirectory is null || !Monitor.TryEnter(_gate, fatal ? 100 : 0)) return;
        try
        {
            // Monitor is reentrant: explicitly suppress logging triggered by a formatter or exception.ToString().
            if (_capturing || _disposed && !fatal) return;
            _capturing = true;
            try
            {
                if (level < LogLevel.Error)
                {
                    LogBatchCapture.Append(DiagnosticDirectory, _policyPath, category, level.ToString(), message());
                    return;
                }
                var now = _clock.GetUtcNow();
                if (now - _window >= TimeSpan.FromMinutes(1)) { _window = now; _count = 0; }
                if (!fatal && ++_count > 32) return;
                var context = new Dictionary<string, string>(_context)
                { ["eventId"] = eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (eventId.Name is { } name) context["eventName"] = Limit(name, 128);
                var report = new IncidentReport(1, Guid.NewGuid().ToString("N"), now, category,
                    fatal ? "unhandled_exception" : "log", level.ToString(), Limit(message(), 8192),
                    exception is null ? null : Limit(exception.ToString(), 32768), context);
                // Only incident IDs participate in retention; never remove run.json or exit.json.
                var existing = Directory.EnumerateFiles(DiagnosticDirectory, "*.json")
                    .Where(path => DiagnosticsProtocol.IsReportId(Path.GetFileNameWithoutExtension(path)))
                    .OrderBy(File.GetLastWriteTimeUtc).ToArray();
                foreach (string stale in existing.Take(Math.Max(0, existing.Length - 255)))
                {
                    File.Delete(stale);
                    File.Delete(Path.ChangeExtension(stale, ".sealed"));
                }
                string temporary = Path.Combine(DiagnosticDirectory, report.ReportId + ".tmp");
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var file = new FileStream(temporary, options))
                    {
                        JsonSerializer.Serialize(file, report, DiagnosticsProtocol.Json);
                        file.Flush(true);
                    }
                    File.Move(temporary, Path.ChangeExtension(temporary, ".json"));
                }
                finally { File.Delete(temporary); }
            }
            catch { /* Never emit logs from capture, including disk-full and formatter failures. */ }
            finally { _capturing = false; }
        }
        finally { Monitor.Exit(_gate); }
    }

    private static string Limit(string? value, int maximum) => value is null ? "" : value[..Math.Min(value.Length, maximum)];

    private sealed class CaptureLogger(QuasarDiagnosticLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Error or LogLevel.Critical
            || logLevel is >= LogLevel.Trace and < LogLevel.Error && owner._collector.CurrentPolicy is not null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Capture(category, logLevel, eventId, () => formatter(state, exception), exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (DiagnosticDirectory is { } directory) LogBatchCapture.Flush(directory, _policyPath, force: true);
        }
        if (_subscribed) AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
    }
}
