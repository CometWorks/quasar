using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using MudBlazor;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>
/// Singleton store for branding and theme configuration. Persists to
/// <c>branding.json</c> in the Quasar data directory and writes uploaded logo /
/// favicon assets into <c>{WebRootPath}/branding/</c>. Mirrors the file-watch
/// debounce pattern used by <see cref="Discord.DiscordOptionsCatalog"/> so
/// external edits are picked up live.
/// </summary>
public sealed class BrandingService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<BrandingService> _logger;
    private readonly string _brandingAssetsDirectory;
    private BrandingSettings _settings;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public BrandingService(ILogger<BrandingService> logger, IWebHostEnvironment environment)
    {
        _logger = logger;

        var webRootPath = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        _brandingAssetsDirectory = MagnetarPaths.GetQuasarBrandingDirectory(webRootPath);

        _settings = LoadSettings();
        _snapshot = CreateSnapshot(_settings);
        StartWatching();
    }

    public event Action? Changed;

    /// <summary>Live (lock-guarded) reference to the current normalized settings.</summary>
    public BrandingSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    /// <summary>Returns a deep copy safe for UI draft editing.</summary>
    public BrandingSettings GetSettings()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public MudTheme BuildMudTheme()
    {
        BrandingSettings snapshot;
        lock (_sync)
        {
            snapshot = _settings;
        }

        return new MudTheme
        {
            LayoutProperties = QuasarTheme.Default.LayoutProperties,
            PaletteLight = snapshot.LightPalette.ToMudPaletteLight(),
            PaletteDark = snapshot.DarkPalette.ToMudPaletteDark(),
        };
    }

    public async Task SaveAsync(BrandingSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = BrandingSettings.Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = MagnetarPaths.GetQuasarBrandingPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        lock (_sync)
        {
            _settings = normalized;
            _snapshot = json;
        }

        _logger.LogInformation("Saved branding settings to {Path}", path);
        Changed?.Invoke();
    }

    public async Task SaveLogoAsync(bool isDark, Stream data, string extension, CancellationToken cancellationToken = default)
    {
        var baseName = isDark ? "logo-dark" : "logo-light";
        var relativePath = await WriteAssetAsync(baseName, data, extension, cancellationToken);

        var settings = GetSettings();
        if (isDark)
            settings.LogoDarkPath = relativePath;
        else
            settings.LogoLightPath = relativePath;

        await SaveAsync(settings, cancellationToken);
    }

    public async Task SaveFaviconAsync(Stream data, string extension, CancellationToken cancellationToken = default)
    {
        var relativePath = await WriteAssetAsync("favicon", data, extension, cancellationToken);

        var settings = GetSettings();
        settings.FaviconPath = relativePath;
        await SaveAsync(settings, cancellationToken);
    }

    public async Task ResetToDefaultAsync(CancellationToken cancellationToken = default)
    {
        await SaveAsync(BrandingSettings.Normalize(null), cancellationToken);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    private async Task<string> WriteAssetAsync(string baseName, Stream data, string extension, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_brandingAssetsDirectory);

        var normalizedExtension = NormalizeExtension(extension);
        var fileName = baseName + normalizedExtension;
        var fullPath = Path.Combine(_brandingAssetsDirectory, fileName);

        RemoveExistingAssets(baseName);

        await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await data.CopyToAsync(fileStream, cancellationToken);
        }

        var cacheBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"/branding/{fileName}?v={cacheBust}";
    }

    private void RemoveExistingAssets(string baseName)
    {
        try
        {
            if (!Directory.Exists(_brandingAssetsDirectory))
                return;

            foreach (var existing in Directory.EnumerateFiles(_brandingAssetsDirectory, baseName + ".*"))
            {
                try
                {
                    File.Delete(existing);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed clearing previous branding asset {BaseName}.", baseName);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return ".png";

        var trimmed = extension.Trim().ToLowerInvariant();
        if (!trimmed.StartsWith('.'))
            trimmed = "." + trimmed;

        // Strip anything beyond a valid file-name extension (defensive).
        foreach (var invalid in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(invalid, '-');

        return trimmed;
    }

    private BrandingSettings LoadSettings()
    {
        var path = MagnetarPaths.GetQuasarBrandingPath();

        try
        {
            if (!File.Exists(path))
                return BrandingSettings.Normalize(null);

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<BrandingSettings>(json, JsonOptions);
            return BrandingSettings.Normalize(settings);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading branding settings from {Path}", path);
            return BrandingSettings.Normalize(null);
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarBrandingPath();
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
        if (!IsTrackedPath(args.FullPath))
            return;

        ScheduleReload();
    }

    private static bool IsTrackedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(MagnetarPaths.GetQuasarBrandingPath()),
            StringComparison.OrdinalIgnoreCase);
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
        BrandingSettings reloaded;
        string snapshot;

        try
        {
            reloaded = LoadSettings();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading branding settings from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _settings = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded branding settings from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(BrandingSettings settings)
    {
        return JsonSerializer.Serialize(BrandingSettings.Normalize(settings), JsonOptions);
    }
}
