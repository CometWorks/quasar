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
    private DebouncedFileWatcher? _watcher;

    public SteamWorkshopCredentialsCatalog(
        ILogger<SteamWorkshopCredentialsCatalog> logger,
        IDataProtectionProvider dataProtectionProvider)
    {
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _credentials = LoadCredentials();
        _snapshot = CreateSnapshot(_credentials);

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
    }

    private SteamWorkshopCredentials LoadCredentials()
    {
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();

        try
        {
            if (!File.Exists(path))
                return SteamWorkshopCredentials.Normalize(null);

            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedCredentials>(json, JsonOptions);
            if (persisted is null)
                return SteamWorkshopCredentials.Normalize(null);

            var credentials = persisted.ToCredentials(_protector, _logger);
            return SteamWorkshopCredentials.Normalize(credentials);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading Steam Workshop credentials from {Path}", path);
            return SteamWorkshopCredentials.Normalize(null);
        }
    }

    private void StartWatching()
    {
        _watcher = DebouncedFileWatcher.WatchFile(MagnetarPaths.GetQuasarWorkshopOptionsPath(), ReloadFromDisk);
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
        public string? ProtectedSteamUsername { get; set; }
        public string? ProtectedSteamPassword { get; set; }

        public static PersistedCredentials FromCredentials(SteamWorkshopCredentials credentials, IDataProtector protector)
        {
            var key = credentials.WebApiKey;
            return new PersistedCredentials
            {
                ProtectedWebApiKey = string.IsNullOrWhiteSpace(key) ? null : protector.Protect(key),
                ProtectedSteamUsername = string.IsNullOrWhiteSpace(credentials.SteamUsername)
                    ? null : protector.Protect(credentials.SteamUsername),
                ProtectedSteamPassword = string.IsNullOrWhiteSpace(credentials.SteamPassword)
                    ? null : protector.Protect(credentials.SteamPassword),
            };
        }

        public SteamWorkshopCredentials ToCredentials(
            IDataProtector protector,
            ILogger logger)
        {
            try
            {
                return new SteamWorkshopCredentials
                {
                    WebApiKey = string.IsNullOrWhiteSpace(ProtectedWebApiKey) ? string.Empty : protector.Unprotect(ProtectedWebApiKey),
                    SteamUsername = string.IsNullOrWhiteSpace(ProtectedSteamUsername) ? string.Empty : protector.Unprotect(ProtectedSteamUsername),
                    SteamPassword = string.IsNullOrWhiteSpace(ProtectedSteamPassword) ? string.Empty : protector.Unprotect(ProtectedSteamPassword),
                };
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed unprotecting Steam Workshop credentials; the Data Protection keyring may have changed.");
                return new SteamWorkshopCredentials();
            }
        }
    }
}

public sealed class SteamWorkshopCredentials
{
    public string WebApiKey { get; set; } = string.Empty;
    public string SteamUsername { get; set; } = string.Empty;
    public string SteamPassword { get; set; } = string.Empty;

    public SteamWorkshopCredentials Clone() => new()
    {
        WebApiKey = WebApiKey,
        SteamUsername = SteamUsername,
        SteamPassword = SteamPassword,
    };

    public static SteamWorkshopCredentials Normalize(SteamWorkshopCredentials? credentials) => new()
    {
        WebApiKey = credentials?.WebApiKey?.Trim() ?? string.Empty,
        SteamUsername = credentials?.SteamUsername?.Trim() ?? string.Empty,
        SteamPassword = credentials?.SteamPassword ?? string.Empty,
    };
}
