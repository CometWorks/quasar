using System.Text.Json;
using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

// Written by the installer in the protected application directory. Update intents
// cannot choose service names, accounts, installation roots or updater executables.
public sealed record QuasarServiceInstallation
{
    public string MetadataPath { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string DataDirectory { get; init; } = "";
    public string MainService { get; init; } = "Quasar";
    public string UpdaterService { get; init; } = "QuasarUpdater";
    public string Account { get; init; } = "";
    public string UpdaterAccount { get; init; } = "";
    public bool UserService { get; init; }
    public string HealthUrl { get; init; } = "http://127.0.0.1:8080";
    public string ReleaseOwner { get; init; } = "CometWorks";
    public string ReleaseRepository { get; init; } = "quasar";
    public string PackageAsset { get; init; } = OperatingSystem.IsWindows() ? "quasar-win-x64.zip" : "quasar-linux-x64.tar.gz";
    public string HealthToken { get; init; } = "";
    public static string FileName => "service-installation.json";
    public static QuasarServiceInstallation? Current { get; private set; }
    public string Executable => Path.Combine(InstallDirectory, OperatingSystem.IsWindows() ? "Quasar.exe" : "Quasar");

    public static bool IsLocalAddress(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return System.Net.IPAddress.IsLoopback(address) || System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses).Any(value => value.Address.Equals(address));
    }

    public static QuasarServiceInstallation? Load(string? path = null, bool applyEnvironment = false)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        using (var reference = JsonDocument.Parse(text))
        {
            if (!reference.RootElement.TryGetProperty("installDirectory", out _) && reference.RootElement.TryGetProperty("metadataPath", out var target))
            {
                var protectedPath = Path.GetFullPath(target.GetString() ?? throw new InvalidDataException("Missing protected metadata path."));
                if (protectedPath == Path.GetFullPath(path)) throw new InvalidDataException("Installation metadata refers to itself.");
                // One level only: the protected file must contain the actual record.
                path = protectedPath;
                text = File.ReadAllText(protectedPath);
            }
        }
        var value = JsonSerializer.Deserialize<QuasarServiceInstallation>(text, QuasarUpdateJournal.JsonOptions)
            ?? throw new InvalidDataException("Invalid service installation metadata.");
        if (!Path.IsPathRooted(value.InstallDirectory) || !Path.IsPathRooted(value.DataDirectory) ||
            !Path.IsPathRooted(value.MetadataPath) ||
            (Path.GetFullPath(path) != Path.GetFullPath(value.MetadataPath) && Path.GetFullPath(path) != Path.Combine(value.InstallDirectory, FileName)) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value.MainService, "^[A-Za-z0-9_.@-]+$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(value.UpdaterService, "^[A-Za-z0-9_.@-]+$") || value.MainService == value.UpdaterService ||
            !System.Text.RegularExpressions.Regex.IsMatch(value.ReleaseOwner, "^[A-Za-z0-9_.-]+$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(value.ReleaseRepository, "^[A-Za-z0-9_.-]+$") || Path.GetFileName(value.PackageAsset) != value.PackageAsset || value.PackageAsset.Contains('\\') ||
            !Uri.TryCreate(value.HealthUrl, UriKind.Absolute, out var uri) || (!uri.IsLoopback && (!System.Net.IPAddress.TryParse(uri.Host, out var healthAddress) || !IsLocalAddress(healthAddress))) || uri.Scheme is not ("http" or "https") || value.HealthToken.Length < 32)
            throw new InvalidDataException("Unsafe service installation metadata. Repair this installation using its installer.");
        if (applyEnvironment)
        {
            Current = value;
            Environment.SetEnvironmentVariable("QUASAR_INSTALL_DIR", value.DataDirectory);
            Environment.SetEnvironmentVariable("QUASAR_LAUNCHER_TOKEN", value.HealthToken);
            Environment.SetEnvironmentVariable("QUASAR_MODE", "service");
            Environment.SetEnvironmentVariable("QUASAR_OPEN_BROWSER_ON_START", "false");
        }
        return value;
    }
}
