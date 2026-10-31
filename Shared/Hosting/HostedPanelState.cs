using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;

namespace Quasar.Hosting;

public sealed record HostedPanelState
{
    public const string Origin = "https://dash.se-servers.net";
    public string Choice { get; init; } = "";
    public string PanelId { get; init; } = "";
    [JsonRequired]
    public string InstallationSecret { get; init; } = NewSecret();
    [JsonRequired]
    public string LocalProxySecret { get; init; } = NewSecret();
    public string? EnrollmentId { get; init; }
    public string? BrowserUrl { get; init; }
    public string? Credential { get; init; }
    public string? OwnerSteamId { get; init; }
    public string DashboardUrl => Origin + "/" + PanelId + "/";
    public static string DirectoryPath => Path.Combine(MagnetarPaths.GetQuasarDirectory(), "HostedPanel");
    public static string StatePath => Path.Combine(DirectoryPath, "enrollment.json");
    public static bool IsPanelId(string? value) => value is { Length: >= 3 and <= 63 }
        && Regex.IsMatch(value, "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z")
        && !new[] { "account", "api", "internal", "health", "login", "logout", "signin-steam", "billing", "assets", "terms", "privacy", "refunds" }.Contains(value);
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static bool SecretEquals(string? left, string? right) => left is not null && right is not null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(left)),
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(right)));

    public static HostedPanelState? Read()
    {
        try { return ReadCore(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static HostedPanelState? ReadCore()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<HostedPanelState>(File.ReadAllText(StatePath));
        if (state is null || state.InstallationSecret is not { Length: 64 } || state.LocalProxySecret is not { Length: 64 }
            || state.InstallationSecret.Any(c => !char.IsAsciiHexDigit(c)) || state.LocalProxySecret.Any(c => !char.IsAsciiHexDigit(c))
            || state.Choice is not ("" or "self" or "dismissed" or "hosted")
            || state.Choice == "hosted" && !IsPanelId(state.PanelId))
            throw new InvalidDataException("Hosted enrollment state is invalid; restore its private backup.");
        return state;
    }

    // Only the web worker writes enrollment state. Bootstrap reads it atomically.
    public static async Task SaveAsync(HostedPanelState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(DirectoryPath).SetAccessControl(security);
        }
        else File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await WritePrivateAsync(StatePath, state, ct);
    }

    internal static async Task WritePrivateAsync<T>(string path, T value, CancellationToken ct)
    {
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(file, value, cancellationToken: ct);
                await file.FlushAsync(ct);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record HostedConnectorHealth(string PanelId, bool TunnelRegistered, bool WorkerReady, long UpdatedAt)
{
    public bool Ready => TunnelRegistered && WorkerReady && UpdatedAt >= DateTimeOffset.UtcNow.AddSeconds(-35).ToUnixTimeSeconds();
    public static string FilePath => Path.Combine(HostedPanelState.DirectoryPath, "connector-health.json");
    public static HostedConnectorHealth? Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<HostedConnectorHealth>(File.ReadAllText(FilePath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public Task SaveAsync(CancellationToken ct) => HostedPanelState.WritePrivateAsync(FilePath, this, ct);
}
