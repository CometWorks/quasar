using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

// OS-owned lease: a crashed worker cannot leave a permanent maintenance marker.
// Bootstrap waits before retiring a worker that is still publishing a UI plugin.
internal static class QuasarMaintenanceLease
{
    public static async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(MagnetarPaths.GetQuasarDirectory(), "maintenance.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(200, cancellationToken); }
        }
    }
}
