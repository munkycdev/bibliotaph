using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A section of Settings: its entry in the section list, and what fills the page while it is chosen. Settings calls
/// <see cref="LoadAsync"/> and <see cref="Unload"/> as the shell calls a page's, so a section listens for updates
/// only while it shows.
/// </summary>
public abstract partial class SettingsSectionViewModel : ObservableObject
{
    public abstract SettingsSection Section { get; }

    /// <summary>The section's name in the list.</summary>
    public abstract string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Called each time the section is shown: when it is chosen, and when Settings shows with it chosen.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Called when another section is chosen or Settings is left.</summary>
    public virtual void Unload()
    {
    }
}

/// <summary>
/// Settings > Processing: indexing step by step with the time left, what each part is on now, pause and resume for
/// each lane, and why online-only files wait. The progress itself is <see cref="LibraryActivity"/>'s, which the
/// sidebar's status line also shows.
/// </summary>
public sealed partial class ProcessingSectionViewModel(SourceRootStore roots, LibraryActivity activity, SettingsLinks links) : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.Processing;
    public override string Label => "Processing";

    public LibraryActivity Activity { get; } = activity;

    /// <summary>With no folders there is nothing to process, and the section says where to add one.</summary>
    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    public override async Task LoadAsync() => HasFolders = (await roots.ListAsync()).Count > 0;

    [RelayCommand]
    void ToggleIndexing() => Activity.SetPaused(Lane.Index, !Activity.IndexPaused);

    [RelayCommand]
    void ToggleOcr() => Activity.SetPaused(Lane.Ocr, !Activity.OcrPaused);

    [RelayCommand]
    void ToggleAi() => Activity.SetPaused(Lane.Classify, !Activity.AiPaused);

    [RelayCommand]
    void OpenLibrary() => links.Open(SettingsSection.Library);
}

/// <summary>Settings > Appearance: light, dark, or as Windows is.</summary>
public sealed class AppearanceSectionViewModel(ThemeService theme) : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.Appearance;
    public override string Label => "Appearance";

    public bool UseSystem
    {
        get => theme.Preference == ThemePreference.System;
        set { if (value) _ = theme.SetPreferenceAsync(ThemePreference.System); }
    }

    public bool UseLight
    {
        get => theme.Preference == ThemePreference.Light;
        set { if (value) _ = theme.SetPreferenceAsync(ThemePreference.Light); }
    }

    public bool UseDark
    {
        get => theme.Preference == ThemePreference.Dark;
        set { if (value) _ = theme.SetPreferenceAsync(ThemePreference.Dark); }
    }

    /// <summary>The top bar's toggle changes the preference too, so the choice follows it while it shows.</summary>
    public override Task LoadAsync()
    {
        theme.Changed -= OnThemeChanged;
        theme.Changed += OnThemeChanged;
        OnThemeChanged(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public override void Unload() => theme.Changed -= OnThemeChanged;

    void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(UseSystem));
        OnPropertyChanged(nameof(UseLight));
        OnPropertyChanged(nameof(UseDark));
    }
}

/// <summary>Settings > Needs review: whether every unconfirmed suggestion waits there, or only those worth a second look.</summary>
public sealed partial class ReviewSectionViewModel(ReviewService review) : SettingsSectionViewModel
{
    bool _reviewAll;

    public override SettingsSection Section => SettingsSection.Review;
    public override string Label => "Needs review";

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
        await review.SetReviewAllAsync(reviewAll);
        ReviewNote = "";
    }

    public override async Task LoadAsync()
    {
        _reviewAll = await review.GetReviewAllAsync();
        OnPropertyChanged(nameof(ReviewWhenUnsure));
        OnPropertyChanged(nameof(ReviewEverything));
    }
}

/// <summary>Settings > Start over: a development aid, last in the list so it isn't chosen by accident, and removed before 1.0.</summary>
public sealed partial class StartOverSectionViewModel(StartOver startOver) : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.StartOver;
    public override string Label => "Start over";

    /// <summary>Deletes everything Bibliotaph stores and restarts, after asking.</summary>
    [RelayCommand]
    void StartOver()
    {
        if (Services.StartOver.Confirm()) startOver.Run();
    }
}
