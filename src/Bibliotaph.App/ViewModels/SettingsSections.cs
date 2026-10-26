using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

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
/// sidebar's status line also shows. Also whether files indexing downloaded go back to online-only.
/// </summary>
public sealed partial class ProcessingSectionViewModel(SourceRootStore roots, LibraryActivity activity, SettingsLinks links, OnlineOnlyReturns returns,
    ILogger<ProcessingSectionViewModel> log) : SettingsSectionViewModel
{
    bool _loading;

    public override SettingsSection Section => SettingsSection.Processing;
    public override string Label => "Processing";

    public LibraryActivity Activity { get; } = activity;

    /// <summary>With no folders there is nothing to process, and the section says where to add one.</summary>
    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    /// <summary>"Return files to online-only after indexing": on unless the user turned it off.</summary>
    [ObservableProperty]
    public partial bool ReturnOnlineOnly { get; set; } = true;

    public override async Task LoadAsync()
    {
        _loading = true;
        try
        {
            ReturnOnlineOnly = await returns.IsOnAsync();
        }
        finally
        {
            _loading = false;
        }
        HasFolders = (await roots.ListAsync()).Count > 0;
    }

    async partial void OnReturnOnlineOnlyChanged(bool value)
    {
        if (_loading) return;
        try
        {
            await returns.SetOnAsync(value);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving whether files go back to online-only failed");
        }
    }

    [RelayCommand]
    void ToggleIndexing() => Activity.SetPaused(Lane.Index, !Activity.IndexPaused);

    [RelayCommand]
    void ToggleOcr() => Activity.SetPaused(Lane.Ocr, !Activity.OcrPaused);

    [RelayCommand]
    void ToggleAi() => Activity.SetPaused(Lane.Classify, !Activity.AiPaused);

    [RelayCommand]
    void OpenLibrary() => links.Open(SettingsSection.Library);

    [RelayCommand]
    void OpenAiReading() => links.OpenAiReading();

    [RelayCommand]
    void OpenFilesNeedingAttention() => links.OpenFilesNeedingAttention();
}

/// <summary>
/// Settings > Preferences: light, dark or as Windows is; whether Bibliotaph opens on Home or the Library; and whether
/// every unconfirmed suggestion waits in Needs review, or only those worth a second look.
/// </summary>
public sealed partial class PreferencesSectionViewModel(ThemeService theme, SettingsStore settings, ReviewService review,
    ILogger<PreferencesSectionViewModel> log) : SettingsSectionViewModel
{
    bool _loading;
    bool _reviewAll;

    public override SettingsSection Section => SettingsSection.Preferences;
    public override string Label => "Preferences";

    public bool UseSystem
    {
        get => theme.Preference == ThemePreference.System;
        set { if (value) theme.SetPreferenceAsync(ThemePreference.System).FireAndForget(); }
    }

    public bool UseLight
    {
        get => theme.Preference == ThemePreference.Light;
        set { if (value) theme.SetPreferenceAsync(ThemePreference.Light).FireAndForget(); }
    }

    public bool UseDark
    {
        get => theme.Preference == ThemePreference.Dark;
        set { if (value) theme.SetPreferenceAsync(ThemePreference.Dark).FireAndForget(); }
    }

    /// <summary>While a Windows contrast theme is on, the system's colours show whatever is chosen here.</summary>
    public bool IsHighContrast => theme.IsHighContrast;

    /// <summary>Start on: the Library rather than Home when Bibliotaph opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartOnHome))]
    public partial bool StartOnLibrary { get; set; }

    public bool StartOnHome
    {
        get => !StartOnLibrary;
        set { if (value) StartOnLibrary = false; }
    }

    /// <summary>Needs review gets only what sources disagree about, and missing titles (the default).</summary>
    public bool ReviewWhenUnsure
    {
        get => !_reviewAll;
        set { if (value) SetReviewAllAsync(false).FireAndForget(); }
    }

    /// <summary>Needs review gets every suggestion nobody has confirmed.</summary>
    public bool ReviewEverything
    {
        get => _reviewAll;
        set { if (value) SetReviewAllAsync(true).FireAndForget(); }
    }

    /// <summary>"Counting…" while every book's cards are counted again.</summary>
    [ObservableProperty]
    public partial string ReviewNote { get; set; } = "";

    /// <summary>The top bar's toggle changes the theme too, so the choice follows it while the section shows.</summary>
    public override async Task LoadAsync()
    {
        theme.Changed -= OnThemeChanged;
        theme.Changed += OnThemeChanged;
        OnThemeChanged(this, EventArgs.Empty);
        _loading = true;
        try
        {
            StartOnLibrary = await settings.GetAsync(SettingKeys.StartPage) == nameof(Route.Library);
        }
        finally
        {
            _loading = false;
        }
        _reviewAll = await review.GetReviewAllAsync();
        OnPropertyChanged(nameof(ReviewWhenUnsure));
        OnPropertyChanged(nameof(ReviewEverything));
    }

    public override void Unload() => theme.Changed -= OnThemeChanged;

    void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(UseSystem));
        OnPropertyChanged(nameof(UseLight));
        OnPropertyChanged(nameof(UseDark));
        OnPropertyChanged(nameof(IsHighContrast));
    }

    async partial void OnStartOnLibraryChanged(bool value)
    {
        if (_loading) return;
        try
        {
            await settings.SetAsync(SettingKeys.StartPage, value ? nameof(Route.Library) : nameof(Route.Home));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving the start page failed");
        }
    }

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
}

/// <summary>Settings > Start over: a development aid, shown only with --dev, and last in the list so it isn't chosen by accident.</summary>
public sealed partial class StartOverSectionViewModel(StartOver startOver, IUserPrompts prompts) : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.StartOver;
    public override string Label => "Start over";

    /// <summary>Deletes everything Bibliotaph stores and restarts, after asking.</summary>
    [RelayCommand]
    void StartOver()
    {
        if (Services.StartOver.Confirm(prompts)) startOver.Run();
    }
}

/// <summary>A book with a remembered password, in Settings, Passwords. Only its hash and title: never the password.</summary>
public sealed record PasswordRow(string ContentHash, string Title);

/// <summary>
/// Settings > Passwords: the books whose password was remembered in Windows Credential Manager, each with Forget, and
/// Forget all. Forgetting one also forgets the copy kept for this sitting, so the next open asks again; text already
/// read stays searchable until Forget its text.
/// </summary>
public sealed partial class PasswordsSectionViewModel(IPasswordVault vault, UnlockedPasswords unlocked, IndexQueries queries,
    IUserPrompts prompts, ILogger<PasswordsSectionViewModel> log) : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.Passwords;
    public override string Label => "Passwords";

    public ObservableCollection<PasswordRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    public override async Task LoadAsync()
    {
        try
        {
            var hashes = await Task.Run(vault.List);
            var titles = await Task.Run(() => queries.GetTitlesByHashAsync(hashes));
            Rows.Clear();
            foreach (var row in hashes
                .Select(h => new PasswordRow(h, titles.TryGetValue(h, out var title) ? title : "A book not in your library now"))
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
                Rows.Add(row);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Listing remembered passwords failed");
        }
        HasRows = Rows.Count > 0;
        IsLoaded = true;
    }

    [RelayCommand]
    void Forget(PasswordRow row)
    {
        vault.Forget(row.ContentHash);
        unlocked.Forget(row.ContentHash);
        Rows.Remove(row);
        HasRows = Rows.Count > 0;
    }

    [RelayCommand]
    void ForgetAll()
    {
        if (Rows.Count == 0) return;
        var confirmed = prompts.Confirm(
            $"Bibliotaph will forget {(Rows.Count == 1 ? "the remembered password" : $"all {Rows.Count:N0} remembered passwords")} and ask again the next time you open {(Rows.Count == 1 ? "the book" : "each book")}.\n\nYour files are not touched.",
            "Forget all passwords?", PromptKind.Question, okByDefault: false);
        if (!confirmed) return;
        var count = vault.ForgetAll();
        foreach (var row in Rows) unlocked.Forget(row.ContentHash);
        log.LogInformation("Forgot {Count} remembered passwords", count);
        Rows.Clear();
        HasRows = false;
    }
}
