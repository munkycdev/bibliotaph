using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Sessions: every session pack as a card, the upcoming ones by date, then those kept at hand during games by title,
/// then those without a date by when they were worked on, then Earlier ones (date passed), folded. A card opens the
/// pack's page; New session makes one and opens it.
/// </summary>
public sealed partial class SessionsViewModel(SessionActions actions, SessionPages sessionPages, LibraryPages pages, ILogger<SessionsViewModel> log)
    : PageViewModel
{
    public override Route Route => Route.Sessions;
    public override string Title => "Binders";

    public SessionActions Actions { get; } = actions;

    public ObservableCollection<SessionCardViewModel> Upcoming { get; } = [];

    /// <summary>Binders of rules and references, which never list as upcoming.</summary>
    public ObservableCollection<SessionCardViewModel> AtHand { get; } = [];

    public ObservableCollection<SessionCardViewModel> Undated { get; } = [];

    public ObservableCollection<SessionCardViewModel> Earlier { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpcoming), nameof(HasAtHand), nameof(HasUndated), nameof(HasEarlier), nameof(EarlierLabel), nameof(ShowEmpty))]
    public partial int Version { get; private set; }

    public bool HasUpcoming => Upcoming.Count > 0;

    public bool HasAtHand => AtHand.Count > 0;

    public bool HasUndated => Undated.Count > 0;

    public bool HasEarlier => Earlier.Count > 0;

    public bool HasSessions => HasUpcoming || HasAtHand || HasUndated || HasEarlier;

    public bool ShowEmpty => IsLoaded && !HasSessions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Earlier sessions show when asked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EarlierLabel))]
    public partial bool ShowEarlier { get; set; }

    public string EarlierLabel => $"{(ShowEarlier ? "Hide" : "Show")} earlier binders ({Earlier.Count})";

    int _version;

    public override async Task LoadAsync()
    {
        Unsubscribe();
        await Actions.Directory.LoadAsync();
        await RefreshAsync();
        Actions.Created += OnCreated;
        Actions.Directory.Changed += OnChanged;
    }

    public override void Unload()
    {
        Unsubscribe();
        Actions.Close();
    }

    void Unsubscribe()
    {
        Actions.Created -= OnCreated;
        Actions.Directory.Changed -= OnChanged;
    }

    async void OnChanged(object? sender, EventArgs e) => await RefreshAsync();

    void OnCreated(object? sender, SessionPackInfo created) => sessionPages.Open(created.Id);

    public async Task RefreshAsync()
    {
        var version = ++_version;
        try
        {
            var today = Actions.Directory.Today;
            var all = Actions.Directory.All;
            var cards = (await Actions.Directory.CardsAsync(all)).ToDictionary(c => c.Id);
            if (version != _version) return;
            var dated = all.Where(p => !p.KeepAtHand).ToList();
            Fill(Upcoming, dated.Where(p => p.Date >= today).OrderBy(p => p.Date).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase), cards);
            Fill(AtHand, all.Where(p => p.KeepAtHand).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase), cards);
            Fill(Undated, dated.Where(p => p.Date is null).OrderByDescending(p => p.TouchedUtc), cards);
            Fill(Earlier, dated.Where(p => p.Date < today).OrderByDescending(p => p.Date), cards);
            IsLoaded = true;
            Version++;
            OnPropertyChanged(nameof(HasSessions));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Refreshing Sessions failed");
        }
    }

    static void Fill(ObservableCollection<SessionCardViewModel> row, IEnumerable<SessionPackInfo> packs, Dictionary<long, SessionCardViewModel> cards)
    {
        row.Clear();
        foreach (var pack in packs) row.Add(cards[pack.Id]);
    }

    /// <summary>After a pack was deleted from its page: the note saying so, with Undo.</summary>
    public void ShowDeleted(SessionPackSnapshot deleted) => Actions.ShowDeleted(deleted);

    [RelayCommand]
    void NewSession() => Actions.Create();

    [RelayCommand]
    void OpenSession(SessionCardViewModel card) => sessionPages.Open(card.Id);

    [RelayCommand]
    void ToggleEarlier() => ShowEarlier = !ShowEarlier;

    [RelayCommand]
    void Escape() => Actions.Dialog?.CancelCommand.Execute(null);

    [RelayCommand]
    void BrowseLibrary() => pages.Open(null);
}
