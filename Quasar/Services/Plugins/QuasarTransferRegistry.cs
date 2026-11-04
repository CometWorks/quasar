using System.Text.Json;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

/// <summary>Bounded, single-use transfer leases. No file or socket is opened until a lease is claimed.</summary>
public sealed class QuasarTransferRegistry(QuasarBrowserSessions sessions) : IDisposable
{
    public const int PerSessionLimit = 4;
    public const int PerUserLimit = 16;
    public const int TotalLimit = 64;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ActiveLifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);

    public Transfer Create(QuasarBrowserSession binding, string plugin, string operation, QuasarOperationKind kind, JsonElement request)
    {
        sessions.Verify(binding);
        var owner = binding.Owner;
        var session = binding.Id;
        lock (_gate)
        {
            foreach (var stale in _transfers.Values.Where(item => !item.Claimed && item.Token.IsCancellationRequested).ToArray()) Remove(stale);
            if (_transfers.Count >= TotalLimit || _transfers.Values.Count(item => item.Session == session) >= PerSessionLimit
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
        var session = binding.Id;
        var owner = binding.Owner;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(id, out var transfer) || transfer.Owner != owner || transfer.Session != session || transfer.Token.IsCancellationRequested)
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

    public void Remove(Transfer transfer)
    {
        lock (_gate)
            if (_transfers.Remove(transfer.Id)) { transfer.Stop.Cancel(); transfer.Stop.Dispose(); }
    }

    public void Cancel(Transfer transfer)
    {
        lock (_gate)
        {
            if (!_transfers.ContainsKey(transfer.Id)) return;
            if (transfer.Claimed) transfer.Stop.Cancel();
            else Remove(transfer);
        }
    }

    public void Dispose()
    {
        lock (_gate) foreach (var transfer in _transfers.Values.ToArray()) Remove(transfer);
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
        internal CancellationTokenSource Stop { get; }
        public CancellationToken Token { get; }
    }
}
