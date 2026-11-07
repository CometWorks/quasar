namespace Quasar.Hosting;

/// <summary>
/// Size-capped rotation of a log file that other processes keep appending to, such as the local Host's
/// <c>host.log</c>, which holds the Host's, the Gateway's and the nodes' output.
/// </summary>
/// <remarks>
/// The writers hold the file open through an inherited redirect, so it can't be swapped for a new one.
/// Rotation copies it to <c>.1</c> and truncates it in place (logrotate's copytruncate). A writer that opened
/// it with O_APPEND (<c>&gt;&gt;</c>) goes on at the new end; lines written between the copy and the truncation are lost.
/// </remarks>
internal static class OutputLogRotation
{
    internal const long MaxBytes = 64 * 1024 * 1024;
    internal const int Files = 4;
    private const int AppendFlag = 0x400; // O_APPEND

    /// <summary>
    /// Once <paramref name="live"/> reaches <paramref name="maxBytes"/>, shifts <c>log.1</c>.. up, copies the live
    /// file to <c>log.1</c> and empties it. At most <paramref name="files"/> files remain, the live one included.
    /// </summary>
    /// <param name="live">The path to open; the log itself, or <c>/proc/self/fd/N</c> for a redirected stream.</param>
    internal static bool RotateIfOver(string log, long maxBytes, int files, string? live = null)
    {
        if (!File.Exists(live ?? log))
            return false;
        using var stream = new FileStream(
            live ?? log,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            1
        );
        if (stream.Length < maxBytes)
            return false;
        File.Delete($"{log}.{files - 1}");
        for (int i = files - 2; i >= 1; i--)
            if (File.Exists($"{log}.{i}"))
                File.Move($"{log}.{i}", $"{log}.{i + 1}");
        using (var copy = new FileStream($"{log}.1", FileMode.CreateNew, FileAccess.Write))
            stream.CopyTo(copy); // Reads to the end, so it includes what was appended during the copy.
        stream.SetLength(0);
        return true;
    }

    /// <summary>
    /// The files this process's stdout and stderr append to, as (log, live) pairs. Empty when they go to a
    /// terminal, a pipe or the journal, or the file was opened without O_APPEND: after a truncation such a
    /// writer would go on at its old offset and leave a hole the size of what was removed.
    /// </summary>
    internal static IReadOnlyList<(string Log, string Live)> StandardStreamFiles()
    {
        var result = new List<(string Log, string Live)>();
        if (!OperatingSystem.IsLinux())
            return result;
        foreach (int fd in new[] { 1, 2 })
        {
            try
            {
                string live = $"/proc/self/fd/{fd}";
                if (
                    new FileInfo(live).LinkTarget is not { } log
                    || !Path.IsPathFullyQualified(log)
                    || !File.Exists(log)
                    || result.Exists(file => file.Log == log)
                )
                    continue;
                string? flags = File.ReadLines($"/proc/self/fdinfo/{fd}")
                    .FirstOrDefault(line => line.StartsWith("flags:"));
                if (flags is not null && (Convert.ToInt32(flags[6..].Trim(), 8) & AppendFlag) != 0)
                    result.Add((log, live));
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or FormatException)
            { }
        }
        return result;
    }
}
