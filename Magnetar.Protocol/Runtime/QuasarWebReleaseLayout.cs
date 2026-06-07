using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Magnetar.Protocol.Runtime;

public static class QuasarWebReleaseLayout
{
    public const string WorkerExecutableName = "Quasar";

    // Platform-specific worker executable file name. The self-contained single-file
    // worker is published as Quasar.exe on Windows and the extension-less Quasar on
    // Linux/macOS. Use this for filesystem lookups; WorkerExecutableName remains the
    // platform-neutral identifier.
    public static string WorkerExecutableFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Quasar.exe" : WorkerExecutableName;

    // Web payload files required in every release archive, independent of platform.
    public static readonly string[] RequiredWebAssetFiles =
    [
        "wwwroot/_framework/blazor.web.js",
        "wwwroot/_content/MudBlazor/MudBlazor.min.css",
        "wwwroot/_content/MudBlazor/MudBlazor.min.js",
        "wwwroot/app.css",
        "wwwroot/quasar-configs.js",
        "wwwroot/quasar-charts.js",
        "wwwroot/lib/uplot/uPlot.min.css",
        "wwwroot/lib/uplot/uPlot.iife.min.js",
    ];

    public static readonly string[] RequiredRelativeFiles =
    [
        WorkerExecutableName,
        .. RequiredWebAssetFiles,
    ];

    public static void ValidateDirectory(string directory)
    {
        var missing = RequiredWebAssetFiles
            .Prepend(WorkerExecutableFileName)
            .Where(relativePath => !File.Exists(Path.Combine(directory, relativePath)))
            .ToArray();

        if (missing.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Quasar web release at '{directory}' is missing required files: {string.Join(", ", missing)}");
    }
}
