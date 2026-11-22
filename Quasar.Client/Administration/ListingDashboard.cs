namespace Quasar.Client.Administration;

public sealed record ListingDraft(string Kind, string UniqueName, string SourceName, bool Exists)
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; }
}

public sealed record ListingDashboard(long Revision, bool HasAdmin, bool CanManage, string? LoadError, ListingDraft[] Listings);
public sealed record ListingSave(long Revision, ListingDraft[] Listings);
