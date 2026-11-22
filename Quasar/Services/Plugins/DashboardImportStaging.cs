using System.Security.Claims;
using Quasar.Client.Fleet;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

// File-backed staging is held between the review and commit actions. Unclaimed staging expires even if
// the tab disappears. A claimed directory belongs to the in-flight operation until it releases it.
public abstract class DashboardImportStaging<TTarget, TStage>(Func<TTarget, Guid> importId) : IDisposable
    where TTarget : notnull where TStage : class, IDisposable, new()
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Dictionary<(string, Guid), Entry> _entries = new();
    private Timer? _sweep;
    private bool _disposed;

    internal sealed class Entry(string owner, TTarget target)
    {
        internal string Owner { get; } = owner;
        internal TTarget Target { get; } = target;
        internal bool Working = true;
        internal DateTimeOffset UsedAt = DateTimeOffset.UtcNow;
        internal volatile WorldTransferProgress Progress = new("Reading world", 0);
        internal TStage Stage { get; } = new();
    }

    private static string Owner(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new QuasarOperationException(401, "A signed-in browser session is required.");
        return (user.FindFirstValue(QuasarClaimTypes.Provider) ?? user.Identity?.AuthenticationType) + "/" + id;
    }

    internal Entry Begin(ClaimsPrincipal user, TTarget target)
    {
        var key = (Owner(user), importId(target));
        if (importId(target) == Guid.Empty) throw new QuasarOperationException(400, "An import ID is required.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sweep ??= new Timer(_ => Sweep(), null, Lifetime, TimeSpan.FromMinutes(1));
            if (_entries.ContainsKey(key)) throw new QuasarOperationException(409, "This import is already open.");
            if (_entries.Count >= QuasarTransferRegistry.TotalLimit || _entries.Values.Count(e => e.Owner == key.Item1) >= QuasarTransferRegistry.PerSessionLimit)
                throw new QuasarOperationException(429, "Close an open import before starting another one.");
            var entry = new Entry(key.Item1, target);
            _entries.Add(key, entry);
            return entry;
        }
    }

    internal Entry Claim(ClaimsPrincipal user, TTarget target)
    {
        lock (_gate)
        {
            var entry = Find(user, target) ?? throw new QuasarOperationException(404, "Import staging has expired. Read the source again.");
            if (entry.Working) throw new QuasarOperationException(409, "The import is busy.");
            entry.Working = true;
            return entry;
        }
    }

    private Entry? Find(ClaimsPrincipal user, TTarget target)
    {
        var entry = _entries.GetValueOrDefault((Owner(user), importId(target)));
        return entry is not null && EqualityComparer<TTarget>.Default.Equals(entry.Target, target) ? entry : null;
    }

    internal WorldTransferProgress? Status(ClaimsPrincipal user, TTarget target)
    {
        lock (_gate)
        {
            var entry = Find(user, target);
            if (entry is null) return null;
            entry.UsedAt = DateTimeOffset.UtcNow;
            return entry.Progress;
        }
    }

    internal void Release(Entry entry, bool discard)
    {
        lock (_gate)
        {
            discard |= _disposed;
            entry.Working = false;
            entry.UsedAt = DateTimeOffset.UtcNow;
            if (discard) _entries.Remove((entry.Owner, importId(entry.Target)));
        }
        if (discard) entry.Stage.Dispose();
    }

    internal void Discard(ClaimsPrincipal user, TTarget target)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = Find(user, target);
            if (entry is null || entry.Working) return;
            _entries.Remove((entry.Owner, importId(entry.Target)));
        }
        entry.Stage.Dispose();
    }

    private void Sweep()
    {
        Entry[] stale;
        lock (_gate)
        {
            stale = _entries.Values.Where(e => !e.Working && DateTimeOffset.UtcNow - e.UsedAt >= Lifetime).ToArray();
            foreach (var entry in stale) _entries.Remove((entry.Owner, importId(entry.Target)));
        }
        foreach (var entry in stale) entry.Stage.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _sweep?.Dispose();
            foreach (var entry in _entries.Values.Where(e => !e.Working)) entry.Stage.Dispose();
            _entries.Clear();
        }
    }
}
