using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Quasar.Services;

namespace Quasar.Services.Discord;

public sealed class DiscordOptionsCatalog : IDisposable
{
    private const string DataProtectionPurpose = "Quasar.DiscordOptions.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<DiscordOptionsCatalog> _logger;
    private readonly IDataProtector _protector;
    private readonly string _path;
    private DiscordOptions _options;
    private string _snapshot;
    private DebouncedFileWatcher? _watcher;

    public DiscordOptionsCatalog(
        ILogger<DiscordOptionsCatalog> logger,
        IDataProtectionProvider dataProtectionProvider)
        : this(logger, dataProtectionProvider, MagnetarPaths.GetQuasarDiscordOptionsPath())
    {
    }

    internal DiscordOptionsCatalog(
        ILogger<DiscordOptionsCatalog> logger,
        IDataProtectionProvider dataProtectionProvider,
        string path)
    {
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _path = path;
        _options = LoadOptions();
        _snapshot = CreateSnapshot(_options);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    public DiscordOptions GetOptions()
    {
        lock (_sync)
        {
            return _options.Clone();
        }
    }

    public async Task SaveAsync(DiscordOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = DiscordOptions.Normalize(options);
        var json = CreatePersistedJson(normalized);

        await AtomicFileWriter.WritePrivateTextAsync(_path, json, cancellationToken);

        lock (_sync)
        {
            _options = normalized.Clone();
            _snapshot = CreateSnapshot(normalized);
        }

        _logger.LogInformation("Saved Discord options to {Path}", _path);
        Changed?.Invoke();
    }

    private DiscordOptions LoadOptions()
    {
        try
        {
            if (!File.Exists(_path))
                return DiscordOptions.Normalize(null);

            var json = File.ReadAllText(_path);
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new JsonException("Discord options must be a JSON object.");
            var options = DiscordOptions.Normalize(root.Deserialize<DiscordOptions>(JsonOptions));
            var migrateLegacyToken = root.ContainsKey("botToken");

            if (root["protectedBotToken"] is JsonValue protectedValue
                && protectedValue.TryGetValue<string>(out var protectedToken)
                && !string.IsNullOrWhiteSpace(protectedToken))
            {
                try
                {
                    options.BotToken = _protector.Unprotect(protectedToken);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception,
                        "Failed unprotecting the Discord bot token; the non-secret Discord options remain available.");
                    options.BotToken = string.Empty;
                    migrateLegacyToken = false;
                }
            }

            if (migrateLegacyToken)
            {
                // Migrate legacy plaintext without waiting for an operator to save the page again.
                try
                {
                    var migrated = CreatePersistedJson(options);
                    AtomicFileWriter.WritePrivateTextAsync(_path, migrated).GetAwaiter().GetResult();
                    _logger.LogInformation("Migrated legacy Discord options to protected storage at {Path}", _path);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception,
                        "Failed migrating legacy Discord options at {Path}; the loaded options remain available.", _path);
                }
            }

            return options;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading Discord options from {Path}", _path);
            return DiscordOptions.Normalize(null);
        }
    }

    private void StartWatching()
    {
        _watcher = DebouncedFileWatcher.WatchFile(_path, ReloadFromDisk);
    }

    private void ReloadFromDisk()
    {
        DiscordOptions reloaded;
        string snapshot;

        try
        {
            reloaded = LoadOptions();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Discord options from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _options = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Discord options from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(DiscordOptions options)
    {
        return JsonSerializer.Serialize(DiscordOptions.Normalize(options), JsonOptions);
    }

    private string CreatePersistedJson(DiscordOptions options)
    {
        var normalized = DiscordOptions.Normalize(options);
        var root = JsonSerializer.SerializeToNode(normalized, JsonOptions)?.AsObject()
            ?? new JsonObject();
        root.Remove("botToken");
        if (!string.IsNullOrWhiteSpace(normalized.BotToken))
            root["protectedBotToken"] = _protector.Protect(normalized.BotToken);
        return root.ToJsonString(JsonOptions);
    }
}
