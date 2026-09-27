using System.Security.Cryptography;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

// The Gateway's Steam frontend loads Valve's steamclient.so from ~/.steam/sdk64 when the account has no
// Steam client. The cluster release cannot ship that file and a cluster machine usually has neither Steam
// nor SteamCMD, so Quasar sends the copy from its managed SteamCMD and the Host stages it here.
internal static class SteamClientLibrary
{
    private const long MaxBytes = 256L * 1024 * 1024;

    internal static string TargetPath(string home) => Path.Combine(home, ".steam", "sdk64", "steamclient.so");

    internal static async Task<HostContract.HostSteamClientLibrary> InstallAsync(string home, string sha256, Stream source, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            throw new InvalidOperationException("The Host account has no home directory for steamclient.so.");
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("steamclient.so SHA-256 is required.");
        string target = TargetPath(home);
        if (File.Exists(target) && Hash(target).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            return new(target, sha256.ToLowerInvariant(), Installed: false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Stage and rename, never write over the target: a running Gateway or server has the file
        // mapped, and truncating that inode kills the process with SIGBUS.
        string staging = target + "." + Environment.ProcessId + ".tmp";
        try
        {
            long total = 0;
            await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    if ((total += count) > MaxBytes) throw new InvalidDataException("steamclient.so exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                output.Flush(true);
            }
            if (total == 0 || !Hash(staging).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("steamclient.so upload checksum mismatch.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staging, target, overwrite: true);
            return new(target, sha256.ToLowerInvariant(), Installed: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
