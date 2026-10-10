using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Bibliotaph.App.Services;

/// <summary>
/// Light and dark. Follows the Windows app mode unless Settings overrides it, swaps the token dictionary
/// (every brush is used through DynamicResource), and tints each window's standard title bar to match. While a Windows
/// contrast theme is on, the system's own colours replace both palettes, and the light or dark choice waits for it to
/// go off again.
/// </summary>
public sealed class ThemeService(SettingsStore settings, ILogger<ThemeService> log)
{
    static readonly Uri LightTokens = new("pack://application:,,,/Themes/Tokens.Light.xaml");
    static readonly Uri DarkTokens = new("pack://application:,,,/Themes/Tokens.Dark.xaml");
    static readonly Uri ContrastTokens = new("pack://application:,,,/Themes/Tokens.Contrast.xaml");

    readonly List<Window> _windows = [];
    ResourceDictionary? _tokens;
    bool? _contrastForTest;

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public bool IsDark { get; private set; }

    /// <summary>True while a Windows contrast theme is on, so the tokens are the system's colours.</summary>
    public bool IsHighContrast { get; private set; }

    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        var saved = await settings.GetAsync(SettingKeys.Appearance);
        Preference = Enum.TryParse<ThemePreference>(saved, out var preference) ? preference : ThemePreference.System;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current?.Dispatcher.BeginInvoke(() => Apply());
        };
        Apply();
    }

    /// <summary>For the smoke test: the contrast palette on or off whatever Windows says, or null to follow Windows again.</summary>
    internal void UseHighContrastForTest(bool? on)
    {
        _contrastForTest = on;
        Apply(reload: true);
    }

    public async Task SetPreferenceAsync(ThemePreference preference)
    {
        if (preference == Preference) return;
        Preference = preference;
        Apply();
        await settings.SetAsync(SettingKeys.Appearance, preference.ToString());
    }

    /// <summary>The top bar's quick toggle: switch to the opposite of what is showing now.</summary>
    public Task ToggleAsync() => SetPreferenceAsync(IsDark ? ThemePreference.Light : ThemePreference.Dark);

    /// <summary>Tints this window's title bar now and whenever the theme changes.</summary>
    public void Track(Window window)
    {
        _windows.Add(window);
        window.SourceInitialized += (_, _) => TintTitleBar(window);
        window.Closed += (_, _) => _windows.Remove(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) TintTitleBar(window);
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // The app mode lives under the General category.
        if (e.Category == UserPreferenceCategory.General && Preference == ThemePreference.System)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply());
        // Choosing another contrast theme changes the system colours, which the contrast palette copied when it loaded.
        else if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(reload: IsHighContrast));
    }

    void Apply(bool reload = false)
    {
        var dark = Preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => WindowsUsesDarkAppMode(),
        };
        var contrast = _contrastForTest ?? SystemParameters.HighContrast;
        var changed = reload || dark != IsDark || contrast != IsHighContrast || _tokens is null;
        IsDark = dark;
        IsHighContrast = contrast;
        if (!changed) return;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var tokens = new ResourceDictionary { Source = contrast ? ContrastTokens : dark ? DarkTokens : LightTokens };
        if (_tokens is not null && dictionaries.IndexOf(_tokens) is var index and >= 0) dictionaries[index] = tokens;
        else dictionaries.Insert(0, tokens);
        _tokens = tokens;

        foreach (var window in _windows) TintTitleBar(window);
        log.LogInformation("Theme is now {Mode} (preference {Preference})", contrast ? "contrast" : dark ? "dark" : "light", Preference);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void TintTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || _tokens is null) return;
        // A contrast theme draws its own title bars; ours would only fight it.
        if (IsHighContrast)
        {
            DwmTitleBar.Reset(handle);
            return;
        }
        DwmTitleBar.Apply(handle, IsDark,
            caption: (Color)_tokens["Bt.SideColor"],
            text: (Color)_tokens["Bt.TextColor"],
            border: (Color)_tokens["Bt.LineColor"]);
    }

    static bool WindowsUsesDarkAppMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
