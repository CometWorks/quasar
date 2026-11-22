namespace Quasar.Client.Administration;

public sealed record RoleMapping(string Provider, string Subject, string[] Roles);
public sealed record DiscordMapping(string DiscordUserId, string Subject, string[] Roles);
public sealed record ConsentChoices(bool? ConsentGranted, bool DiagnosticsGranted, bool DumpsGranted);
public sealed record SecurityDashboard(string DefaultProvider, bool SteamEnabled, bool Enrolled,
    string AuthPath, string ConsentPath, Quasar.Services.Auth.QuasarTrustedNetworkSettings Network,
    ConsentChoices Consent, RoleMapping[] SubjectRoleMappings, DiscordMapping[] DiscordSubjects, string[] Roles);
