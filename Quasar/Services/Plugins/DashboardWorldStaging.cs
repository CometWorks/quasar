using System.Security.Claims;
using Quasar.Client.Fleet;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

// File-backed staging is held between the review and commit actions. Unclaimed staging expires even if
// the tab disappears. A claimed directory belongs to the in-flight operation until it releases it.
public sealed class DashboardWorldStaging : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Dictionary<(string, Guid), Entry> _entries = new();
    private readonly Timer _sweep;
    public DashboardWorldStaging() => _sweep = new Timer(_ => Sweep(), null, Lifetime, TimeSpan.FromMinutes(1));

    internal sealed class Entry(string owner, WorldTarget target)
    {
        internal string Owner { get; } = owner;
        internal WorldTarget Target { get; } = target;
        internal bool Working = true;
        internal DateTimeOffset UsedAt = DateTimeOffset.UtcNow;
        internal volatile WorldTransferProgress Progress = new("Reading world", 0);
        internal StagedWorld? Files;
        internal string Path = "";
        internal string? TemplateId;
    }

    private static string Owner(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new QuasarOperationException(401, "A signed-in browser session is required.");
        return (user.FindFirstValue(QuasarClaimTypes.Provider) ?? user.Identity?.AuthenticationType) + "/" + id;
    }

    internal Entry Begin(ClaimsPrincipal user, WorldTarget target)
    {
        var key = (Owner(user), target.Id);
        if (target.Id == Guid.Empty) throw new QuasarOperationException(400, "An import ID is required.");
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) throw new QuasarOperationException(409, "This import is already open.");
            if (_entries.Count >= QuasarTransferRegistry.TotalLimit || _entries.Values.Count(e => e.Owner == key.Item1) >= QuasarTransferRegistry.PerSessionLimit)
                throw new QuasarOperationException(429, "Close an open world import before starting another one.");
            var entry = new Entry(key.Item1, target);
            _entries.Add(key, entry);
            return entry;
        }
    }

    internal Entry Claim(ClaimsPrincipal user, WorldTarget target)
    {
        lock (_gate)
        {
            var entry = Find(user, target) ?? throw new QuasarOperationException(404, "World staging has expired. Read the world again.");
            if (entry.Working) throw new QuasarOperationException(409, "The import is busy.");
            entry.Working = true;
            return entry;
        }
    }

    private Entry? Find(ClaimsPrincipal user, WorldTarget target)
    {
        var entry = _entries.GetValueOrDefault((Owner(user), target.Id));
        return entry?.Target == target ? entry : null;
    }

    internal WorldTransferProgress? Status(ClaimsPrincipal user, WorldTarget target)
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
            entry.Working = false;
            entry.UsedAt = DateTimeOffset.UtcNow;
            if (discard) _entries.Remove((entry.Owner, entry.Target.Id));
        }
        if (discard) entry.Files?.Dispose();
    }

    internal void Discard(ClaimsPrincipal user, WorldTarget target)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = Find(user, target);
            if (entry is null || entry.Working) return;
            _entries.Remove((entry.Owner, entry.Target.Id));
        }
        entry.Files?.Dispose();
    }

    private void Sweep()
    {
        Entry[] stale;
        lock (_gate)
        {
            stale = _entries.Values.Where(e => !e.Working && DateTimeOffset.UtcNow - e.UsedAt >= Lifetime).ToArray();
            foreach (var entry in stale) _entries.Remove((entry.Owner, entry.Target.Id));
        }
        foreach (var entry in stale) entry.Files?.Dispose();
    }

    public void Dispose()
    {
        _sweep.Dispose();
        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(e => !e.Working)) entry.Files?.Dispose();
            _entries.Clear();
        }
    }
}
