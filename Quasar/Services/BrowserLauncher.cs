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
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }
}
