using System.Diagnostics;
using System.Text;
using Quasar.Diagnostics;
using Quasar.Hosting;

namespace Quasar.Host;

/// <summary>
/// Passes a child's stdout and stderr through to the Host's own console and keeps the last lines,
/// so a failure report can say why the child exited.
/// </summary>
/// <remarks>
/// On Linux the child appends both streams to a log file this Host follows (<see cref="Follow"/>). The file
/// outlives the Host, so the next Host follows a child it adopts from where the file ends. A pipe breaks
/// with the Host that reads it: a .NET child ignores the broken pipe and keeps running, writing into nothing.
/// Elsewhere the child writes into pipes this Host reads, and only children of this Host process have a tail.
/// </remarks>
internal sealed class ProcessOutputTail : IDisposable
{
    internal const int MaxLines = 20;
    internal const int MaxLineLength = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly Process? _process;
    private readonly string _prefix;
    private readonly string? _diagnosticDirectory;
    private readonly Queue<string> _lines = new();
    private readonly FileStream? _log;
    private readonly string? _logPath;
    private readonly List<byte> _partial = [];
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Starts reading a process started with both streams redirected, and takes ownership of it.</summary>
    internal ProcessOutputTail(Process process, string prefix, string? diagnosticDirectory = null)
    {
        _process = process;
        _prefix = prefix;
        _diagnosticDirectory = diagnosticDirectory;
        process.OutputDataReceived += (_, line) => Add(line.Data, Console.Out);
        process.ErrorDataReceived += (_, line) => Add(line.Data, Console.Error);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private ProcessOutputTail(
        Process? process,
        string prefix,
        string? diagnosticDirectory,
        string logPath,
        long offset
    )
    {
        _process = process;
        _prefix = prefix;
        _diagnosticDirectory = diagnosticDirectory;
        _logPath = logPath;
        _log = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1
        );
        _log.Position = Math.Min(offset, _log.Length);
        _ = Task.Run(FollowAsync);
    }

    /// <summary>
    /// Follows a log file a process appends its output to, from <paramref name="offset"/>: where the file
    /// ended before a launch, or its end when a Host adopts a running process. Takes ownership of
    /// <paramref name="process"/>; without one, <see cref="DrainAsync"/> reports no exit code.
    /// </summary>
    internal static ProcessOutputTail Follow(
        Process? process,
        string prefix,
        string? diagnosticDirectory,
        string logPath,
        long offset
    ) => new(process, prefix, diagnosticDirectory, logPath, offset);

    private async Task FollowAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                ReadNew();
                await Task.Delay(PollInterval, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Reads what was appended since the last call, then rotates the file once it is too big.
    private void ReadNew()
    {
        lock (_partial)
        {
            if (_log is null || _stop.IsCancellationRequested)
                return;
            try
            {
                // Rotation truncates the file in place; go on from its beginning.
                if (_log.Length < _log.Position)
                    _log.Position = 0;
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = _log.Read(buffer)) > 0)
                    for (int i = 0; i < read; i++)
                        if (buffer[i] != '\n')
                            _partial.Add(buffer[i]);
                        else
                        {
                            Add(
                                Encoding.UTF8.GetString(_partial.ToArray()).TrimEnd('\r'),
                                Console.Out
                            );
                            _partial.Clear();
                        }
                if (
                    _log.Position >= OutputLogRotation.MaxBytes
                    && OutputLogRotation.RotateIfOver(
                        _logPath!,
                        OutputLogRotation.MaxBytes,
                        OutputLogRotation.Files
                    )
                )
                    _log.Position = 0;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"{_prefix}Output log read deferred ({exception.Message})."
                );
            }
        }
    }

    private void Add(string? line, TextWriter echo)
    {
        if (line is null)
            return;
        echo.WriteLine(_prefix + line);
        DiagnosticCollector.RecordOutput(_diagnosticDirectory, line);
        lock (_lines)
        {
            if (_lines.Count == MaxLines)
                _lines.Dequeue();
            _lines.Enqueue(line.Length > MaxLineLength ? line[..MaxLineLength] + "…" : line);
        }
    }

    /// <summary>
    /// Waits up to two seconds for the exited process's output to end, so its last lines are in,
    /// then returns its exit code (null if it has not exited or isn't this Host's child) and the kept lines.
    /// </summary>
    internal async Task<(int? ExitCode, string[] Lines)> DrainAsync(
        CancellationToken cancellationToken
    )
    {
        int? exitCode = null;
        if (_process is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                // Also waits for the end of the redirected streams. A grandchild that inherited them can hold them open.
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            exitCode = _process.HasExited ? _process.ExitCode : null;
        }
        ReadNew();
        lock (_lines)
            return (exitCode, _lines.ToArray());
    }

    public void Dispose()
    {
        lock (_partial)
        {
            _stop.Cancel();
            _log?.Dispose();
        }
        _process?.Dispose();
    }
}
