using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Collections (slice 3 plan, choice 9): a card for each collection at the top level, pinned first, then by name. A
/// card opens the Library scoped to it, where its sub-collections and its actions are; New collection makes one here.
/// </summary>
public sealed partial class CollectionsViewModel(CollectionActions actions, LibraryPages pages, LibraryActivity activity, ILogger<CollectionsViewModel> log)
    : PageViewModel
{
    public override Route Route => Route.Collections;
    public override string Title => "Collections";

    public CollectionActions Actions { get; } = actions;

    public ObservableCollection<CollectionCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    public partial bool HasCollections { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    int _version;

    public override async Task LoadAsync()
    {
        Unsubscribe();
        await Actions.Directory.LoadAsync();
        await RefreshAsync();
        // Every change reloads the directory, so its Changed covers this page's own changes too.
        Actions.Created += OnCreated;
        Actions.Directory.Changed += OnChanged;
        activity.Refreshed += OnChanged;
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
        activity.Refreshed -= OnChanged;
    }

    async void OnChanged(object? sender, EventArgs e) => await RefreshAsync();

    /// <summary>A new collection opens at once, ready for books.</summary>
    void OnCreated(object? sender, CollectionInfo created) => pages.Open(Actions.Directory.ScopeFor(created));

    public async Task RefreshAsync()
    {
        var version = ++_version;
        try
        {
            var cards = await Actions.Directory.CardsAsync(Actions.Directory.ChildrenOf(null));
            if (version != _version) return;
            Cards.Clear();
            foreach (var card in cards) Cards.Add(card);
            HasCollections = Cards.Count > 0;
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Refreshing Collections failed");
        }
    }

    /// <summary>After a collection was deleted from its page: the note saying so, with Undo.</summary>
    public void ShowDeleted(CollectionDeletion deleted) => Actions.ShowDeleted(deleted);

    [RelayCommand]
    void NewCollection() => Actions.Create(null);

    [RelayCommand]
    void OpenCollection(CollectionCardViewModel card) => pages.Open(Actions.Directory.ScopeFor(card.Collection));

    [RelayCommand]
    void Escape() => Actions.Dialog?.CancelCommand.Execute(null);

    [RelayCommand]
    void BrowseLibrary() => pages.Open(null);
}
