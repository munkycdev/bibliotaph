using Bibliotaph.Core;

namespace Bibliotaph.App.Services;

/// <summary>Light, dark or the system's choice, and whether a contrast theme has replaced them: what a view model asks of the theme.</summary>
public interface IThemeService
{
    ThemePreference Preference { get; }

    /// <summary>True while the app shows its dark palette.</summary>
    bool IsDark { get; }

    /// <summary>True while a contrast theme is on, so the system's colours replace both palettes.</summary>
    bool IsHighContrast { get; }

    /// <summary>Raised on the UI thread after the palette changed.</summary>
    event EventHandler? Changed;

    Task SetPreferenceAsync(ThemePreference preference);

    /// <summary>The top bar's quick toggle: switch to the opposite of what is showing now.</summary>
    Task ToggleAsync();
}
