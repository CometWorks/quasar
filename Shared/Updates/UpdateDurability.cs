using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Quasar.Updates;

internal static class UpdateDurability
{
    internal static void Publish(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            for (var attempt = 0; ; attempt++)
            {
                if (MoveFileEx(temporary, destination, 1 | 8)) break;
                var error = Marshal.GetLastWin32Error();
                if (error is not (32 or 33) || attempt >= 49) throw new IOException("Cannot durably publish update state.", new Win32Exception(error));
                Thread.Sleep(20);
            }
        }
        else
        {
            File.Move(temporary, destination, overwrite: true);
            SyncDirectory(Path.GetDirectoryName(destination)!);
        }
    }
    internal static void SyncTree(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) SyncDirectory(child);
        SyncDirectory(directory);
        SyncDirectory(Path.GetDirectoryName(directory)!);
    }
    internal static void SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        var descriptor = open(directory, 0x10000); // O_RDONLY | O_DIRECTORY
        if (descriptor < 0) throw new IOException("Cannot open update directory for durable publication.", new Win32Exception());
        try { if (fsync(descriptor) != 0) throw new IOException("The update filesystem cannot durably sync directory metadata.", new Win32Exception()); }
        finally { close(descriptor); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string destination, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc")] private static extern int close(int descriptor);
}
