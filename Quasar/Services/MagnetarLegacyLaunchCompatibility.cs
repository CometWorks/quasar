using System.Diagnostics;

namespace Quasar.Services;

// =====================================================================================
// LEGACY-MAGNETAR-COMPAT (TEMPORARY)
//
// Magnetar 2.3.3.0, the first pulsar-based release, changed its launch surface:
//   * telemetry consent became one value-taking flag (-consent accept|deny) instead of
//     the bare -consent / -noconsent pair, and
//   * the GitHub token moved from the -github-token <pat> argument to the
//     PULSAR_GITHUB_TOKEN environment variable.
//
// Quasar always installs the latest Magnetar release, but a host can still be running an
// older build: offline, after a failed update fell back to the existing runtime, or with
// a custom executable path. Sending the new flags to such a build is harmful, because it
// reads "-consent deny" as a bare -consent and enables telemetry. This class tells the two
// generations apart so the preparer can emit the legacy forms for pre-2.3.3.0 builds.
//
// REMOVE in the first Quasar release of 2027: delete this file, then delete every block
// marked LEGACY-MAGNETAR-COMPAT in DedicatedServerRuntimePreparer,
// DedicatedServerSupervisor, the docs and Quasar.Tests, keeping only the Current path.
// =====================================================================================
public enum MagnetarLaunchArgumentStyle
{
    /// <summary>Magnetar 2.3.3.0 and later: <c>-consent accept|deny</c>, token via <c>PULSAR_GITHUB_TOKEN</c>.</summary>
    Current,

    /// <summary>Magnetar before 2.3.3.0: bare <c>-consent</c> / <c>-noconsent</c>, token via <c>-github-token</c>.</summary>
    Legacy,
}

internal static class MagnetarLegacyLaunchCompatibility
{
    // First Magnetar release with the new launch surface. Magnetar versions are
    // <Pulsar version>.<Magnetar build> from this release on; older releases were 1.x.
    internal static readonly Version FirstCurrentVersion = new(2, 3, 3, 0);

    private const string LegacyLinuxLauncherFileName = "MagnetarInterim";
    private const string CurrentLinuxLauncherFileName = "MagnetarInterim.bin";

    internal static MagnetarLaunchArgumentStyle Detect(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return MagnetarLaunchArgumentStyle.Current;

        // Linux: the launcher name alone identifies the generation. The old bundle ran the
        // extension-less apphost under Bin/; the new one ships MagnetarInterim.bin at the root.
        var fileName = Path.GetFileName(executablePath);
        if (string.Equals(fileName, LegacyLinuxLauncherFileName, StringComparison.OrdinalIgnoreCase))
            return MagnetarLaunchArgumentStyle.Legacy;
        if (string.Equals(fileName, CurrentLinuxLauncherFileName, StringComparison.OrdinalIgnoreCase))
            return MagnetarLaunchArgumentStyle.Current;

        // Windows: both generations ship MagnetarInterim.exe / MagnetarLegacy.exe, so fall
        // back to the version resource stamped into the executable (1.x before the change,
        // 2.3.3.0 and later after it). Unknown or unreadable versions get the current style.
        var version = TryReadFileVersion(executablePath);
        return version is not null && version < FirstCurrentVersion
            ? MagnetarLaunchArgumentStyle.Legacy
            : MagnetarLaunchArgumentStyle.Current;
    }

    private static Version? TryReadFileVersion(string path)
    {
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
