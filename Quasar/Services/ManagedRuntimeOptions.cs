using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

public sealed class ManagedRuntimeOptions
{
    private const string DefaultMagnetarReleaseApiUrl = "https://api.github.com/repos/CometWorks/magnetar/releases/latest";
    private const string DefaultLinuxMagnetarArchiveAssetPattern = "MagnetarForLinux-*.7z";
    private const string DefaultWindowsMagnetarArchiveAssetPattern = "MagnetarForWindows-*.7z";
    private const string DefaultLinuxSteamCmdArchiveUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    private const string DefaultWindowsSteamCmdArchiveUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    public string MagnetarArchiveUrl { get; init; } = string.Empty;

    public string MagnetarReleaseApiUrl { get; init; } = DefaultMagnetarReleaseApiUrl;

    public string MagnetarArchiveAssetPattern { get; init; } = GetDefaultMagnetarArchiveAssetPattern();

    public string MagnetarInstallDirectory { get; init; } = MagnetarPaths.GetQuasarManagedMagnetarInstallDirectory();

    public string SteamCmdArchiveUrl { get; init; } = GetDefaultSteamCmdArchiveUrl();

    public string SteamCmdInstallDirectory { get; init; } = MagnetarPaths.GetQuasarManagedSteamCmdInstallDirectory();

    public string DedicatedServerInstallDirectory { get; init; } = MagnetarPaths.GetQuasarManagedDedicatedServerInstallDirectory();

    public string DedicatedServer64OverridePath { get; init; } = string.Empty;

    public string SteamCmdPath { get; init; } = string.Empty;

    public bool PreferManagedDedicatedServerInstall { get; init; } = true;

    public static ManagedRuntimeOptions Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("Quasar").GetSection("ManagedRuntime");

        var magnetarArchiveUrl = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_ARCHIVE_URL")
                                 ?? section["MagnetarArchiveUrl"]
                                 ?? string.Empty;

        var magnetarReleaseApiUrl = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_RELEASE_API_URL")
                                    ?? section["MagnetarReleaseApiUrl"];
        if (string.IsNullOrWhiteSpace(magnetarReleaseApiUrl))
            magnetarReleaseApiUrl = DefaultMagnetarReleaseApiUrl;

        var magnetarArchiveAssetPattern = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_ARCHIVE_ASSET_PATTERN")
                                          ?? section["MagnetarArchiveAssetPattern"];
        if (string.IsNullOrWhiteSpace(magnetarArchiveAssetPattern))
            magnetarArchiveAssetPattern = GetDefaultMagnetarArchiveAssetPattern();

        var magnetarInstallDirectory = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_INSTALL_DIR")
                                       ?? section["MagnetarInstallDirectory"];
        if (string.IsNullOrWhiteSpace(magnetarInstallDirectory))
            magnetarInstallDirectory = MagnetarPaths.GetQuasarManagedMagnetarInstallDirectory();

        var steamCmdArchiveUrl = Environment.GetEnvironmentVariable("QUASAR_STEAMCMD_ARCHIVE_URL")
                                 ?? section["SteamCmdArchiveUrl"];
        if (string.IsNullOrWhiteSpace(steamCmdArchiveUrl))
            steamCmdArchiveUrl = GetDefaultSteamCmdArchiveUrl();

        var steamCmdInstallDirectory = Environment.GetEnvironmentVariable("QUASAR_STEAMCMD_INSTALL_DIR")
                                      ?? section["SteamCmdInstallDirectory"];
        if (string.IsNullOrWhiteSpace(steamCmdInstallDirectory))
            steamCmdInstallDirectory = MagnetarPaths.GetQuasarManagedSteamCmdInstallDirectory();

        var dedicatedServerInstallDirectory = Environment.GetEnvironmentVariable("QUASAR_DS_INSTALL_DIR")
                                              ?? section["DedicatedServerInstallDirectory"];
        if (string.IsNullOrWhiteSpace(dedicatedServerInstallDirectory))
            dedicatedServerInstallDirectory = MagnetarPaths.GetQuasarManagedDedicatedServerInstallDirectory();

        var dedicatedServer64OverridePath = Environment.GetEnvironmentVariable("QUASAR_DS64_PATH")
                                            ?? Environment.GetEnvironmentVariable("DS64")
                                            ?? section["DedicatedServer64OverridePath"]
                                            ?? string.Empty;

        var steamCmdPath = Environment.GetEnvironmentVariable("QUASAR_STEAMCMD_PATH")
                           ?? section["SteamCmdPath"]
                           ?? string.Empty;

        var preferManagedDedicatedServerInstallValue = Environment.GetEnvironmentVariable("QUASAR_PREFER_MANAGED_DS")
                                                       ?? section["PreferManagedDedicatedServerInstall"]
                                                       ?? "true";
        if (!bool.TryParse(preferManagedDedicatedServerInstallValue, out var preferManagedDedicatedServerInstall))
            preferManagedDedicatedServerInstall = true;

        return new ManagedRuntimeOptions
        {
            MagnetarArchiveUrl = magnetarArchiveUrl.Trim(),
            MagnetarReleaseApiUrl = magnetarReleaseApiUrl.Trim(),
            MagnetarArchiveAssetPattern = magnetarArchiveAssetPattern.Trim(),
            MagnetarInstallDirectory = magnetarInstallDirectory.Trim(),
            SteamCmdArchiveUrl = steamCmdArchiveUrl.Trim(),
            SteamCmdInstallDirectory = steamCmdInstallDirectory.Trim(),
            DedicatedServerInstallDirectory = dedicatedServerInstallDirectory.Trim(),
            DedicatedServer64OverridePath = dedicatedServer64OverridePath.Trim(),
            SteamCmdPath = steamCmdPath.Trim(),
            PreferManagedDedicatedServerInstall = preferManagedDedicatedServerInstall,
        };
    }

    private static string GetDefaultMagnetarArchiveAssetPattern() =>
        OperatingSystem.IsWindows()
            ? DefaultWindowsMagnetarArchiveAssetPattern
            : DefaultLinuxMagnetarArchiveAssetPattern;

    private static string GetDefaultSteamCmdArchiveUrl() =>
        OperatingSystem.IsWindows()
            ? DefaultWindowsSteamCmdArchiveUrl
            : DefaultLinuxSteamCmdArchiveUrl;
}
