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
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public GitHubUpdateCredentialsCatalog(
        ILogger<GitHubUpdateCredentialsCatalog> logger,
        IDataProtectionProvider dataProtectionProvider)
    {
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _credentials = LoadCredentials(out var requiresMigration);
        _snapshot = CreateSnapshot(_credentials);

        if (requiresMigration)
            _ = MigrateLegacyPlaintextAsync();

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
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    private GitHubUpdateCredentials LoadCredentials(out bool requiresMigration)
    {
        requiresMigration = false;
        var path = MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath();

        try
        {
            if (!File.Exists(path))
                return GitHubUpdateCredentials.Normalize(null);

            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedCredentials>(json, JsonOptions);
            if (persisted is null)
                return GitHubUpdateCredentials.Normalize(null);

            var credentials = persisted.ToCredentials(_protector, _logger, out var migrated);
            requiresMigration = migrated;
            return GitHubUpdateCredentials.Normalize(credentials);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading GitHub update credentials from {Path}", path);
            return GitHubUpdateCredentials.Normalize(null);
        }
    }

    private async Task MigrateLegacyPlaintextAsync()
    {
        try
        {
            GitHubUpdateCredentials snapshot;
            lock (_sync)
            {
                snapshot = _credentials.Clone();
            }

            await SaveAsync(snapshot).ConfigureAwait(false);
            _logger.LogInformation("Migrated legacy plaintext GitHub update credentials to protected storage.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed migrating legacy plaintext GitHub update credentials.");
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = Path.GetFileName(path),
        };

        _watcher.Changed += HandleWatchedFileChanged;
        _watcher.Created += HandleWatchedFileChanged;
        _watcher.Deleted += HandleWatchedFileChanged;
        _watcher.Renamed += HandleWatchedFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void HandleWatchedFileChanged(object sender, FileSystemEventArgs args)
    {
        if (!string.Equals(
                Path.GetFullPath(args.FullPath),
                Path.GetFullPath(MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath()),
                StringComparison.OrdinalIgnoreCase))
            return;

        ScheduleReload();
    }

    private void ScheduleReload()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _reloadDebounce?.Cancel();
            _reloadDebounce?.Dispose();
            _reloadDebounce = new CancellationTokenSource();
            debounce = _reloadDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), debounce.Token);
                ReloadFromDisk();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void ReloadFromDisk()
    {
        var reloaded = LoadCredentials(out var requiresMigration);
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

        if (requiresMigration)
            _ = MigrateLegacyPlaintextAsync();

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

        // Legacy plaintext field, read-only for migration if early builds wrote it.
        public string? Token { get; set; }

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
            ILogger logger,
            out bool migratedFromPlaintext)
        {
            migratedFromPlaintext = false;

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

            if (!string.IsNullOrWhiteSpace(Token))
            {
                migratedFromPlaintext = true;
                return new GitHubUpdateCredentials
                {
                    Token = Token,
                };
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
