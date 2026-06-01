using System.Diagnostics;

namespace Quasar.Services;

public static class BrowserLauncher
{
    public static bool ShouldOpenBrowser(WebServiceOptions options)
    {
        if (options.IsServiceMode || !options.OpenBrowserOnStart)
            return false;

        if (OperatingSystem.IsLinux())
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
                   || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        }

        return Environment.UserInteractive;
    }

    public static void TryOpen(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (TryStartBrowserCommand("xdg-open", url) ||
                    TryStartBrowserCommand("gio", $"open \"{url}\"") ||
                    TryStartBrowserCommand("sensible-browser", url))
                {
                    return;
                }
            }

            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    private static bool TryStartBrowserCommand(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
