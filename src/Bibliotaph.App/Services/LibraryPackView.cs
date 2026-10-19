using Bibliotaph.Catalog;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Whether the Library shows each pack as one card or each of its files on a card of its own, kept from one run to the
/// next. Browsing and searching share it: it is about what a card is, not about what is looked for.
/// </summary>
public sealed class LibraryPackView(SettingsStore settings, ILogger<LibraryPackView> log)
{
    const string Files = "files";

    const string Packs = "packs";

    public bool ShowFiles { get; private set; }

    /// <summary>Read once at start, so a Library page shows its cards the remembered way from its first query.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            ShowFiles = await settings.GetAsync(SettingKeys.LibraryPackView) == Files;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reading how the Library shows packs failed; it shows them as packs");
        }
    }

    public async Task RememberAsync(bool showFiles)
    {
        if (showFiles == ShowFiles) return;
        ShowFiles = showFiles;
        try
        {
            await settings.SetAsync(SettingKeys.LibraryPackView, showFiles ? Files : Packs);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving how the Library shows packs failed; it is kept until the app closes");
        }
    }
}
