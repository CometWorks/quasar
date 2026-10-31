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

            File.Move(tempPath, path, overwrite: true);
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
