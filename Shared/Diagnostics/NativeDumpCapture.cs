using System.Diagnostics;
using System.Runtime.InteropServices;
using CometWorks.Diagnostics;

namespace Quasar.Diagnostics;

/// <summary>Imports completed OS cores for an exact process/run; no global kernel settings are changed.</summary>
internal static class NativeDumpCapture
{
    private static readonly string? Tool = FindTool();
    internal static string Capability => OperatingSystem.IsLinux()
        ? Tool is null ? "systemd-coredump-unavailable" : "systemd-coredump"
        : OperatingSystem.IsWindows() ? "windows-debugger" : "os-collector-required";

    [StructLayout(LayoutKind.Sequential)] private struct RLimit { public ulong Current; public ulong Maximum; }
    [DllImport("libc", SetLastError = true)] private static extern int getrlimit(int resource, out RLimit limit);
    [DllImport("libc", SetLastError = true)] private static extern int setrlimit(int resource, in RLimit limit);
    internal static void EnableInheritedCoreLimit()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (getrlimit(4, out var limit) == 0) { limit.Current = limit.Maximum; _ = setrlimit(4, in limit); }
    }

    internal static async Task ImportAsync(string directory, int? pid, string executable,
        DateTimeOffset started, DateTimeOffset ended, string? kernelIdentity, CancellationToken token)
    {
        if (File.Exists(Path.Combine(directory, "native.dmp")) || File.Exists(Path.Combine(directory, "core-unavailable"))) return;
        if (OperatingSystem.IsWindows())
        { Unavailable(directory, "No Windows dump was captured; the Host must attach at launch with a current dump grant."); return; }
        if (Tool is null || pid is null || kernelIdentity is null || !OperatingSystem.IsLinux())
        { Unavailable(directory, "Exact OS process identity or core collector is unavailable."); return; }
        string bootId = kernelIdentity.Split('/')[0].Replace("-", "", StringComparison.Ordinal);
        if (bootId.Length != 32 || !bootId.All(Uri.IsHexDigit))
        { Unavailable(directory, "OS boot identity is invalid."); return; }
        string temp = Path.Combine(directory, "native-core.tmp");
        if (DiagnosticStorage.AvailableFreeSpace(directory) < DiagnosticsProtocol.MaxArchiveBytes + (1L << 30))
        { Unavailable(directory, "Insufficient free disk space for an OS core."); return; }
        var start = new ProcessStartInfo(Tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // Kernel timestamp + boot ID + executable excludes PID reuse, unrelated users and older cores.
        foreach (string argument in new[] { "--no-pager", "--quiet", "--since", started.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff UTC"),
            "--until", ended.UtcDateTime.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss.ffffff UTC"), "dump",
            "_BOOT_ID=" + bootId, "COREDUMP_PID=" + pid, "COREDUMP_EXE=" + executable }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(start)!;
        var stdout = CopyCoreAsync(process.StandardOutput.BaseStream, temp, deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            Task exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(stdout, exited) == stdout) await stdout;
            await exited;
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, Path.Combine(directory, "native.dmp"));
            }
            // Missing journal entries may arrive later. Retry for two minutes, then retain an explicit diagnostic status.
            else if (DateTimeOffset.UtcNow - ended > TimeSpan.FromMinutes(2))
                Unavailable(directory, "OS core unavailable or not readable by the Host service account.");
        }
        catch (InvalidDataException) { Unavailable(directory, "OS core exceeds the archive size limit."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { Unavailable(directory, "OS core import timed out."); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        { Unavailable(directory, "OS core import failed."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(stdout, stderr); } catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { }
            File.Delete(temp);
        }
    }

    private static void Unavailable(string directory, string reason) => File.WriteAllText(Path.Combine(directory, "core-unavailable"), reason);
    private static async Task CopyCoreAsync(Stream source, string path, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total >= DiagnosticsProtocol.MaxArchiveBytes) throw new InvalidDataException("Core too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private static string? FindTool() => new[] { "/usr/bin/coredumpctl", "/bin/coredumpctl" }.FirstOrDefault(File.Exists);
}
