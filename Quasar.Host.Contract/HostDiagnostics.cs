namespace Quasar.Host.Contract.V1;

/// <summary>Short-lived authorization for diagnostic sharing, issued only by the owning Quasar.</summary>
public sealed record HostDiagnosticPolicy(string InstallationId, bool DiagnosticsGranted, bool DumpsGranted,
    long Generation, DateTimeOffset GrantedSinceUtc, DateTimeOffset ExpiresAtUtc,
    string KeyId, string PublicKeyPem, DateTimeOffset KeyExpiresAtUtc);
