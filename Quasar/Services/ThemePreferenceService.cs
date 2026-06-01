using Microsoft.JSInterop;
using MudBlazor;

namespace Quasar.Services;

public enum ThemeMode { System, Light, Dark }

public sealed class ThemePreferenceService
{
    private const string StorageKey = "quasar.theme.mode";
    private readonly ILocalStorageService _localStorage;
    private readonly BrandingService _brandingService;
    private readonly IJSRuntime _js;
    private bool _initialized;

    public ThemePreferenceService(ILocalStorageService localStorage, BrandingService brandingService, IJSRuntime js)
    {
        _localStorage = localStorage;
        _brandingService = brandingService;
        _js = js;
    }

    public MudTheme Theme => _brandingService.BuildMudTheme();

    public ThemeMode Mode { get; private set; } = ThemeMode.System;
    public bool IsDarkMode { get; private set; } = true;

    public async Task<(ThemeMode Mode, bool IsDarkMode)> InitializeAsync()
    {
        if (_initialized)
            return (Mode, IsDarkMode);

        try
        {
            var stored = await _localStorage.GetItemAsync<string>(StorageKey);

            if (string.IsNullOrEmpty(stored))
            {
                var systemDark = await GetSystemDarkModeAsync();
                await _localStorage.SetItemAsync<string>(StorageKey, "system");
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
        if (Mode == ThemeMode.System)
            IsDarkMode = isDark;
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
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
            var value = mode switch
            {
                ThemeMode.Light => "light",
                ThemeMode.Dark => "dark",
                _ => "system",
            };
            await _localStorage.SetItemAsync<string>(StorageKey, value);
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
