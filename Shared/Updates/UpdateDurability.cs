#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Quasar.Updates;

/// <summary>
/// Durable file replacement. Also linked into Quasar.Agent, which is built for netstandard2.0 and runs in both
/// Magnetar builds, .NET 10 and .NET Framework 4.8, so it uses only APIs both have.
/// </summary>
internal static class UpdateDurability
{
    /// <param name="directorySyncFailed">
    /// Outside Windows the folder is synced after the rename. Without a handler a failed sync throws. With one, the
    /// published file, complete and in place by then, is kept and the handler gets the failure.
    /// </param>
    internal static void Publish(string temporary, string destination, Action<IOException>? directorySyncFailed = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            for (var attempt = 0; ; attempt++)
            {
                if (MoveFileEx(temporary, destination, 1 | 8)) break;
                var error = Marshal.GetLastWin32Error();
                if (error is not (32 or 33) || attempt >= 49) throw new IOException($"Cannot durably replace {destination}.", new Win32Exception(error));
                Thread.Sleep(20);
            }
        }
        else
        {
#if NETCOREAPP3_0_OR_GREATER
            File.Move(temporary, destination, overwrite: true);
#else
            // File.Move cannot overwrite before .NET Core 3.0; File.Replace is the same single rename here.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
#endif
            if (directorySyncFailed is null)
                SyncDirectory(Path.GetDirectoryName(destination)!);
            else
            {
                try { SyncDirectory(Path.GetDirectoryName(destination)!); }
                catch (IOException error) { directorySyncFailed(error); }
            }
        }
    }
    internal static void SyncTree(string directory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) SyncDirectory(child);
        SyncDirectory(directory);
        SyncDirectory(Path.GetDirectoryName(directory)!);
    }
    internal static void SyncDirectory(string directory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        var descriptor = open(directory, 0x10000); // O_RDONLY | O_DIRECTORY
        if (descriptor < 0) { var error = new Win32Exception(); throw new IOException($"Cannot open {directory} to sync it: {error.Message}", error); }
        try { if (fsync(descriptor) != 0) { var error = new Win32Exception(); throw new IOException($"The filesystem cannot durably sync {directory}: {error.Message}", error); } }
        finally { close(descriptor); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string destination, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc")] private static extern int close(int descriptor);
}
