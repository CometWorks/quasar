using System.Text.Json;

namespace Quasar.Updates;

// One bridge requirement for UI, updater, CLI, installers and direct startup.
// A staged legacy worker does not complete installation of the unified package.
internal static class QuasarUpgradePath
{
    internal const string MigrationVersion = "1.4.1";
    internal const string Requirement = "Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. Install the complete 1.4.1 package, run its installer with your existing paths/account/scope, and start it successfully before retrying. A staged legacy web worker alone is insufficient.";
    internal static string ReceiptPath(string data) => Path.Combine(data, "Updates", "migration-1.4.1.json");
    private sealed record Receipt(string Version, DateTimeOffset HealthyAtUtc);

    private static Version Parse(string version)
    {
        var value = Version.Parse(Magnetar.Protocol.Runtime.QuasarReleaseVersion.Normalize(version).Split('-', 2)[0]);
        return new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));
    }
    internal static bool IsBridge(string version)
    {
        var value = Parse(version);
        return value.Major == 1 && value.Minor == 4 && value.Build == 1;
    }
    internal static bool IsCompleted(string data)
    {
        var path = ReceiptPath(data);
        if (!File.Exists(path)) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(QuasarUpdateJournal.ReadText(path), QuasarUpdateJournal.JsonOptions);
            return receipt is not null && receipt.HealthyAtUtc != default && IsBridge(receipt.Version);
        }
        catch (Exception exception) when (exception is JsonException or FormatException) { return false; }
    }
    internal static void EnsureAllowed(string version, string data)
    {
        if (Parse(version) >= new Version(1, 5, 0, 0) && !IsCompleted(data)) throw new InvalidOperationException(Requirement);
    }
    internal static string PackageVersion(string root)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "release.json")));
        return Magnetar.Protocol.Runtime.QuasarReleaseVersion.Normalize(metadata.RootElement.GetProperty("version").GetString() ?? throw new InvalidDataException("Missing release identity."));
    }
    internal static void RecordHealthy(string root, string data, string version)
    {
        if (!IsBridge(version) || IsCompleted(data)) return;
        if (!File.Exists(Path.Combine(root, LauncherReplacement.ManifestName)) || !File.Exists(Path.Combine(root, "release.json"))) return;
        if (PackageVersion(root) != version) throw new InvalidDataException("The running bridge does not match the installed package identity.");
        QuasarUpdateJournal.WriteJson(ReceiptPath(data), new Receipt(version, DateTimeOffset.UtcNow));
    }
}
