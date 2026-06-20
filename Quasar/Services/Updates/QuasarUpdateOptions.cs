namespace Quasar.Services.Updates;

public sealed class QuasarUpdateOptions
{
    public bool Enabled { get; init; } = true;

    public string Owner { get; init; } = "CometWorks";

    public string Repository { get; init; } = "quasar";

    public bool IncludePrerelease { get; set; }

    public bool AutoStageWebUpdates { get; set; } = true;

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    public string LinuxWebAssetName { get; init; } = "quasar-web-linux-x64.tar.gz";

    public string LinuxBootstrapAssetName { get; init; } = "quasar-installer-linux.tar.gz";

    public string WindowsWebAssetName { get; init; } = "quasar-web-win-x64.zip";

    public string WindowsBootstrapAssetName { get; init; } = "quasar-installer-windows.zip";

    // Asset names resolved for the current operating system. Windows uses the .zip
    // assets; every other platform keeps the Linux .tar.gz assets.
    public string WebAssetName => OperatingSystem.IsWindows() ? WindowsWebAssetName : LinuxWebAssetName;

    public string BootstrapAssetName => OperatingSystem.IsWindows() ? WindowsBootstrapAssetName : LinuxBootstrapAssetName;

    public static QuasarUpdateOptions Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("Quasar").GetSection("Updates");

        var enabledValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_ENABLED")
                           ?? section["Enabled"]
                           ?? "true";
        if (!bool.TryParse(enabledValue, out var enabled))
            enabled = true;

        var includePrereleaseValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_INCLUDE_PRERELEASE")
                                     ?? section["IncludePrerelease"]
                                     ?? "false";
        if (!bool.TryParse(includePrereleaseValue, out var includePrerelease))
            includePrerelease = false;

        var autoStageValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_AUTO_STAGE_WEB")
                             ?? section["AutoStageWebUpdates"]
                             ?? "true";
        if (!bool.TryParse(autoStageValue, out var autoStageWebUpdates))
            autoStageWebUpdates = true;

        var intervalValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_CHECK_INTERVAL_SECONDS")
                            ?? section["CheckIntervalSeconds"];
        if (!int.TryParse(intervalValue, out var intervalSeconds) || intervalSeconds < 60)
            intervalSeconds = 900;

        return new QuasarUpdateOptions
        {
            Enabled = enabled,
            Owner = Environment.GetEnvironmentVariable("QUASAR_UPDATES_OWNER")
                    ?? section["Owner"]
                    ?? "CometWorks",
            Repository = Environment.GetEnvironmentVariable("QUASAR_UPDATES_REPOSITORY")
                         ?? section["Repository"]
                         ?? "quasar",
            IncludePrerelease = includePrerelease,
            AutoStageWebUpdates = autoStageWebUpdates,
            CheckInterval = TimeSpan.FromSeconds(intervalSeconds),
            LinuxWebAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_LINUX_WEB_ASSET")
                                ?? section["LinuxWebAssetName"]
                                ?? "quasar-web-linux-x64.tar.gz",
            LinuxBootstrapAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET")
                                       ?? section["LinuxBootstrapAssetName"]
                                       ?? "quasar-installer-linux.tar.gz",
            WindowsWebAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_WINDOWS_WEB_ASSET")
                                  ?? section["WindowsWebAssetName"]
                                  ?? "quasar-web-win-x64.zip",
            WindowsBootstrapAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_WINDOWS_BOOTSTRAP_ASSET")
                                        ?? section["WindowsBootstrapAssetName"]
                                        ?? "quasar-installer-windows.zip",
        };
    }
}
