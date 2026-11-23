using System.Text.Json;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

/// <summary>
/// Tracks the short-lived, single-use transfer leases used by the browser SDK.
/// </summary>
/// <remarks>
/// <para>
/// A transfer moves through three states:
/// </para>
/// <list type="number">
///   <item><description>
///   <c>POST /resources/{plugin}/{operation}</c> creates it and binds it to one
///   browser session and owner.
///   </description></item>
///   <item><description>
///   <c>GET</c> or <c>PUT /resources/{id}</c> claims it once and streams the
///   read or upload.
///   </description></item>
///   <item><description>
///   The streaming adapter removes it when the transfer finishes, or the
///   browser's cleanup <c>DELETE</c> releases it early.
///   </description></item>
/// </list>
/// <para>
/// Cleanup is best effort. A completed read normally removes its own lease
/// before the browser's <c>DELETE</c> arrives, and navigation can race the two.
/// Release therefore has to be idempotent; see
/// <see cref="Cancel(QuasarBrowserSession, string)"/>. No file or socket is
/// opened until a lease is claimed.
/// </para>
/// <para>
/// Cancellation and disposal run after the registry lock is released.
/// Cancellation callbacks run inline and can take other locks, so holding the
/// registry lock while cancelling would serialize unrelated requests and risks
/// a re-entrant deadlock.
/// </para>
/// </remarks>
public sealed class QuasarTransferRegistry(QuasarBrowserSessions sessions) : IDisposable
{
    public const int PerSessionLimit = 4;
    public const int PerUserLimit = 16;
    public const int TotalLimit = 64;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ActiveLifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private bool _disposed;

    public Transfer Create(QuasarBrowserSession binding, string plugin, string operation, QuasarOperationKind kind, JsonElement request)
    {
        sessions.Verify(binding);
        var owner = binding.Owner;
        var session = binding.Id;

        Transfer[] stale;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stale = _transfers.Values
                .Where(item => !item.Claimed && item.Token.IsCancellationRequested)
                .ToArray();
            foreach (var item in stale)
                _transfers.Remove(item.Id);
        }

        // Release stale leases before taking the lock again. Cancellation
        // callbacks run arbitrary code and must never run under the registry
        // lock; doing this first also prevents a callback failure from leaving
        // a newly created transfer behind.
        foreach (var item in stale)
            Release(item, dispose: true);

        lock (_gate)
        {
            // Dispose can run while the stale leases are released outside the
            // lock. Re-check before adding, so a transfer cannot outlive the
            // registry that is supposed to cancel it.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transfers.Count >= TotalLimit
                || _transfers.Values.Count(item => item.Session == session) >= PerSessionLimit
                || _transfers.Values.Count(item => item.Owner == owner) >= PerUserLimit)
                throw new QuasarOperationException(429, "Too many active SDK transfers. Close an existing transfer first.");

            var transfer = new Transfer(binding, plugin, operation, kind, request.Clone());
            _transfers.Add(transfer.Id, transfer);
            return transfer;
        }
    }

    public Transfer Get(QuasarBrowserSession binding, string id, bool claim)
    {
        sessions.Verify(binding);
        lock (_gate)
        {
            var transfer = FindOwned(binding, id);
            if (transfer is null || transfer.Token.IsCancellationRequested)
                throw new QuasarOperationException(404, "SDK transfer not found or expired.");
            if (claim)
            {
                if (transfer.Claimed) throw new QuasarOperationException(409, "SDK transfers are single-use.");
                transfer.Claimed = true;
                transfer.Stop.CancelAfter(ActiveLifetime);
            }
            return transfer;
        }
    }

    /// <summary>
    /// Releases a transfer owned by the current browser session. An unknown,
    /// expired, or foreign id is a no-op, so the SDK cleanup <c>DELETE</c> can
    /// return 204 without turning ordinary navigation into a failed request.
    /// </summary>
    public void Cancel(QuasarBrowserSession binding, string id)
    {
        sessions.Verify(binding);
        Transfer? transfer;
        lock (_gate)
            transfer = FindOwned(binding, id);
        if (transfer is not null)
            Cancel(transfer);
    }

    private Transfer? FindOwned(QuasarBrowserSession binding, string id) =>
        _transfers.TryGetValue(id, out var transfer) && transfer.Owner == binding.Owner && transfer.Session == binding.Id
            ? transfer : null;

    public void Remove(Transfer transfer)
    {
        bool removed;
        lock (_gate)
            removed = _transfers.Remove(transfer.Id);
        if (removed)
            Release(transfer, dispose: true);
    }

    public void Cancel(Transfer transfer)
    {
        bool claimed;
        lock (_gate)
        {
            if (!_transfers.ContainsKey(transfer.Id))
                return;
            claimed = transfer.Claimed;
            if (!claimed)
                _transfers.Remove(transfer.Id);
        }
        Release(transfer, dispose: !claimed);
    }

    /// <summary>
    /// Releases a lease after the registry lock is released. A claimed lease
    /// may already have been removed and disposed by its streaming request;
    /// that is a completed release, so a disposed source is treated as done.
    /// </summary>
    private static void Release(Transfer transfer, bool dispose)
    {
        try { transfer.Stop.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { if (dispose) transfer.Stop.Dispose(); }
    }

    public void Dispose()
    {
        Transfer[] transfers;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            transfers = _transfers.Values.ToArray();
            _transfers.Clear();
        }

        foreach (var transfer in transfers)
            Release(transfer, dispose: true);
    }

    public sealed class Transfer
    {
        internal Transfer(QuasarBrowserSession binding, string plugin, string operation, QuasarOperationKind kind, JsonElement request)
        {
            Binding = binding; Owner = binding.Owner; Session = binding.Id; Plugin = plugin; Operation = operation; Kind = kind; Request = request;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(binding.Revoked);
            Stop.CancelAfter(PendingLifetime);
            Token = Stop.Token;
        }
        public QuasarBrowserSession Binding { get; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Owner { get; }
        public string Session { get; }
        public string Plugin { get; }
        public string Operation { get; }
        public QuasarOperationKind Kind { get; }
        public JsonElement Request { get; }
        public bool Claimed { get; set; }
        // Keep bounded leases for idle transfers while a slow, authorized backup can make forward progress.
        internal void RecordProgress()
        {
            if (Token.IsCancellationRequested) return;
            try { Stop.CancelAfter(ActiveLifetime); }
            catch (ObjectDisposedException) when (Token.IsCancellationRequested) { }
        }
        internal CancellationTokenSource Stop { get; }
        public CancellationToken Token { get; }
    }
}
