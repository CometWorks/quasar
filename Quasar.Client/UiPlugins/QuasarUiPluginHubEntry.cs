namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginHubEntry
{
    public string CatalogId { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string RepositoryUrl => GetRepositoryUrl(RepoId);

    public static string GetRepositoryUrl(string sourceRepo)
    {
        var repo = sourceRepo?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(repo))
            return string.Empty;

        if (Uri.TryCreate(repo, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return uri.ToString();
        }

        return repo.Contains('/', StringComparison.Ordinal)
            ? $"https://github.com/{repo.Trim('/')}"
            : string.Empty;
    }

    public string RepoId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Tooltip { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string PluginKind { get; set; } = string.Empty;

    public string ProjectPath { get; set; } = string.Empty;

    public string PackageManifest { get; set; } = "quasar-plugin.json";

    public string QuasarVersion { get; set; } = string.Empty;

    public string Commit { get; set; } = string.Empty;

    public string ManifestRepo { get; set; } = string.Empty;

    public string ManifestBranch { get; set; } = string.Empty;

    public string ManifestFile { get; set; } = string.Empty;

    public bool Hidden { get; set; }

    public bool Recommended { get; set; }

    public bool ImplicitLoading { get; set; }

    public IReadOnlyList<string> DependencyIds { get; set; } = [];

    public IReadOnlyList<string> CompanionPluginIds { get; set; } = [];

    public IReadOnlyList<string> Platforms { get; set; } = [];
}

public sealed class QuasarUiPluginInstallState
{
    public string InstallDirectory { get; set; } = string.Empty;

    public bool Installed { get; set; }

    public bool Enabled { get; set; } = true;

    public bool ImplicitInstallSuppressed { get; set; }

    public string ManifestPath { get; set; } = string.Empty;

    public string RuntimePluginId { get; set; } = string.Empty;

    public string RuntimeDisplayName { get; set; } = string.Empty;

    public string InstalledCommit { get; set; } = string.Empty;

    public DateTimeOffset? InstalledAtUtc { get; set; }

    public DateTimeOffset? CompanionUpdatedAtUtc { get; set; }

    public string Error { get; set; } = string.Empty;
}
