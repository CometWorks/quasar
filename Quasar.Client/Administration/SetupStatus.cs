namespace Quasar.Client.Administration;

public sealed record SetupStatus(bool HasAdmin, bool Complete, bool SteamEnabled, string Choice, string PanelId,
    bool Enrolled, string? BrowserUrl, string DashboardUrl, bool Active, bool TunnelRegistered, bool Ready, string Status,
    DirectPortStatus? DirectPort = null);

// The hosted direct-connection port step. Pending means a hosted panel has not chosen or skipped it.
public sealed record DirectPortStatus(bool Pending, int? Port, bool Skipped, long? CheckedAt, bool Reachable,
    string? Address, bool AddressPublic, string? Error, int DefaultPort);
