namespace Quasar.Services;

public sealed class DebouncedFileWatcher : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, bool> _isTrackedPath;
    private readonly Action _changed;
    private CancellationTokenSource? _debounce;

    private DebouncedFileWatcher(
        string directory,
        string filter,
        bool includeSubdirectories,
        Func<string, bool> isTrackedPath,
        Action changed)
    {
        _isTrackedPath = isTrackedPath;
        _changed = changed;
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = filter,
        };

        _watcher.Changed += HandleChanged;
        _watcher.Created += HandleChanged;
        _watcher.Deleted += HandleChanged;
        _watcher.Renamed += HandleChanged;
        _watcher.EnableRaisingEvents = true;
    }

    public static DebouncedFileWatcher? WatchFile(string path, Action changed)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        Directory.CreateDirectory(directory);
        return new DebouncedFileWatcher(
            directory,
            Path.GetFileName(fullPath),
            includeSubdirectories: false,
            candidate => PathsEqual(candidate, fullPath),
            changed);
    }

    public static DebouncedFileWatcher WatchDirectory(
        string directory,
        string filter,
        bool includeSubdirectories,
        Func<string, bool> isTrackedPath,
        Action changed)
    {
        Directory.CreateDirectory(directory);
        return new DebouncedFileWatcher(directory, filter, includeSubdirectories, isTrackedPath, changed);
    }

    public void Dispose()
    {
        _watcher.Dispose();

        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private void HandleChanged(object sender, FileSystemEventArgs args)
    {
        if (!IsTracked(args.FullPath)
            && (args is not RenamedEventArgs renamed || !IsTracked(renamed.OldFullPath)))
        {
            return;
        }

        Schedule();
    }

    private bool IsTracked(string path) =>
        !string.IsNullOrWhiteSpace(path) && _isTrackedPath(path);

    private void Schedule()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            debounce = _debounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay, debounce.Token);
                _changed();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), right, StringComparison.OrdinalIgnoreCase);
}
