using System.Security.Cryptography;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
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
        Environment.SetEnvironmentVariable("QUASAR_INSTALL_DIR", data);
        MigrateLegacyRequest(root);

        // Protected original for the updater; discoverable copy for application/CLI.
        QuasarUpdateJournal.WriteJson(metadata, installation);
        if (Path.GetFullPath(metadata) != Path.Combine(root, QuasarServiceInstallation.FileName))
            QuasarUpdateJournal.WriteJson(Path.Combine(root, QuasarServiceInstallation.FileName), new { metadataPath = metadata });
        QuasarServiceInstallation.Load(metadata);
        return 0;
    }

    private static void MigrateLegacyRequest(string root)
    {
        var legacyRequest = MagnetarPaths.GetQuasarBootstrapUpdateRequestPath();
        if (!File.Exists(legacyRequest)) return;
        var operation = QuasarUpdateJournal.Read();
        if (operation?.Stage == UpdateStage.Restarting ||
            File.Exists(Path.Combine(MagnetarPaths.GetQuasarUpdatesDirectory(), "application-maintenance")) ||
            LauncherReplacement.Phase(root) is "installing" or "ready" or "attempted")
            throw new InvalidOperationException("An interrupted Quasar replacement needs recovery before migrating the legacy Bootstrap request.");

        var target = "unknown";
        var legacyOperationId = Guid.Empty;
        try
        {
            using var request = JsonDocument.Parse(File.ReadAllText(legacyRequest));
            if (request.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (request.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                    target = version.GetString() ?? target;
                if (request.RootElement.TryGetProperty("operationId", out var id) && id.ValueKind == JsonValueKind.String)
                    id.TryGetGuid(out legacyOperationId);
            }
        }
        catch (JsonException) { }
        // The legacy request can only abandon its own operation. Preserve an unrelated journal.
        if (operation is null || legacyOperationId != Guid.Empty && operation.Id == legacyOperationId &&
            operation.Stage is (UpdateStage.Queued or UpdateStage.Downloading or UpdateStage.Verified or UpdateStage.Ready))
            QuasarUpdateJournal.Write((operation ?? new QuasarUpdateOperation { Version = target }) with { Stage = UpdateStage.Abandoned,
                Message = "Legacy Bootstrap activation abandoned during native-service migration. It will not be replayed." });
        File.Move(legacyRequest, legacyRequest + ".migrated", overwrite: true);
    }
}
