using Bibliotaph.Core;
using Microsoft.AspNetCore.Components;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The first-run tour, as the Windows app's: callouts over the page's own controls, a chapter at a time. The Folders
/// chapter starts on Home before any folder is in, goes on in Settings › Library (connect a storage, pick a folder:
/// whichever the account still needs, so it carries on after a provider's sign-in brings the page back) and ends on
/// the status line once the first folder is in. The Books chapter opens the first time Home or the Library shows
/// books. Skipping ends the whole tour; Settings › Preferences starts it again. How far it has gone is the library's
/// <c>tour.stage</c> setting, so it never comes back on its own, and the Windows app sees the same.
/// </summary>
public sealed class TourGuide(ApiClient api, NavigationManager navigation)
{
    const int StorageStep = 1, FolderStep = 2, StatusStep = 3;

    IReadOnlyList<TourStep> _steps = [];
    int _index;
    string? _stage;
    bool _stageKnown;

    public bool IsOpen { get; private set; }

    /// <summary>The overlay redraws for a new step, or on opening and closing.</summary>
    public event Action? Changed;

    public TourStep? Current => IsOpen && _index < _steps.Count ? _steps[_index] : null;

    /// <summary>"Step 2 of 7".</summary>
    public string StepText => IsOpen ? $"Step {_index + 1} of {_steps.Count}" : "";

    /// <summary>What the primary button does: the step's own action, Done at the end, Next otherwise.</summary>
    public string NextText => Current?.Action ?? (_index >= _steps.Count - 1 ? "Done" : "Next");

    public bool CanGoBack => IsOpen && _index > 0;

    /// <summary>Home, with no folder in: the Folders chapter, unless the tour has been seen or skipped.</summary>
    public async Task OfferFoldersAsync()
    {
        if (IsOpen) return;
        if (await StageAsync() is null) Start(TourChapter.Folders);
    }

    /// <summary>
    /// Settings › Library, during the Folders chapter: the step the account needs next, whether the tour came here by
    /// its own Add a folder or the page was opened afresh after a storage provider's sign-in.
    /// </summary>
    public async Task OfferSetupAsync(bool hasStorage, bool hasFolders)
    {
        if (IsOpen && Current?.Chapter != TourChapter.Folders) return;
        if (!IsOpen && await StageAsync() is not null) return;
        var at = hasFolders ? StatusStep : hasStorage ? FolderStep : StorageStep;
        if (IsOpen && _index == at) return;
        Start(TourChapter.Folders, at);
    }

    /// <summary>The first folder is in: the chapter's last step, the status line.</summary>
    public async Task FolderAddedAsync()
    {
        if (await StageAsync() is not null) return;
        Start(TourChapter.Folders, StatusStep);
    }

    /// <summary>
    /// Home or the Library, with books in: the Books chapter, unless the whole tour has been seen or skipped. A library
    /// from before the tour existed gets this chapter once too.
    /// </summary>
    public async Task OfferBooksAsync()
    {
        if (IsOpen) return;
        if (await StageAsync() != Tour.StageBooks) Start(TourChapter.Books);
    }

    /// <summary>Settings › Preferences › Show the tour again: on Home, the chapter the library is ready for.</summary>
    public async Task ReplayAsync()
    {
        var summary = await api.LibrarySummaryAsync();
        navigation.NavigateTo("home");
        Start(summary.Folders > 0 ? TourChapter.Books : TourChapter.Folders);
    }

    public async Task NextAsync()
    {
        if (Current is not { } step) return;
        if (step.Anchors.Contains(TourAnchor.AddFolder))
        {
            // Settings › Library says which step comes next from what the account has.
            _index = StorageStep;
            navigation.NavigateTo("settings#library");
            Changed?.Invoke();
            return;
        }
        if (_index >= _steps.Count - 1)
        {
            await FinishAsync(step.Chapter == TourChapter.Folders ? Tour.StageFolders : Tour.StageBooks);
            return;
        }
        _index++;
        Changed?.Invoke();
    }

    public void Back()
    {
        if (!CanGoBack) return;
        _index--;
        Changed?.Invoke();
    }

    /// <summary>Skip tour, or Esc: the rest of the tour is never offered again.</summary>
    public Task SkipAsync() => FinishAsync(Tour.StageBooks);

    /// <summary>The overlay found none of the step's controls on the page, so the step is passed over.</summary>
    public Task PassOverAsync() => NextAsync();

    void Start(TourChapter chapter, int at = 0)
    {
        _steps = Tour.Steps(chapter, TourClient.Web);
        _index = Math.Min(at, _steps.Count - 1);
        IsOpen = true;
        Changed?.Invoke();
    }

    async Task FinishAsync(string stage)
    {
        IsOpen = false;
        _stage = stage;
        _stageKnown = true;
        Changed?.Invoke();
        try
        {
            await api.SetTourProgressAsync(stage);
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            // Remembered on this page; it may come back on the next.
        }
    }

    async Task<string?> StageAsync()
    {
        if (_stageKnown) return _stage;
        try
        {
            _stage = (await api.TourProgressAsync()).Stage;
            _stageKnown = true;
            return _stage;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            return Tour.StageBooks;
        }
    }
}
