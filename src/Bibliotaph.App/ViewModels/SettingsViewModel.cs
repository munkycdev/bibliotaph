using System.ComponentModel;
using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Settings, one page with a section list down its left and the chosen section filling the rest. Links into it open
/// a section (<see cref="SettingsLinks"/>), and Back returns to the section that was showing. About sits at the foot
/// of the list and opens the About popup rather than a section.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    readonly SettingsLinks _links;
    readonly AboutBox _about;
    readonly ILogger<SettingsViewModel> _log;
    bool _showing;

    public SettingsViewModel(
        SettingsLinks links, AboutBox about, LibrarySectionViewModel library, ProcessingSectionViewModel processing, AppearanceSectionViewModel appearance,
        ReviewSectionViewModel review, VocabularyViewModel vocabulary, AiSettingsViewModel ai, BackupSectionViewModel backup, StartOverSectionViewModel startOver,
        ILogger<SettingsViewModel> log)
    {
        _links = links;
        _about = about;
        _log = log;
        Sections = [library, processing, appearance, review, vocabulary, ai, backup, startOver];
        foreach (var section in Sections) section.PropertyChanged += OnSectionChanged;
        Selected = library;
        library.IsSelected = true;
    }

    public override Route Route => Route.Settings;
    public override string Title => "Settings";

    /// <summary>The section list stays put while the chosen section scrolls beside it.</summary>
    public override bool ScrollsItself => true;

    /// <summary>The section list, in order; Start over is last.</summary>
    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    /// <summary>The section filling the page.</summary>
    public SettingsSectionViewModel Selected { get; private set; }

    /// <summary>Chooses a section, as a link into Settings does. Choosing the one already showing loads it again.</summary>
    public void Show(SettingsSection section) => Select(Sections.First(s => s.Section == section));

    public override async Task LoadAsync()
    {
        if (_links.TakeRequest() is { } requested) Show(requested);
        _showing = true;
        await Selected.LoadAsync();
    }

    public override void Unload()
    {
        _showing = false;
        Selected.Unload();
    }

    /// <summary>A section chosen in the list.</summary>
    void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsSectionViewModel.IsSelected) && sender is SettingsSectionViewModel { IsSelected: true } section
            && !ReferenceEquals(section, Selected))
            Select(section);
    }

    void Select(SettingsSectionViewModel section)
    {
        var previous = Selected;
        Selected = section;
        foreach (var other in Sections) other.IsSelected = ReferenceEquals(other, section);
        OnPropertyChanged(nameof(Selected));
        if (!_showing) return;
        if (!ReferenceEquals(previous, section)) previous.Unload();
        _ = LoadSectionAsync(section);
    }

    async Task LoadSectionAsync(SettingsSectionViewModel section)
    {
        try
        {
            await section.LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading Settings > {Section} failed", section.Label);
        }
    }

    [RelayCommand]
    void ShowAbout() => _about.Show();
}
