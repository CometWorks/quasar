using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;

namespace Quasar.Services;

public sealed class GitHubUpdateCredentialsCatalog : IDisposable
{
    private const string DataProtectionPurpose = "Quasar.GitHubUpdateCredentials.v1";

    private static readonly UnixFileMode CredentialUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<GitHubUpdateCredentialsCatalog> _logger;
    private readonly IDataProtector _protector;
    private GitHubUpdateCredentials _credentials;
    private string _snapshot;
    private DebouncedFileWatcher? _watcher;

    public GitHubUpdateCredentialsCatalog(
        ILogger<GitHubUpdateCredentialsCatalog> logger,
        IDataProtectionProvider dataProtectionProvider)
    {
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _credentials = LoadCredentials();
        _snapshot = CreateSnapshot(_credentials);

        StartWatching();
    }

    public event Action? Changed;

    public bool HasToken
    {
        get
        {
            lock (_sync)
            {
                return !string.IsNullOrWhiteSpace(_credentials.Token);
            }
        }
    }

    public GitHubUpdateCredentials GetCredentials()
    {
        lock (_sync)
        {
            return _credentials.Clone();
        }
    }

    public async Task SaveAsync(GitHubUpdateCredentials credentials, CancellationToken cancellationToken = default)
    {
        var normalized = GitHubUpdateCredentials.Normalize(credentials);
        var persisted = PersistedCredentials.FromCredentials(normalized, _protector);
        var json = JsonSerializer.Serialize(persisted, JsonOptions);
        var path = MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);
        RestrictCredentialFileAccess(path);

        lock (_sync)
        {
            _credentials = normalized.Clone();
            _snapshot = CreateSnapshot(_credentials);
        }

        _logger.LogInformation("Saved GitHub update credentials to {Path}", path);
        Changed?.Invoke();
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(new GitHubUpdateCredentials(), cancellationToken);

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    private GitHubUpdateCredentials LoadCredentials()
    {
        var path = MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath();

        try
        {
            if (!File.Exists(path))
                return GitHubUpdateCredentials.Normalize(null);

            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedCredentials>(json, JsonOptions);
            if (persisted is null)
                return GitHubUpdateCredentials.Normalize(null);

            var credentials = persisted.ToCredentials(_protector, _logger);
            return GitHubUpdateCredentials.Normalize(credentials);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading GitHub update credentials from {Path}", path);
            return GitHubUpdateCredentials.Normalize(null);
        }
    }

    private void StartWatching()
    {
        _watcher = DebouncedFileWatcher.WatchFile(MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath(), ReloadFromDisk);
    }

    private void ReloadFromDisk()
    {
        var reloaded = LoadCredentials();
        var snapshot = CreateSnapshot(reloaded);
        var changed = false;

        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _credentials = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded GitHub update credentials from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(GitHubUpdateCredentials credentials) =>
        JsonSerializer.Serialize(GitHubUpdateCredentials.Normalize(credentials), JsonOptions);

    private void RestrictCredentialFileAccess(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, CredentialUnixFileMode);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception,
                "Failed setting owner-only permissions on GitHub update credentials file {Path}. " +
                "The token remains encrypted, but the host filesystem permissions should be checked.",
                path);
        }
    }

    private sealed class PersistedCredentials
    {
        public string? ProtectedToken { get; set; }

        public static PersistedCredentials FromCredentials(GitHubUpdateCredentials credentials, IDataProtector protector)
        {
            var token = credentials.Token;
            return new PersistedCredentials
            {
                ProtectedToken = string.IsNullOrWhiteSpace(token) ? null : protector.Protect(token),
            };
        }

        public GitHubUpdateCredentials ToCredentials(
            IDataProtector protector,
            ILogger logger)
        {
            if (!string.IsNullOrWhiteSpace(ProtectedToken))
            {
                try
                {
                    return new GitHubUpdateCredentials
                    {
                        Token = protector.Unprotect(ProtectedToken),
                    };
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed unprotecting GitHub update token. " +
                        "The Data Protection keyring may have been rotated or replaced; clearing the stored token.");
                    return new GitHubUpdateCredentials();
                }
            }

            return new GitHubUpdateCredentials();
        }
    }
}

public sealed class GitHubUpdateCredentials
{
    public string Token { get; set; } = string.Empty;

    public GitHubUpdateCredentials Clone() => new()
    {
        Token = Token,
    };

    public static GitHubUpdateCredentials Normalize(GitHubUpdateCredentials? credentials) => new()
    {
        Token = credentials?.Token?.Trim() ?? string.Empty,
    };
}
