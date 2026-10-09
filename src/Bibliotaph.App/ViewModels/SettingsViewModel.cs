using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    readonly ThemeService _theme;
    readonly SourceRootStore _roots;
    readonly INavigationService _navigation;
    readonly ReviewService _review;
    readonly AboutBox _about;
    readonly AiService _ai;
    bool _reviewAll;

    public SettingsViewModel(ThemeService theme, SourceRootStore roots, INavigationService navigation, ReviewService review, AboutBox about,
        AiService ai)
    {
        _ai = ai;
        _theme = theme;
        _roots = roots;
        _navigation = navigation;
        _review = review;
        _about = about;
        _theme.Changed += (_, _) => OnAppearanceChanged();
    }

    public override Route Route => Route.Settings;
    public override string Title => "Settings";

    public bool UseSystem
    {
        get => _theme.Preference == ThemePreference.System;
        set { if (value) _ = _theme.SetPreferenceAsync(ThemePreference.System); }
    }

    public bool UseLight
    {
        get => _theme.Preference == ThemePreference.Light;
        set { if (value) _ = _theme.SetPreferenceAsync(ThemePreference.Light); }
    }

    public bool UseDark
    {
        get => _theme.Preference == ThemePreference.Dark;
        set { if (value) _ = _theme.SetPreferenceAsync(ThemePreference.Dark); }
    }

    [ObservableProperty]
    public partial string FolderSummary { get; set; } = "";

    /// <summary>"Off", or the model and where it runs.</summary>
    [ObservableProperty]
    public partial string AiSummary { get; set; } = "Off";

    /// <summary>Needs review gets only what sources disagree about, and missing titles (the default).</summary>
    public bool ReviewWhenUnsure
    {
        get => !_reviewAll;
        set { if (value) _ = SetReviewAllAsync(false); }
    }

    /// <summary>Needs review gets every suggestion nobody has confirmed (choice 4).</summary>
    public bool ReviewEverything
    {
        get => _reviewAll;
        set { if (value) _ = SetReviewAllAsync(true); }
    }

    /// <summary>"Counting…" while every book's cards are counted again.</summary>
    [ObservableProperty]
    public partial string ReviewNote { get; set; } = "";

    async Task SetReviewAllAsync(bool reviewAll)
    {
        if (_reviewAll == reviewAll) return;
        _reviewAll = reviewAll;
        OnPropertyChanged(nameof(ReviewWhenUnsure));
        OnPropertyChanged(nameof(ReviewEverything));
        ReviewNote = "Counting what needs review…";
        await _review.SetReviewAllAsync(reviewAll);
        ReviewNote = "";
    }

    public override async Task LoadAsync()
    {
        _reviewAll = await _review.GetReviewAllAsync();
        OnPropertyChanged(nameof(ReviewWhenUnsure));
        OnPropertyChanged(nameof(ReviewEverything));
        AiSummary = DescribeAi(_ai.Setup);
        var count = (await _roots.ListAsync()).Count;
        FolderSummary = count switch
        {
            0 => "No folders yet",
            1 => "One folder",
            _ => $"{count} folders",
        };
    }

    [RelayCommand]
    void ManageFolders() => _navigation.NavigateTo(Route.LibraryFolders);

    [RelayCommand]
    void ManageVocabulary() => _navigation.NavigateTo(Route.Vocabulary);

    [RelayCommand]
    void ManageAi() => _navigation.NavigateTo(Route.Ai);

    internal static string DescribeAi(AiSetup setup) => setup switch
    {
        { Endpoint: null } => "Off. Bibliotaph works fully without it.",
        { IsReady: false } => $"Off · {setup.Model ?? "no model chosen"}",
        _ => $"On · {setup.Model} on {(setup.IsLocal ? "this computer" : Uri.TryCreate(setup.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : setup.Endpoint)}",
    };

    void OnAppearanceChanged()
    {
        OnPropertyChanged(nameof(UseSystem));
        OnPropertyChanged(nameof(UseLight));
        OnPropertyChanged(nameof(UseDark));
    }

    [RelayCommand]
    void ShowAbout() => _about.Show();
}
