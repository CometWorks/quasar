using Microsoft.JSInterop;
using MudBlazor;

namespace Quasar.Services;

public sealed class ThemePreferenceService
{
    private const string StorageKey = "quasar.theme.mode";
    private readonly ILocalStorageService _localStorage;
    private readonly BrandingService _brandingService;
    private bool _initialized;

    public ThemePreferenceService(ILocalStorageService localStorage, BrandingService brandingService)
    {
        _localStorage = localStorage;
        _brandingService = brandingService;
    }

    public MudTheme Theme => _brandingService.BuildMudTheme();

    public bool IsDarkMode { get; private set; } = true;

    public async Task<bool> InitializeAsync()
    {
        if (_initialized)
            return IsDarkMode;

        try
        {
            var storedValue = await _localStorage.GetItemAsync<string>(StorageKey);
            IsDarkMode = !string.Equals(storedValue, "light", StringComparison.OrdinalIgnoreCase);
            _initialized = true;
        }
        catch (InvalidOperationException)
        {
            IsDarkMode = true;
        }
        catch (JSDisconnectedException)
        {
            IsDarkMode = true;
        }

        return IsDarkMode;
    }

    public async Task SetDarkModeAsync(bool isDarkMode)
    {
        IsDarkMode = isDarkMode;
        _initialized = true;

        try
        {
            await _localStorage.SetItemAsync<string>(StorageKey, isDarkMode ? "dark" : "light");
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
