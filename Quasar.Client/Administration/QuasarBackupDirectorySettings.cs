namespace Quasar.Services.Backup;

public sealed class QuasarBackupDirectorySettings
{
    public string ConfiguredDirectory { get; set; } = string.Empty;

    public string ResolvedDirectory { get; set; } = string.Empty;

    public string AppSettingsPath { get; set; } = string.Empty;

    public bool EnvironmentOverride { get; set; }

    public string EnvironmentDirectory { get; set; } = string.Empty;
}
