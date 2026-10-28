using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Services;

/// <summary>The sections of the Settings page, in the order its section list shows them.</summary>
/// <summary>
/// The groups the section list shows its sections in, as small-capital labels above each: where the books are, how
/// they're read, and the person's own choices. The same four, in the same words, on the web.
/// </summary>
public static class SettingsGroups
{
    public const string Books = "BOOKS", Cataloguing = "CATALOGUING", You = "YOU", Admin = "ADMIN";

    /// <summary>A section's group; Start over, a development aid, sits under no label.</summary>
    public static string Of(SettingsSection section) => section switch
    {
        SettingsSection.Library or SettingsSection.OnlineLibraries or SettingsSection.Devices => Books,
        SettingsSection.Processing or SettingsSection.Ai or SettingsSection.Vocabulary => Cataloguing,
        SettingsSection.Preferences or SettingsSection.Passwords or SettingsSection.Backup => You,
        _ => "",
    };
}

public enum SettingsSection
{
    Library,
    /// <summary>Importing from online services, and how books owned online show and open.</summary>
    OnlineLibraries,
    /// <summary>Connecting this PC to the user's library at bibliotaph.online, which keeps every device in step.</summary>
    Devices,
    Ai,
    Processing,
    Vocabulary,
    /// <summary>Light or dark, where Bibliotaph opens, and what waits in Needs review.</summary>
    Preferences,
    Passwords,
    Backup,
    StartOver,
}

/// <summary>
/// Opens Settings at a section: the sidebar's status line opens Processing, Add folder and Manage folders elsewhere
/// open Library, and the Library's Import from an online library opens Online libraries. A Settings page already
/// showing switches section instead of opening another.
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

    /// <summary>AI reading, from Processing's and Settings > AI's "See what the AI is reading".</summary>
    public void OpenAiReading() => navigation.NavigateTo(Route.AiReading);

    bool _filesRequested;

    /// <summary>Needs review at Files needing attention, from Settings > AI's failed books and Processing's attention note.</summary>
    public void OpenFilesNeedingAttention()
    {
        _filesRequested = true;
        navigation.NavigateTo(Route.NeedsReview);
    }

    /// <summary>Whether the latest Needs review was opened by <see cref="OpenFilesNeedingAttention"/>, once.</summary>
    public bool TakeFilesRequest()
    {
        var requested = _filesRequested;
        _filesRequested = false;
        return requested;
    }

    /// <summary>The section the latest <see cref="Open"/> asked for, once; null when Settings was opened another way.</summary>
    public SettingsSection? TakeRequest()
    {
        var requested = _requested;
        _requested = null;
        return requested;
    }
}
