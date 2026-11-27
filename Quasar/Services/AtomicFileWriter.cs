using System.Text;

namespace Quasar.Services;

public static class AtomicFileWriter
{
    private static readonly UnixFileMode PrivateUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        UnixFileMode? unixCreateMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            unixCreateMode = File.GetUnixFileMode(path);

        await WriteTextAsync(path, content, unixCreateMode, cancellationToken);
    }

    public static Task WritePrivateTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default) =>
        WriteTextAsync(path, content, PrivateUnixFileMode, cancellationToken);

    private static async Task WriteTextAsync(
        string path,
        string content,
        UnixFileMode? unixCreateMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Cannot resolve directory for path '{path}'.");

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (unixCreateMode.HasValue && !OperatingSystem.IsWindows())
                options.UnixCreateMode = unixCreateMode.Value;

            await using (var stream = new FileStream(tempPath, options))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                // Without a durable flush a power loss after the rename can leave a zero-length target.
                stream.Flush(flushToDisk: true);
            }

            await MoveWithRetryAsync(tempPath, path, cancellationToken);
        }
        catch
        {
            // Cancellation or an I/O fault before the atomic rename leaves a
            // partial temp file behind; the target is untouched. Remove the temp
            // so these do not accumulate, then surface the original failure.
            TryDelete(tempPath);
            throw;
        }
    }

    private const int MoveRetries = 20;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(50);

    // On Windows, replacing a file fails with "access denied" or a sharing violation
    // while another process holds it open without delete sharing, for example a file
    // watcher reloading it or a virus scanner reading it. Those holds are brief, so
    // retry for about a second before giving up and surfacing the original error.
    private static async Task MoveWithRetryAsync(string source, string target, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception error) when (
                OperatingSystem.IsWindows()
                && (error is IOException or UnauthorizedAccessException)
                && attempt < MoveRetries)
            {
                await Task.Delay(MoveRetryDelay, cancellationToken);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort — a leftover temp file is harmless, never the target.
        }
    }
}
