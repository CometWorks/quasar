using System.Diagnostics.CodeAnalysis;

namespace Quasar.Services.Backup;

/// <summary>
/// Reserves managed servers during restores and world backups. The supervisor
/// must refuse to start a server until its file operation finishes. Operations
/// and starts come from independent requests/threads, so all access is synchronized.
/// </summary>
public sealed class ServerRestoreCoordinator
{
    private readonly object _sync = new();
    private readonly HashSet<string> _restoring = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _backingUp = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFileOperationInProgress(string uniqueName)
    {
        lock (_sync)
            return _restoring.Contains(uniqueName) || _backingUp.Contains(uniqueName);
    }

    public bool IsBackupInProgress(string uniqueName)
    {
        lock (_sync)
            return _backingUp.Contains(uniqueName);
    }

    public bool TryBeginBackup(string uniqueName, [NotNullWhen(true)] out IDisposable? scope) =>
        TryBegin(uniqueName, _backingUp, out scope);

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
    /// slot, or a world backup is running, so conflicting file operations cannot overlap.
    /// </summary>
    public bool TryBeginRestore(string uniqueName, [NotNullWhen(true)] out IDisposable? scope) =>
        TryBegin(uniqueName, _restoring, out scope);

    private bool TryBegin(string uniqueName, HashSet<string> operations, [NotNullWhen(true)] out IDisposable? scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueName);

        lock (_sync)
        {
            if (_restoring.Contains(uniqueName) || _backingUp.Contains(uniqueName))
            {
                scope = null;
                return false;
            }
            operations.Add(uniqueName);
        }

        scope = new OperationScope(this, uniqueName, operations);
        return true;
    }

    private void Release(string uniqueName, HashSet<string> operations)
    {
        lock (_sync)
            operations.Remove(uniqueName);
    }

    private sealed class OperationScope(ServerRestoreCoordinator owner, string uniqueName, HashSet<string> operations) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.Release(uniqueName, operations);
        }
    }
}
