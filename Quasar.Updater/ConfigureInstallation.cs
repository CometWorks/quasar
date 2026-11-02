using System.Security.Cryptography;
using System.Text.Json;
using Quasar.Updates;

internal static class ConfigureInstallation
{
    internal static int Run(string[] args)
    {
        string Option(string name, string? fallback = null)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException("Missing " + name);
        }
        var root = Path.GetFullPath(Option("--install-dir"));
        var data = Path.GetFullPath(Option("--data-dir", root));
        var metadata = Path.GetFullPath(Option("--metadata", Path.Combine(root, QuasarServiceInstallation.FileName)));
        var previous = File.Exists(metadata) ? QuasarServiceInstallation.Load(metadata) : null;
        var port = Environment.GetEnvironmentVariable("QUASAR_WEB_PORT") ?? "8080";
        var releaseOwner = previous?.ReleaseOwner ?? "CometWorks";
        var releaseRepository = previous?.ReleaseRepository ?? "quasar";
        var packageAsset = OperatingSystem.IsWindows() ? "quasar-win-x64.zip" : "quasar-linux-x64.tar.gz";
        var healthHost = Environment.GetEnvironmentVariable("QUASAR_WEB_HOST") ?? "127.0.0.1";
        var settings = Path.Combine(data, "appsettings.json");
        if (File.Exists(settings))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (json.RootElement.TryGetProperty("Quasar", out var quasar))
            {
                if (quasar.TryGetProperty("Port", out var configured)) port = configured.ToString();
                if (quasar.TryGetProperty("Host", out var host)) healthHost = host.GetString() ?? healthHost;
                if (quasar.TryGetProperty("Updates", out var updates))
                {
                    if (updates.TryGetProperty("Owner", out var owner)) releaseOwner = owner.GetString() ?? releaseOwner;
                    if (updates.TryGetProperty("Repository", out var repository)) releaseRepository = repository.GetString() ?? releaseRepository;
                    if (updates.TryGetProperty(OperatingSystem.IsWindows() ? "WindowsPackageAssetName" : "LinuxPackageAssetName", out var asset)) packageAsset = asset.GetString() ?? packageAsset;
                }
            }
        }
        if (healthHost is "0.0.0.0" or "*" or "+" or "::") healthHost = "127.0.0.1";
        var healthUrl = new UriBuilder("http", healthHost, int.Parse(port)).Uri.AbsoluteUri.TrimEnd('/');
        var installation = new QuasarServiceInstallation { InstallDirectory = root, DataDirectory = data, MetadataPath = metadata,
            MainService = Option("--main-service"), UpdaterService = Option("--updater-service"),
            Account = Option("--account"), UpdaterAccount = Option("--updater-account"), UserService = args.Contains("--user-service"),
            ReleaseOwner = releaseOwner, ReleaseRepository = releaseRepository, PackageAsset = packageAsset,
            HealthUrl = Option("--health-url", healthUrl),
            HealthToken = previous?.HealthToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        // Protected original for the updater; discoverable copy for application/CLI.
        QuasarUpdateJournal.WriteJson(metadata, installation);
        if (Path.GetFullPath(metadata) != Path.Combine(root, QuasarServiceInstallation.FileName))
            QuasarUpdateJournal.WriteJson(Path.Combine(root, QuasarServiceInstallation.FileName), new { metadataPath = metadata });
        QuasarServiceInstallation.Load(metadata);
        Environment.SetEnvironmentVariable("QUASAR_INSTALL_DIR", data);
        var legacyRequest = Path.Combine(data, "Updates", "bootstrap-update-request.json");
        if (File.Exists(legacyRequest))
        {
            var target = "unknown";
            try { using var request = JsonDocument.Parse(File.ReadAllText(legacyRequest)); if (request.RootElement.TryGetProperty("version", out var version)) target = version.GetString() ?? target; }
            catch (JsonException) { }
            QuasarUpdateJournal.Write(new QuasarUpdateOperation { Version = target, Stage = UpdateStage.Abandoned,
                Message = "Legacy Bootstrap activation abandoned during native-service migration. It will not be replayed." });
            File.Move(legacyRequest, legacyRequest + ".migrated", overwrite: true);
        }
        return 0;
    }
}
