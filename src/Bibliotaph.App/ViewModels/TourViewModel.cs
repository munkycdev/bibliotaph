using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The first-run tour: callouts over the window's own controls, a chapter at a time. The Folders chapter opens on
/// Home before any folder is in and ends once the first is; the Books chapter opens the first time Home or the
/// Library shows books. Skipping ends the whole tour, and Settings > Preferences starts it again. How far it has gone
/// is the <see cref="SettingKeys.TourStage"/> setting, so it never comes back on its own.
/// </summary>
public sealed partial class TourViewModel(SourceRootStore roots, SettingsStore settings, LibraryFolders folders, INavigationService navigation,
    LibraryActivity activity, ILogger<TourViewModel> log) : ObservableObject
{
    IReadOnlyList<TourStep> _steps = [];
    int _index;
    bool _hooked;
    /// <summary>The setting as last read or written; Home asks on every refresh.</summary>
    string? _stage;
    bool _stageKnown;
    /// <summary>Set when Add a folder was pressed from the tour, until a folder is in and the chapter goes on.</summary>
    bool _awaitingFolder;
    /// <summary>The chapter Show the tour again asked for, until Home has loaded and starts it.</summary>
    TourChapter? _replay;

    /// <summary>The view redraws for a new step, or on opening.</summary>
    public event EventHandler? StepChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(StepText), nameof(NextText), nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool IsOpen { get; private set; }

    public TourStep? Current => IsOpen && _index < _steps.Count ? _steps[_index] : null;

    /// <summary>"Step 2 of 6".</summary>
    public string StepText => IsOpen ? $"Step {_index + 1} of {_steps.Count}" : "";

    /// <summary>What the primary button does: the step's own action, Done at the end, Next otherwise.</summary>
    public string NextText => Current?.Action ?? (_index >= _steps.Count - 1 ? "Done" : "Next");

    public bool CanGoBack => IsOpen && _index > 0;

    /// <summary>Home, with no folder in: the Folders chapter, unless the tour has been seen or skipped.</summary>
    public async Task OfferFoldersAsync()
    {
        if (IsOpen || _awaitingFolder) return;
        if (await StageAsync() is null) Start(TourChapter.Folders);
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

    /// <summary>Settings > Preferences > Show the tour again: on Home, the chapter the library is ready for.</summary>
    public async Task ReplayAsync()
    {
        _awaitingFolder = false;
        var chapter = (await roots.ListAsync()).Count > 0 ? TourChapter.Books : TourChapter.Folders;
        if (navigation.Current?.Route == Route.Home)
        {
            Start(chapter);
            return;
        }
        // Home starts it once its rows are in, so the first step finds the covers it points at however long they take.
        Hook();
        _replay = chapter;
        navigation.NavigateTo(Route.Home);
    }

    /// <summary>Home has loaded its rows: the chapter Show the tour again asked for, unless the page already opened one.</summary>
    public void HomeLoaded()
    {
        if (_replay is not { } chapter) return;
        _replay = null;
        if (!IsOpen) Start(chapter);
    }

    void Start(TourChapter chapter, int at = 0)
    {
        Hook();
        _steps = Tour.Steps(chapter, TourClient.Windows);
        _index = at;
        IsOpen = true;
        StepChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    async Task NextAsync()
    {
        if (Current is not { } step) return;
        if (step.Anchors.Contains(TourAnchor.AddFolder))
        {
            // The picker is in Settings > Library; the chapter goes on from the status line once a folder is in.
            _awaitingFolder = true;
            IsOpen = false;
            folders.RequestPick();
            return;
        }
        if (_index >= _steps.Count - 1)
        {
            await FinishAsync(step.Chapter == TourChapter.Folders ? Tour.StageFolders : Tour.StageBooks);
            return;
        }
        _index++;
        Show();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    void Back()
    {
        _index--;
        Show();
    }

    /// <summary>Skip tour, or Esc: the rest of the tour is never offered again.</summary>
    [RelayCommand]
    Task SkipAsync()
    {
        _awaitingFolder = false;
        return FinishAsync(Tour.StageBooks);
    }

    /// <summary>The view found none of the step's controls on screen, so the step is passed over.</summary>
    public Task PassOverAsync() => NextAsync();

    void Show()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(CanGoBack));
        BackCommand.NotifyCanExecuteChanged();
        StepChanged?.Invoke(this, EventArgs.Empty);
    }

    async Task FinishAsync(string stage)
    {
        IsOpen = false;
        _stage = stage;
        _stageKnown = true;
        try
        {
            await settings.SetAsync(SettingKeys.TourStage, stage);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Remembering the tour's stage failed");
        }
    }

    async Task<string?> StageAsync()
    {
        if (_stageKnown) return _stage;
        try
        {
            _stage = await settings.GetAsync(SettingKeys.TourStage);
            _stageKnown = true;
            return _stage;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reading the tour's stage failed");
            return Tour.StageBooks;
        }
    }

    void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        activity.Refreshed += OnActivityRefreshed;
        navigation.Navigated += OnNavigated;
    }

    /// <summary>Leaving Home during Add a folder's step, by whatever route, counts as going to add one.</summary>
    void OnNavigated(object? sender, EventArgs e)
    {
        if (navigation.Current?.Route != Route.Home) _replay = null;
        if (!IsOpen || Current is not { } step || !step.Anchors.Contains(TourAnchor.AddFolder) || navigation.Current?.Route == Route.Home) return;
        _awaitingFolder = true;
        IsOpen = false;
    }

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        if (!_awaitingFolder || IsOpen) return;
        try
        {
            if ((await roots.ListAsync()).Count == 0) return;
            _awaitingFolder = false;
            var steps = Tour.Steps(TourChapter.Folders, TourClient.Windows);
            Start(TourChapter.Folders, at: steps.Count - 1);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Going on with the tour after the first folder failed");
        }
    }
}
