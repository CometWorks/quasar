using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Quasar.Diagnostics;

// Startup check of system limits which make Quasar or the servers it runs fail in
// ways that are hard to trace back to the cause when they are set too low.
// Copied from Pulsar's Shared/SystemLimits.cs, which Magnetar uses, keep them in step.
public static class SystemLimits
{
    // One warning for each limit below what we expect, empty if all is fine
    public static IEnumerable<string> Check()
    {
        if (OperatingSystem.IsWindows())
        {
            string? warning = CheckLongPaths();
            if (warning is not null)
                yield return warning;
        }

        // Linux limits (open files, inotify, processes) will go here
    }

    // MagnetarLegacy runs on .NET Framework and is longPathAware, which works only if
    // long paths are enabled. Quasar's own .NET code handles long paths without it.
    [SupportedOSPlatform("windows")]
    private static string? CheckLongPaths()
    {
        object? value;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem"
            );
            value = key?.GetValue("LongPathsEnabled");
        }
        catch (Exception e)
        {
            return $"Could not read the Windows long path setting: {e.Message}";
        }

        if (value is int enabled && enabled != 0)
            return null;

        return $"Windows long path support is off (LongPathsEnabled is {value ?? "not set"}, expected 1). "
            + "Paths of 260 or more characters will fail, deep save and plugin folders can reach that. "
            + "To turn it on, enable \"Enable Win32 long paths\" in the Local Group Policy Editor "
            + "or run this as administrator, then restart: "
            + @"reg add HKLM\SYSTEM\CurrentControlSet\Control\FileSystem /v LongPathsEnabled /t REG_DWORD /d 1 /f";
    }
}
