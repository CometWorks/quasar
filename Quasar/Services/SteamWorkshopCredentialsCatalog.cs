using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;

namespace Quasar.Services;

public sealed class SteamWorkshopCredentialsCatalog : IDisposable
{
    private const string DataProtectionPurpose = "Quasar.SteamWorkshopCredentials.v1";

    private static readonly UnixFileMode CredentialUnixFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<SteamWorkshopCredentialsCatalog> _logger;
    private readonly IDataProtector _protector;
    private SteamWorkshopCredentials _credentials;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public SteamWorkshopCredentialsCatalog(
        ILogger<SteamWorkshopCredentialsCatalog> logger,
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

    public bool HasWebApiKey
    {
        get
        {
            lock (_sync)
            {
                return !string.IsNullOrWhiteSpace(_credentials.WebApiKey);
            }
        }
    }

    public SteamWorkshopCredentials GetCredentials()
    {
        lock (_sync)
        {
            return _credentials.Clone();
        }
    }

    public async Task SaveAsync(SteamWorkshopCredentials credentials, CancellationToken cancellationToken = default)
    {
        var normalized = SteamWorkshopCredentials.Normalize(credentials);
        var persisted = PersistedCredentials.FromCredentials(normalized, _protector);
        var json = JsonSerializer.Serialize(persisted, JsonOptions);
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);
        RestrictCredentialFileAccess(path);

        lock (_sync)
        {
            _credentials = normalized.Clone();
            _snapshot = CreateSnapshot(_credentials);
        }

        _logger.LogInformation("Saved Steam Workshop credentials to {Path}", path);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    private SteamWorkshopCredentials LoadCredentials(out bool requiresMigration)
    {
        requiresMigration = false;
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();

        try
        {
            if (!File.Exists(path))
                return SteamWorkshopCredentials.Normalize(null);

            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedCredentials>(json, JsonOptions);
            if (persisted is null)
                return SteamWorkshopCredentials.Normalize(null);

            var credentials = persisted.ToCredentials(_protector, _logger, out var migrated);
            requiresMigration = migrated;
            return SteamWorkshopCredentials.Normalize(credentials);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading Steam Workshop credentials from {Path}", path);
            return SteamWorkshopCredentials.Normalize(null);
        }
    }

    private async Task MigrateLegacyPlaintextAsync()
    {
        try
        {
            SteamWorkshopCredentials snapshot;
            lock (_sync)
            {
                snapshot = _credentials.Clone();
            }

            await SaveAsync(snapshot).ConfigureAwait(false);
            _logger.LogInformation("Migrated legacy plaintext Steam Workshop credentials to protected storage.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed migrating legacy plaintext Steam Workshop credentials.");
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();
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
                Path.GetFullPath(MagnetarPaths.GetQuasarWorkshopOptionsPath()),
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

        _logger.LogInformation("Reloaded Steam Workshop credentials from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(SteamWorkshopCredentials credentials) =>
        JsonSerializer.Serialize(SteamWorkshopCredentials.Normalize(credentials), JsonOptions);

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
                "Failed setting owner-only permissions on Steam Workshop credentials file {Path}. " +
                "The key remains encrypted, but the host filesystem permissions should be checked.",
                path);
        }
    }

    private sealed class PersistedCredentials
    {
        public string? ProtectedWebApiKey { get; set; }

        // Legacy plaintext field — read-only for backward compatibility.
        // New writes only emit ProtectedWebApiKey.
        public string? WebApiKey { get; set; }

        public static PersistedCredentials FromCredentials(SteamWorkshopCredentials credentials, IDataProtector protector)
        {
            var key = credentials.WebApiKey;
            return new PersistedCredentials
            {
                ProtectedWebApiKey = string.IsNullOrWhiteSpace(key) ? null : protector.Protect(key),
            };
        }

        public SteamWorkshopCredentials ToCredentials(
            IDataProtector protector,
            ILogger logger,
            out bool migratedFromPlaintext)
        {
            migratedFromPlaintext = false;

            if (!string.IsNullOrWhiteSpace(ProtectedWebApiKey))
            {
                try
                {
                    return new SteamWorkshopCredentials
                    {
                        WebApiKey = protector.Unprotect(ProtectedWebApiKey),
                    };
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed unprotecting Steam Workshop Web API key. " +
                        "The Data Protection keyring may have been rotated or replaced; clearing the stored key.");
                    return new SteamWorkshopCredentials();
                }
            }

            if (!string.IsNullOrWhiteSpace(WebApiKey))
            {
                migratedFromPlaintext = true;
                return new SteamWorkshopCredentials
                {
                    WebApiKey = WebApiKey,
                };
            }

            return new SteamWorkshopCredentials();
        }
    }
}

public sealed class SteamWorkshopCredentials
{
    public string WebApiKey { get; set; } = string.Empty;

    public SteamWorkshopCredentials Clone() => new()
    {
        WebApiKey = WebApiKey,
    };

    public static SteamWorkshopCredentials Normalize(SteamWorkshopCredentials? credentials) => new()
    {
        WebApiKey = credentials?.WebApiKey?.Trim() ?? string.Empty,
    };
}
