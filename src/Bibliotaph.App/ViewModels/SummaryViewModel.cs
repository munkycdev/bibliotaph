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
/// Summary: the library by the numbers. How much there is, where it came from, what it is and what has been done with
/// it, each figure opening the Library at the books it counts. The page itself is Core's <see cref="SummaryPage"/>, as
/// the web draws it too.
/// </summary>
public sealed partial class SummaryViewModel(LibrarySummaryService summaries, SourceRootStore roots, LibraryActivity activity, LibraryPages pages,
    INavigationService navigation, LibraryFolders folders, SettingsLinks settings, WebShelfCache shelf, ILogger<SummaryViewModel> log)
    : LibraryAwarePageViewModel(roots, activity)
{
    /// <summary>The height of the tallest column in a strip.</summary>
    public const double StripHeight = 72;

    int _version;

    public override Route Route => Route.Summary;
    public override string Title => "Summary";

    public static SummaryLink Everything => SummaryPage.Everything;

    public static SummaryLink NeverOpened => SummaryPage.NeverOpened;

    /// <summary>The figures, once read; null until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasBooks))]
    public partial SummaryPage? Page { get; private set; }

    public bool HasBooks => Page is { HasBooks: true };

    public bool IsEmpty => Page is { HasBooks: false };

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        Activity.Refreshed -= OnActivityRefreshed;
        Activity.Refreshed += OnActivityRefreshed;
        await RefreshAsync();
    }

    public override void Unload() => Activity.Refreshed -= OnActivityRefreshed;

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        await base.LoadAsync();
        await RefreshAsync();
    }

    /// <summary>A figure: the Library at the books it counts, or the page it names.</summary>
    [RelayCommand]
    void Open(SummaryLink? link)
    {
        switch (link)
        {
            case null:
                return;
            case { Goal: SummaryGoal.Collections }:
                navigation.NavigateTo(Route.Collections);
                return;
            case { Goal: SummaryGoal.Binders }:
                navigation.NavigateTo(Route.Sessions);
                return;
            default:
                pages.OpenFiltered(link.Query, Filters(link.Filter));
                return;
        }
    }

    [RelayCommand]
    void ManageFolders() => folders.Manage();

    [RelayCommand]
    void ImportMore() => settings.Open(SettingsSection.OnlineLibraries);

    public async Task RefreshAsync()
    {
        var version = ++_version;
        try
        {
            var summary = await Task.Run(() => summaries.GetAsync());
            if (version != _version) return;
            Page = SummaryPage.Of(summary, f => (shelf.NameOf(f.Path), shelf.Show(f.Path)));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the library summary failed");
        }
    }

    /// <summary>A figure's filters, as the Library's filter panel has them.</summary>
    public static SmartViewDefinition? Filters(SummaryFilter? filter)
    {
        if (filter is null) return null;
        string? Value(string field) => filter.Field == field ? filter.Value : null;
        return new SmartViewDefinition(
            Kind: Enum.TryParse<KindFilter>(filter.Kind, out var kind) ? kind : KindFilter.All,
            Folder: filter.Folder,
            System: Value("system"),
            Type: Value("type"),
            Level: filter.Level,
            Ai: filter.ReadByAi ? AiFilter.Read : AiFilter.All,
            Copies: filter.Copies,
            Own: Value("own"),
            Store: Value("store"),
            Picked: Value("setting") is { } setting ? PickedValues.Of([KeyValuePair.Create("setting", (IReadOnlyList<string>)[setting])]) : null,
            Adventures: filter.AdventureCards ? AdventureCards.Cards : AdventureCards.InBooks);
    }
}
