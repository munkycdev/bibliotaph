using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Services;

/// <summary>Opens a session pack's page (slice 3 plan, choice 12), from the Sessions page, Home or a new pack, and run mode (choice 16).</summary>
public sealed class SessionPages(INavigationService navigation, Func<SessionPackViewModel> createPack, Func<RunSessionViewModel> createRun)
{
    /// <summary>The pack's page. Nothing happens when it is already the page shown.</summary>
    public void Open(long packId)
    {
        if (navigation.Current is SessionPackViewModel current && current.PackId == packId) return;
        var page = createPack();
        page.PackId = packId;
        navigation.Show(page);
    }

    /// <summary>Begin session: run mode for the pack, at its first item.</summary>
    public void Begin(long packId)
    {
        var page = createRun();
        page.PackId = packId;
        navigation.Show(page);
    }

    /// <summary>The Sessions page, as after a pack was deleted.</summary>
    public void OpenList() => navigation.NavigateUp(Route.Sessions);
}
