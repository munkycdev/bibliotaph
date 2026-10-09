using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    readonly ThemeService _theme;
    readonly SourceRootStore _roots;
    readonly INavigationService _navigation;
    readonly AboutBox _about;

    public SettingsViewModel(ThemeService theme, SourceRootStore roots, INavigationService navigation, AboutBox about)
    {
        _theme = theme;
        _roots = roots;
        _navigation = navigation;
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

    public override async Task LoadAsync()
    {
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

    void OnAppearanceChanged()
    {
        OnPropertyChanged(nameof(UseSystem));
        OnPropertyChanged(nameof(UseLight));
        OnPropertyChanged(nameof(UseDark));
    }

    [RelayCommand]
    void ShowAbout() => _about.Show();
}
