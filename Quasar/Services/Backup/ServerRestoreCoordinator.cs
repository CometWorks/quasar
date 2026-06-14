using System.Diagnostics.CodeAnalysis;

namespace Quasar.Services.Backup;

/// <summary>
/// Tracks which managed servers currently have a backup restore in progress.
/// A restore rewrites a server's files in place, so the supervisor must refuse
/// to start that server until the restore finishes. Restores and starts are
/// triggered from independent requests/threads, so all access is synchronized.
/// </summary>
public sealed class ServerRestoreCoordinator
{
    private readonly object _sync = new();
    private readonly HashSet<string> _restoring = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a restore is running for the given server.</summary>
    public bool IsRestoreInProgress(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return false;

        lock (_sync)
            return _restoring.Contains(uniqueName);
    }

    /// <summary>
    /// Claims the restore slot for a server, returning a scope that releases it
    /// on dispose. Returns <c>false</c> when another restore already holds the
    /// slot, so callers never run two restores against the same server at once.
    /// </summary>
    public bool TryBeginRestore(string uniqueName, [NotNullWhen(true)] out IDisposable? scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueName);

        lock (_sync)
        {
            if (!_restoring.Add(uniqueName))
            {
                scope = null;
                return false;
            }
        }

        scope = new RestoreScope(this, uniqueName);
        return true;
    }

    private void Release(string uniqueName)
    {
        lock (_sync)
            _restoring.Remove(uniqueName);
    }

    private sealed class RestoreScope(ServerRestoreCoordinator owner, string uniqueName) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.Release(uniqueName);
        }
    }
}
