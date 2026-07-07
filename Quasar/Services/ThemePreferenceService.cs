using Microsoft.JSInterop;
using MudBlazor;

namespace Quasar.Services;

public enum ThemeMode { System, Light, Dark }

public sealed class ThemePreferenceService
{
    private const string StorageKey = "quasar.theme.mode";
    private readonly BrowserStorageService _storage;
    private readonly BrandingService _brandingService;
    private readonly IJSRuntime _js;
    private bool _initialized;

    public ThemePreferenceService(BrowserStorageService storage, BrandingService brandingService, IJSRuntime js)
    {
        _storage = storage;
        _brandingService = brandingService;
        _js = js;
    }

    public MudTheme Theme => _brandingService.BuildMudTheme();
    public event Action<bool>? ThemeModeChanged;

    public ThemeMode Mode { get; private set; } = ThemeMode.System;
    public bool IsDarkMode { get; private set; } = true;

    public async Task<(ThemeMode Mode, bool IsDarkMode)> InitializeAsync()
    {
        if (_initialized)
            return (Mode, IsDarkMode);

        var previousDarkMode = IsDarkMode;
        try
        {
            var stored = await _storage.GetStringAsync(StorageKey);

            if (string.IsNullOrEmpty(stored))
            {
                var systemDark = await GetSystemDarkModeAsync();
                await _storage.SetStringAsync(StorageKey, "system");
                Mode = ThemeMode.System;
                IsDarkMode = systemDark;
            }
            else
            {
                Mode = stored switch
                {
                    "light" => ThemeMode.Light,
                    "dark" => ThemeMode.Dark,
                    _ => ThemeMode.System,
                };

                IsDarkMode = Mode switch
                {
                    ThemeMode.Light => false,
                    ThemeMode.Dark => true,
                    _ => await GetSystemDarkModeAsync(),
                };
            }

            if (previousDarkMode != IsDarkMode)
                ThemeModeChanged?.Invoke(IsDarkMode);

            _initialized = true;
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }

        return (Mode, IsDarkMode);
    }

    public void SyncSystemDarkMode(bool isDark)
    {
        var previous = IsDarkMode;
        if (Mode == ThemeMode.System)
            IsDarkMode = isDark;

        if (previous != IsDarkMode)
            ThemeModeChanged?.Invoke(IsDarkMode);
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
        var previous = IsDarkMode;
        Mode = mode;
        IsDarkMode = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => await GetSystemDarkModeAsync(),
        };
        _initialized = true;

        try
        {
            if (previous != IsDarkMode)
                ThemeModeChanged?.Invoke(IsDarkMode);

            var value = mode switch
            {
                ThemeMode.Light => "light",
                ThemeMode.Dark => "dark",
                _ => "system",
            };
            await _storage.SetStringAsync(StorageKey, value);
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task<bool> GetSystemDarkModeAsync()
    {
        try
        {
            return await _js.InvokeAsync<bool>("quasarConfigs.getSystemDarkMode");
        }
        catch
        {
            return true;
        }
    }
}
