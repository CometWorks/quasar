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
        options.Validate();
        return options;
    }

    internal void Validate(bool allowEmpty = false)
    {
        if (Listings is null || !allowEmpty && Listings.Count == 0 || Listings.Count > 100
            || Listings.Any(l => l is null || l.ListingId is null || string.IsNullOrWhiteSpace(l.UniqueName)
                || l.Kind is not ("server" or "cluster") || l.Name is null || l.PublicHost is null)
            || Listings.Where(l => l.ListingId.Length != 0).Select(l => l.ListingId).Distinct(StringComparer.Ordinal).Count()
                != Listings.Count(l => l.ListingId.Length != 0)
            || Listings.Select(l => (l.Kind, l.UniqueName.ToLowerInvariant())).Distinct().Count() != Listings.Count)
            throw new InvalidDataException("Server list requires 1–100 distinct listings and sources.");
        foreach (var entry in Listings)
        {
            if (entry.Enabled) entry.Validate();
            else if (entry.ListingId.Length != 0 && !ServerListProtocol.IsId(entry.ListingId))
                throw new InvalidDataException("Invalid server list identity.");
        }
    }

    public ServerListOptions Clone() => new() { Enabled = Enabled, DirectoryUrl = DirectoryUrl,
        InstallationToken = InstallationToken, Listings = Listings.Select(l => l.Clone()).ToList() };

    internal static bool IsAllowedEndpoint(Uri url) => url.Scheme == "https"
        && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment);
}

public sealed class ServerListEntry
{
    public bool Enabled { get; set; } = true;
    // Optional stable ID; omitted IDs derive from installation, kind and source name.
    public string ListingId { get; set; } = "";
    public string Kind { get; set; } = "server";
    public string UniqueName { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = 27016;

    public ServerListEntry Clone() => (ServerListEntry)MemberwiseClone();

    internal void Validate()
    {
        if (ListingId.Length != 0 && !ServerListProtocol.IsId(ListingId) || Kind is not ("server" or "cluster")
            || string.IsNullOrWhiteSpace(UniqueName) || string.IsNullOrWhiteSpace(Name) || Name.Length > 128
            || PublicPort is < 1 or > 65535 || Uri.CheckHostName(PublicHost) == UriHostNameType.Unknown
            || PublicHost is "0.0.0.0" or "::" or "localhost"
            || System.Net.IPAddress.TryParse(PublicHost, out var ip) && System.Net.IPAddress.IsLoopback(ip))
            throw new InvalidDataException("Invalid server list identity, source, name or public join address.");
    }
}
