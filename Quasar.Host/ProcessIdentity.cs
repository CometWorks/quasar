namespace Quasar.Host;

// Clock-independent identity of a launched process. The wall-clock start time moves when the
// clock is stepped, and a PID is reused after a reboot or a long uptime; the kernel boot ID
// plus the start time in ticks since boot does neither.
internal static class ProcessIdentity
{
    internal static string? Capture(int processId)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            string stat = File.ReadAllText($"/proc/{processId}/stat");
            // The command name may contain spaces and parentheses; fields resume after the last ')'.
            string[] fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return bootId.Length == 0 || fields.Length < 20 || !ulong.TryParse(fields[19], out ulong startTicks)
                ? null : bootId + "/" + startTicks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // True: same process. False: the PID now belongs to another process, so the recorded one is gone.
    // Null: unknown (record without identity, or not Linux); the caller falls back to the start time.
    internal static bool? Matches(string? recorded, int processId)
    {
        if (recorded is null || Capture(processId) is not { } current)
            return null;
        return current.Equals(recorded, StringComparison.Ordinal);
    }
}
