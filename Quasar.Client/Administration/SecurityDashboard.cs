namespace Quasar.Client.Administration;

public sealed record RoleMapping(string Provider, string Subject, string[] Roles, string? DiscordUserId);
public sealed record DiscordLinkStart(string Url, string FinishToken);
public sealed record MappingNames(string? SteamName, string? DiscordUserId, string? DiscordUsername);
public sealed record ConsentChoices(bool? ConsentGranted, bool DiagnosticsGranted, bool DumpsGranted);
public sealed record SecurityDashboard(string DefaultProvider, bool SteamEnabled, bool Enrolled,
    string AuthPath, string ConsentPath, Quasar.Services.Auth.QuasarTrustedNetworkSettings Network,
    ConsentChoices Consent, RoleMapping[] SubjectRoleMappings, string[] Roles, string? SignedInSteamId = null);
