using System.Security.Cryptography;
using System.Text;
using Quasar.Services.Plugins;

namespace Quasar.Services.Auth;

// A waiter keeps the generation it observed, including across the gap between boot and subscribing.
public sealed class DashboardAccessChanges : IDisposable
{
    public sealed class Generation
    {
        private readonly Guid _id = Guid.NewGuid();
        internal TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Bind reconnects to the sign-in without publishing the SDK session identifier.
        public string Stamp(QuasarBrowserSession session) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{_id:N}/{session.Id}")));
    }

    private Generation _current = new();
    public Generation Current => Volatile.Read(ref _current);
    private readonly RbacConfigCatalog _rbac;
    private readonly QuasarAuthSettingsService _auth;

    public DashboardAccessChanges(RbacConfigCatalog rbac, QuasarAuthSettingsService auth)
    {
        _rbac = rbac;
        _auth = auth;
        _rbac.Changed += Invalidate;
        _auth.Changed += Invalidate;
    }

    private void Invalidate() => Interlocked.Exchange(ref _current, new()).Changed.TrySetResult();

    public void Dispose()
    {
        _rbac.Changed -= Invalidate;
        _auth.Changed -= Invalidate;
    }
}
