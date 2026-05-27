using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

public sealed class ManagedRuntimeOptions
{
    public string MagnetarArchiveUrl { get; init; } = "https://nas.ferenczi.eu/public.php/dav/files/q4godba6fXH6w74/?accept=zip";

    public string MagnetarInstallDirectory { get; init; } = MagnetarPaths.GetQuasarManagedMagnetarInstallDirectory();

    public string DedicatedServerInstallDirectory { get; init; } = MagnetarPaths.GetQuasarManagedDedicatedServerInstallDirectory();

    public string DedicatedServer64OverridePath { get; init; } = string.Empty;

    public string SteamCmdPath { get; init; } = string.Empty;

    public bool PreferManagedDedicatedServerInstall { get; init; } = true;

    public static ManagedRuntimeOptions Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("Quasar").GetSection("ManagedRuntime");

        var magnetarArchiveUrl = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_ARCHIVE_URL")
                                 ?? section["MagnetarArchiveUrl"]
                                 ?? "https://nas.ferenczi.eu/public.php/dav/files/q4godba6fXH6w74/?accept=zip";

        var magnetarInstallDirectory = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_INSTALL_DIR")
                                       ?? section["MagnetarInstallDirectory"];
        if (string.IsNullOrWhiteSpace(magnetarInstallDirectory))
            magnetarInstallDirectory = MagnetarPaths.GetQuasarManagedMagnetarInstallDirectory();

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
            MagnetarArchiveUrl = magnetarArchiveUrl,
            MagnetarInstallDirectory = magnetarInstallDirectory.Trim(),
            DedicatedServerInstallDirectory = dedicatedServerInstallDirectory.Trim(),
            DedicatedServer64OverridePath = dedicatedServer64OverridePath.Trim(),
            SteamCmdPath = steamCmdPath.Trim(),
            PreferManagedDedicatedServerInstall = preferManagedDedicatedServerInstall,
        };
    }
}
