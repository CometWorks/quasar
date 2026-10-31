using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;

namespace Quasar.Services;

public sealed class GitHubUpdateCredentialsCatalog : IDisposable
{
    private const string DataProtectionPurpose = "Quasar.GitHubUpdateCredentials.v1";
    public const string QuasarTokenVariableName = "QUASAR_GITHUB_TOKEN";
    public const string GenericTokenVariableName = "GITHUB_TOKEN";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<GitHubUpdateCredentialsCatalog> _logger;
    private readonly IDataProtector _protector;
    private readonly string _quasarVariableToken;
    private readonly string _genericVariableToken;
    private GitHubUpdateCredentials _credentials;
    private string _snapshot;
    private DebouncedFileWatcher? _watcher;

    public GitHubUpdateCredentialsCatalog(
        ILogger<GitHubUpdateCredentialsCatalog> logger,
        IDataProtectionProvider dataProtectionProvider,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        _quasarVariableToken = readEnvironmentVariable(QuasarTokenVariableName)?.Trim() ?? string.Empty;
        _genericVariableToken = readEnvironmentVariable(GenericTokenVariableName)?.Trim() ?? string.Empty;
        _credentials = LoadCredentials();
        _snapshot = CreateSnapshot(_credentials);

        StartWatching();
    }

    public event Action? Changed;

    public bool HasToken => Token.Length > 0;

    // The same order as the launcher's GitHubUpdateTokenReader, so both use the same token:
    // QUASAR_GITHUB_TOKEN wins over the saved token, a GITHUB_TOKEN set for other tools comes last.
    public string Token
    {
        get
        {
            if (_quasarVariableToken.Length > 0)
                return _quasarVariableToken;

            lock (_sync)
            {
                if (_credentials.Token.Length > 0)
                    return _credentials.Token;
            }

            return _genericVariableToken;
        }
    }

    /// <summary>The environment variable <see cref="Token"/> comes from, empty when it is the saved one or none.</summary>
    public string TokenVariableName
    {
        get
        {
            if (_quasarVariableToken.Length > 0)
                return QuasarTokenVariableName;

            return HasSavedToken || _genericVariableToken.Length == 0 ? string.Empty : GenericTokenVariableName;
        }
    }

    public bool HasSavedToken
    {
        get
        {
            lock (_sync)
            {
                return _credentials.Token.Length > 0;
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

        await AtomicFileWriter.WritePrivateTextAsync(path, json, cancellationToken);

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
