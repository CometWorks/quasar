namespace Quasar.Services;

/// <summary>
/// Notices changes to the <c>&lt;directory&gt;/&lt;child&gt;/&lt;fileName&gt;</c> files that hold the
/// server, cluster, config profile and world template definitions, by checking them every
/// 2 seconds. A <see cref="FileSystemWatcher"/> can reach that depth only by watching the
/// whole tree, saves, logs and world templates included: on Linux that costs one inotify
/// watch per subdirectory, and on Windows every write in the tree fills its event buffer
/// until it overflows and drops the definition edits (SE1-0195).
/// </summary>
public sealed class ChildFileWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly string _fileName;
    private readonly Action _changed;
    private readonly Timer _timer;
    private string _signature;
    private int _polling;

    public ChildFileWatcher(string directory, string fileName, Action changed)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
        _fileName = fileName;
        _changed = changed;
        _signature = Signature();
        _timer = new Timer(_ => Poll(), null, Interval, Interval);
    }

    /// <summary>The <paramref name="fileName"/> files one level below <paramref name="directory"/>.</summary>
    public static IReadOnlyList<string> FindFiles(string directory, string fileName) =>
        !Directory.Exists(directory)
            ? []
            : Directory
                .EnumerateDirectories(directory)
                .Select(child => Path.Combine(child, fileName))
                .Where(File.Exists)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            var signature = Signature();
            if (signature == _signature)
                return;

            _signature = signature;
            _changed();
        }
        catch (Exception)
        {
            // Listing races with folders going away, and the catalogs log their own reload
            // failures. Nothing may escape a timer callback: it would end the process.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private string Signature() =>
        string.Join(
            '\n',
            FindFiles(_directory, _fileName)
                .Select(path => new FileInfo(path))
                .Select(file =>
                    $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{(file.Exists ? file.Length : -1)}"
                )
        );
}
