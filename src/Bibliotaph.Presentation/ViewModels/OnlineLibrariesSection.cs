using Bibliotaph.Processing;

namespace Bibliotaph.Presentation.ViewModels;

/// <summary>
/// Settings &gt; Online libraries, as Settings and Home see it. Reading a library page needs Bibliotaph's own browser, so
/// the section itself is the platform's.
/// </summary>
public abstract class OnlineLibrariesSection : SettingsSectionViewModel
{
    /// <summary>Opens a service's library page in Bibliotaph's browser, with the section showing what importing it does.</summary>
    public abstract void OpenLibraryPage(IOnlineLibraryReader reader);
}
