using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Quasar.Updates;

internal static class GitHubUpdateTokenReader
{
    internal const string DataProtectionPurpose = "Quasar.GitHubUpdateCredentials.v1";
    internal const string QuasarTokenVariableName = "QUASAR_GITHUB_TOKEN";
    internal const string GenericTokenVariableName = "GITHUB_TOKEN";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Shared with legacy Bootstrap and the native updater: an explicit Quasar
    // variable wins, then the saved token/keyring, then a generic tool token.
    public static string ReadToken(ILogger logger)
    {
        var token = ReadVariable(QuasarTokenVariableName);
        if (token.Length == 0)
            token = ReadSavedToken(logger);
        if (token.Length == 0)
            token = ReadVariable(GenericTokenVariableName);
        return token;
    }

    private static string ReadVariable(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    internal static string ReadSavedToken(ILogger logger)
    {
        var path = MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath();
        if (!File.Exists(path))
            return string.Empty;

        try
        {
            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedCredentials>(json, JsonOptions);
            if (persisted is null)
                return string.Empty;

            if (string.IsNullOrWhiteSpace(persisted.ProtectedToken))
                return string.Empty;

            var protector = DataProtectionProvider
                .Create(new DirectoryInfo(MagnetarPaths.GetQuasarDataProtectionKeyringDirectory()), options => options.SetApplicationName("Quasar"))
                .CreateProtector(DataProtectionPurpose);

            return protector.Unprotect(persisted.ProtectedToken).Trim();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed reading GitHub update token from {Path}. Continuing without GitHub authentication.", path);
            return string.Empty;
        }
    }

    private sealed class PersistedCredentials
    {
        public string? ProtectedToken { get; set; }
    }
}
