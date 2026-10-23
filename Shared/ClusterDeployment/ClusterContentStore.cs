using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Quasar.Host.Contract.V1;

namespace Quasar.ClusterDeployment;

// Snapshots are manifests and hard-link references, never private copies of payloads.
// Only immutable input trees may use this store. Mutable worlds/runtimes must copy.
internal sealed class ClusterContentStore : IDisposable
{
    // Set once by each service. Offline commands/tests use a store beside their inputs.
    internal static string? RootDirectory { get; set; }
    private readonly string root;
    private readonly FileStream gate;
    private readonly HashSet<string> verified = new(StringComparer.Ordinal);

    private ClusterContentStore(string root, FileStream gate) => (this.root, this.gate) = (root, gate);

    internal static async Task<ClusterContentStore> OpenAsync(string snapshots, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cluster content storage requires Linux.");
        snapshots = Path.GetFullPath(snapshots);
        Directory.CreateDirectory(snapshots);
        ClusterWorldFiles.Private(snapshots);
        string storage = RootDirectory ?? snapshots;
        Directory.CreateDirectory(storage);
        // Hard links cannot cross devices. A separate volume owns its own objects.
        if (Device(storage) != Device(snapshots)) storage = snapshots;
        string root = Path.Combine(Path.GetFullPath(storage), ".objects");
        Directory.CreateDirectory(root);
        ClusterWorldFiles.Private(root);
        string lockFile = Path.Combine(root, ".lock");
        if (File.Exists(lockFile)) RefuseLink(lockFile);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(root, new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(100, token); }
        }
    }

    internal async Task ReferenceAsync(string source, string destination, DeploymentFile pin, CancellationToken token)
    {
        if (pin.Bytes < 0 || pin.Sha256.Length != 64 || pin.Sha256 != pin.Sha256.ToLowerInvariant()
            || !pin.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid content object pin.");
        string key = pin.Sha256 + (pin.Executable ? "-x" : "-r");
        string path = Path.Combine(root, key);
        if (!File.Exists(path))
        {
            RefuseLink(source);
            string temporary = Path.Combine(root, ".write-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(source, temporary);
                await VerifyAsync(temporary, pin, token);
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    | (pin.Executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
                File.Move(temporary, path);
                verified.Add(key);
            }
            finally { File.Delete(temporary); }
        }
        else if (verified.Add(key)) await VerifyAsync(path, pin, token);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ClusterWorldFiles.Private(Path.GetDirectoryName(destination)!);
        if (Link(path, destination) != 0) throw new IOException("Cannot create content reference.", new Win32Exception(Marshal.GetLastPInvokeError()));
        ClusterStorageActivity.MarkChanged();
    }

    // Used for legacy snapshots and newly received files. Publish each replacement
    // atomically; interruption leaves either the old verified file or its reference.
    internal async Task AdoptAsync(string path, DeploymentFile pin, CancellationToken token)
    {
        await VerifyAsync(path, pin, token);
        string temporary = path + ".reference-" + Guid.NewGuid().ToString("N");
        try
        {
            await ReferenceAsync(path, temporary, pin, token);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    internal void Prune()
    {
        foreach (string path in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith(".write-", StringComparison.Ordinal)) { File.Delete(path); continue; }
            if (name.Length != 66 || name[64] != '-' || name[65] is not ('r' or 'x')
                || !name[..64].All(Uri.IsHexDigit)) continue;
            RefuseLink(path);
            if (ReferenceCount(path) == 1) File.Delete(path);
        }
    }

    // #1397: hard-links a read-only file (a store object, or a configuration file that references one) instead of
    // copying it, for payloads a process only reads, such as every node's mods (1.7 GB per process otherwise).
    // False when it cannot: not Linux, a writable or symbolic-link source, another device, the link limit.
    internal static bool TryLinkReadOnly(string source, string destination)
    {
        if (!OperatingSystem.IsLinux() || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0
            || (File.GetUnixFileMode(source) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            return false;
        return Link(source, destination) == 0;
    }

    internal static uint ReferenceCount(string path)
    {
        if (Statx(-100, path, 0x100, 4, out var data) != 0 || (data.Mask & 4) == 0)
            throw new IOException("Cannot inspect content references.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return data.Links;
    }

    private static (uint Major, uint Minor) Device(string path)
    {
        if (Statx(-100, path, 0x100, 0x7ff, out var data) != 0)
            throw new IOException("Cannot inspect content filesystem.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return (data.DeviceMajor, data.DeviceMinor);
    }

    private static async Task VerifyAsync(string path, DeploymentFile pin, CancellationToken token)
    {
        RefuseLink(path);
        await using var file = File.OpenRead(path);
        if (file.Length != pin.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant() != pin.Sha256)
            throw new InvalidDataException("Content object does not match its verified pin.");
    }

    private static void RefuseLink(string path)
    {
        for (string? parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Content storage must not contain symbolic links.");
    }

    public void Dispose() { try { Prune(); } finally { gate.Dispose(); } }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxData data);
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
}
