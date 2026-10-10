namespace Quasar.Services.ServerList;

public sealed class ServerListOptions
{
    public bool Enabled { get; set; }
    public string? DirectoryUrl { get; set; }
    public string? InstallationToken { get; set; }
    public List<ServerListEntry> Listings { get; set; } = [];

    public static ServerListOptions Create(IConfiguration configuration)
    {
        var options = new ServerListOptions();
        configuration.GetSection("Quasar:ServerList").Bind(options);
        if (!options.Enabled) return options;
        if (!Uri.TryCreate(options.DirectoryUrl, UriKind.Absolute, out var url)
            || !IsAllowedEndpoint(url) || string.IsNullOrWhiteSpace(options.InstallationToken))
            throw new InvalidDataException("Server list requires an HTTPS directory URL and installation token.");
        if (options.Listings.Count == 0 || options.Listings.Count > 100
            || options.Listings.Select(l => l.ListingId).Distinct(StringComparer.Ordinal).Count() != options.Listings.Count
            || options.Listings.Select(l => (l.Kind, l.UniqueName.ToLowerInvariant())).Distinct().Count() != options.Listings.Count)
            throw new InvalidDataException("Server list requires 1–100 distinct listings and sources.");
        foreach (var entry in options.Listings) entry.Validate();
        return options;
    }

    internal static bool IsAllowedEndpoint(Uri url) => url.Scheme == "https"
        && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment);
}

public sealed class ServerListEntry
{
    // Assigned by the directory during enrollment; independent of mutable server names.
    public string ListingId { get; set; } = "";
    public string Kind { get; set; } = "server";
    public string UniqueName { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = 27016;

    internal void Validate()
    {
        if (!ServerListProtocol.IsId(ListingId) || Kind is not ("server" or "cluster")
            || string.IsNullOrWhiteSpace(UniqueName) || string.IsNullOrWhiteSpace(Name) || Name.Length > 128
            || PublicPort is < 1 or > 65535 || Uri.CheckHostName(PublicHost) == UriHostNameType.Unknown
            || PublicHost is "0.0.0.0" or "::" or "localhost"
            || System.Net.IPAddress.TryParse(PublicHost, out var ip) && System.Net.IPAddress.IsLoopback(ip))
            throw new InvalidDataException("Invalid server list identity, source, name or public join address.");
    }
}
