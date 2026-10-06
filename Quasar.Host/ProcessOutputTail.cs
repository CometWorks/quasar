using System.Diagnostics;

namespace Quasar.Host;

/// <summary>
/// Passes a child's stdout and stderr through to the Host's own console and keeps the last lines,
/// so a failure report can say why the child exited.
/// </summary>
/// <remarks>
/// The child writes into pipes this Host reads. After a Host restart a still-running child's output
/// goes nowhere (a .NET child ignores the broken pipe and keeps running); only children started by
/// this Host process have a tail.
/// </remarks>
internal sealed class ProcessOutputTail : IDisposable
{
    internal const int MaxLines = 20;
    internal const int MaxLineLength = 300;
    private readonly Process _process;
    private readonly string _prefix;
    private readonly Queue<string> _lines = new();

    /// <summary>Starts reading a process started with both streams redirected, and takes ownership of it.</summary>
    internal ProcessOutputTail(Process process, string prefix)
    {
        _process = process;
        _prefix = prefix;
        process.OutputDataReceived += (_, line) => Add(line.Data, Console.Out);
        process.ErrorDataReceived += (_, line) => Add(line.Data, Console.Error);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void Add(string? line, TextWriter echo)
    {
        if (line is null)
            return;
        echo.WriteLine(_prefix + line);
        lock (_lines)
        {
            if (_lines.Count == MaxLines)
                _lines.Dequeue();
            _lines.Enqueue(line.Length > MaxLineLength ? line[..MaxLineLength] + "…" : line);
        }
    }

    /// <summary>
    /// Waits up to two seconds for the exited process's pipes to close, so its last lines are in,
    /// then returns its exit code (null if it has not exited) and the kept lines.
    /// </summary>
    internal async Task<(int? ExitCode, string[] Lines)> DrainAsync(
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            // Also waits for the end of the redirected streams. A grandchild that inherited them can hold them open.
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        int? exitCode = _process.HasExited ? _process.ExitCode : null;
        lock (_lines)
            return (exitCode, _lines.ToArray());
    }

    public void Dispose() => _process.Dispose();
}
