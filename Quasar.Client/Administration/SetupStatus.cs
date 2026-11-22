namespace Quasar.Client.Administration;

public sealed record SetupStatus(bool HasAdmin, bool Complete, bool SteamEnabled, string Choice, string PanelId,
    bool Enrolled, string? BrowserUrl, string DashboardUrl, bool Active, bool TunnelRegistered, bool Ready, string Status);
