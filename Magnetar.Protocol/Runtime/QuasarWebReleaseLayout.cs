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

    // Historical baseline: files every complete package has carried since before
    // the native dialog/toast release. Keep this list stable so older complete
    // archives remain valid rollback targets.
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

    // Native dialog/toast and plugin-host assets added in the native UI release.
    // A package opts in by declaring "webUi": 1 in release.json. Packages without
    // the marker predate these assets and are validated against the baseline only.
    public static readonly string[] NativeUiWebAssetFiles =
    [
        "wwwroot/quasar-sdk.js",
        "wwwroot/quasar-push.js",
        "wwwroot/quasar-ui.css",
        "wwwroot/quasar-ui.js",
        "wwwroot/ui-plugins.css",
        "wwwroot/_quasar/sdk/v1/boot.js",
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
