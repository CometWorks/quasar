using System.Diagnostics;
using Quasar.Models;

namespace Quasar.Services;

// Magnetar 2.4.3.11 deletes old SpaceEngineersDedicated_*.log files at start by age and total
// size (-logMaxAgeDays, -logMaxSizeMB). Older builds forward unknown options to Pulsar and the
// DS, so the flags go only to builds known to support them.
internal static class MagnetarLogRetention
{
    internal static readonly Version FirstSupportedVersion = new(2, 4, 3, 11);

    internal static bool IsSupported(Version? magnetarVersion) =>
        magnetarVersion is not null && magnetarVersion >= FirstSupportedVersion;

    // Only values that differ from Magnetar's own defaults need a flag.
    internal static IEnumerable<string> BuildArguments(DedicatedServerDefinition definition, Version? magnetarVersion)
    {
        if (!IsSupported(magnetarVersion))
            yield break;
        if (definition.LogMaxAgeDays != DedicatedServerDefinition.DefaultLogMaxAgeDays)
            yield return $"-logMaxAgeDays {definition.LogMaxAgeDays}";
        if (definition.LogMaxSizeMB != DedicatedServerDefinition.DefaultLogMaxSizeMB)
            yield return $"-logMaxSizeMB {definition.LogMaxSizeMB}";
    }

    // The Windows launchers carry the version resource themselves. The Linux launcher
    // (MagnetarInterim.bin, or the extension-less pre-2.3.3.0 apphost) is native, so the
    // version comes from the managed assembly beside it.
    internal static Version? TryReadMagnetarVersion(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var extension = Path.GetExtension(executablePath);
        var path = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? executablePath
            : Path.ChangeExtension(executablePath, ".dll");
        if (!File.Exists(path))
            return null;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = new Version(
                Math.Max(info.FileMajorPart, 0),
                Math.Max(info.FileMinorPart, 0),
                Math.Max(info.FileBuildPart, 0),
                Math.Max(info.FilePrivatePart, 0));
            return version == new Version(0, 0, 0, 0) ? null : version;
        }
        catch
        {
            return null;
        }
    }
}
