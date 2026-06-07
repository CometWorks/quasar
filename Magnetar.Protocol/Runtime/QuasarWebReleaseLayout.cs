using System;
using System.IO;
using System.Linq;

namespace Magnetar.Protocol.Runtime;

public static class QuasarWebReleaseLayout
{
    public const string WorkerExecutableName = "Quasar";

    public static readonly string[] RequiredRelativeFiles =
    [
        WorkerExecutableName,
        "wwwroot/_framework/blazor.web.js",
        "wwwroot/_content/MudBlazor/MudBlazor.min.css",
        "wwwroot/_content/MudBlazor/MudBlazor.min.js",
        "wwwroot/app.css",
        "wwwroot/quasar-configs.js",
        "wwwroot/quasar-charts.js",
        "wwwroot/lib/uplot/uPlot.min.css",
        "wwwroot/lib/uplot/uPlot.iife.min.js",
    ];

    public static void ValidateDirectory(string directory)
    {
        var missing = RequiredRelativeFiles
            .Where(relativePath => !File.Exists(Path.Combine(directory, relativePath)))
            .ToArray();

        if (missing.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Quasar web release at '{directory}' is missing required files: {string.Join(", ", missing)}");
    }
}
