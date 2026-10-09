using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Services;

/// <summary>The sections of the Settings page, in the order its section list shows them.</summary>
public enum SettingsSection
{
    Library,
    Processing,
    Appearance,
    Review,
    Vocabulary,
    Ai,
    StartOver,
}

/// <summary>
/// Opens Settings at a section: the sidebar's status line opens Processing, and Add folder and Manage folders
/// elsewhere open Library. A Settings page already showing switches section instead of opening another.
/// </summary>
public sealed class SettingsLinks(INavigationService navigation)
{
    SettingsSection? _requested;

    public void Open(SettingsSection section)
    {
        if (navigation.Current is SettingsViewModel settings)
        {
            settings.Show(section);
            return;
        }
        _requested = section;
        navigation.NavigateTo(Route.Settings);
    }

    /// <summary>The section the latest <see cref="Open"/> asked for, once; null when Settings was opened another way.</summary>
    public SettingsSection? TakeRequest()
    {
        var requested = _requested;
        _requested = null;
        return requested;
    }
}
