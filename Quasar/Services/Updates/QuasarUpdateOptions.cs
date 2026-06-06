namespace Quasar.Services.Updates;

public sealed class QuasarUpdateOptions
{
    public bool Enabled { get; init; } = true;

    public string Owner { get; init; } = "viktor-ferenczi";

    public string Repository { get; init; } = "Quasar";

    public bool IncludePrerelease { get; init; }

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(5);

    public string LinuxWebAssetName { get; init; } = "quasar-web-linux-x64.tar.gz";

    public string LinuxBootstrapAssetName { get; init; } = "quasar-bootstrap-linux-x64.tar.gz";

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

        var intervalValue = Environment.GetEnvironmentVariable("QUASAR_UPDATES_CHECK_INTERVAL_SECONDS")
                            ?? section["CheckIntervalSeconds"];
        if (!int.TryParse(intervalValue, out var intervalSeconds) || intervalSeconds < 60)
            intervalSeconds = 300;

        return new QuasarUpdateOptions
        {
            Enabled = enabled,
            Owner = Environment.GetEnvironmentVariable("QUASAR_UPDATES_OWNER")
                    ?? section["Owner"]
                    ?? "viktor-ferenczi",
            Repository = Environment.GetEnvironmentVariable("QUASAR_UPDATES_REPOSITORY")
                         ?? section["Repository"]
                         ?? "Quasar",
            IncludePrerelease = includePrerelease,
            CheckInterval = TimeSpan.FromSeconds(intervalSeconds),
            LinuxWebAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_LINUX_WEB_ASSET")
                                ?? section["LinuxWebAssetName"]
                                ?? "quasar-web-linux-x64.tar.gz",
            LinuxBootstrapAssetName = Environment.GetEnvironmentVariable("QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET")
                                       ?? section["LinuxBootstrapAssetName"]
                                       ?? "quasar-bootstrap-linux-x64.tar.gz",
        };
    }
}
