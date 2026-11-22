namespace Quasar.Services.Plugins;

public sealed record QuasarDotNetSdkInstallAvailability(
    bool CanInstall,
    string Message,
    string InstallDirectory);

public sealed record QuasarDotNetSdkInstallResult(
    bool Succeeded,
    string Message,
    string Output);

public sealed record QuasarDotNetSdkStatus(
    int RequiredMajorVersion,
    string PinnedManagedSdkVersion,
    QuasarDotNetSdkSource Source,
    bool DotNetOnPath,
    bool RequiredSdkAvailable,
    IReadOnlyList<string> InstalledSdkVersions,
    string DotNetExecutablePath,
    string ManagedInstallDirectory,
    string Message)
{
    public bool CanBuildUiPlugins => RequiredSdkAvailable && !string.IsNullOrWhiteSpace(DotNetExecutablePath);
}

public enum QuasarDotNetSdkSource
{
    None,
    Global,
    Managed,
}
