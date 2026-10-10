using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CometWorks.Diagnostics;
using Quasar.Diagnostics;

namespace Quasar.Services;

/// <summary>Bounded local evidence for Quasar itself; consent and delivery remain the collector's responsibility.</summary>
[ProviderAlias("Diagnostics")]
public sealed class QuasarDiagnosticLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly object _enqueueGate = new();
    private readonly Channel<Action> _queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Task _writer;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, string> _context;
    private readonly bool _subscribed;
    private bool _capturing;
    private volatile bool _disposed;
    private DateTimeOffset _window;
    private int _count;
    internal string? DiagnosticDirectory { get; }

    public QuasarDiagnosticLoggerProvider(DiagnosticCollector collector) : this(collector, TimeProvider.System, true) { }

    internal QuasarDiagnosticLoggerProvider(DiagnosticCollector collector, TimeProvider clock, bool subscribe, string? inheritedDirectory = null)
    {
        _clock = clock;
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
        _writer = Task.Run(WriteQueuedAsync);
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
        try
        {
            Persist("Quasar", LogLevel.Critical, default, "Unhandled Quasar process exception",
                exception is null ? null : Limit(exception.ToString(), 32768), _clock.GetUtcNow(), fatal: true);
        }
        catch { /* Even a failing exception formatter must not interrupt process-exit recording. */ }
        if (DiagnosticDirectory is { } directory) DiagnosticCollector.Exited(directory, 1, expected: false);
    }

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args) => CaptureFatal(args.ExceptionObject as Exception);

    private void Capture(string category, LogLevel level, EventId eventId, Func<string> message, Exception? exception)
    {
        if (_disposed || DiagnosticDirectory is null || !Monitor.TryEnter(_enqueueGate)) return;
        try
        {
            if (_capturing || _disposed) return;
            _capturing = true;
            try
            {
                var at = _clock.GetUtcNow();
                // Format on the caller while its state is valid; retain only bounded text in the queue.
                string text = Limit(message(), 8192);
                string? error = exception is null ? null : Limit(exception.ToString(), 32768);
                eventId = new EventId(eventId.Id, eventId.Name is { } name ? Limit(name, 128) : null);
                _queue.Writer.TryWrite(() => Persist(category, level, eventId, text, error, at));
            }
            catch { /* Formatting failures must not escape application logging. */ }
            finally { _capturing = false; }
        }
        finally { Monitor.Exit(_enqueueGate); }
    }

    private async Task WriteQueuedAsync()
    {
        await foreach (var write in _queue.Reader.ReadAllAsync())
        {
            try { write(); }
            catch { /* Capture failures must not fault the background writer. */ }
        }
    }

    internal async Task DrainAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(() => drained.SetResult());
        await drained.Task;
    }

    private void Persist(string category, LogLevel level, EventId eventId, string message, string? exception,
        DateTimeOffset at, bool fatal = false)
    {
        if (DiagnosticDirectory is null || !Monitor.TryEnter(_gate, fatal ? 100 : 0)) return;
        try
        {
            var now = at;
            if (now - _window >= TimeSpan.FromMinutes(1)) { _window = now; _count = 0; }
            if (!fatal && ++_count > 32) return;
            var context = new Dictionary<string, string>(_context)
            { ["eventId"] = eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (eventId.Name is { } name) context["eventName"] = Limit(name, 128);
            var report = new IncidentReport(1, Guid.NewGuid().ToString("N"), now, category,
                fatal ? "unhandled_exception" : "log", level.ToString(), message, exception, context);
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
        catch { /* Never emit logs from capture, including disk-full failures. */ }
        finally { Monitor.Exit(_gate); }
    }

    private static string Limit(string? value, int maximum) => value is null ? "" : value[..Math.Min(value.Length, maximum)];

    private sealed class CaptureLogger(QuasarDiagnosticLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => !owner._disposed && logLevel is LogLevel.Error or LogLevel.Critical;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Capture(category, logLevel, eventId, () => formatter(state, exception), exception);
        }
    }

    public void Dispose()
    {
        lock (_enqueueGate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.TryComplete();
        }
        if (_subscribed) AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        // Shutdown may drain disk writes, but ordinary logger calls never wait for the writer.
        _writer.Wait(TimeSpan.FromSeconds(5));
    }
}
